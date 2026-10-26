using MiniMcp.Services.Patching;
using MiniMcp.Services.Workspace;
using System.Text;
using System.Text.Json;
using MiniMcp.Configuration;
using MiniMcp.Recovery;
using MiniMcp.Tools;
using ModelContextProtocol;
using static PatchExecutionFixture;

internal static class RecordedToolExecutionChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("shared patch execution previews without storage and records only allowed ordinary writes in all three modes", async () =>
        {
            foreach (var mode in Enum.GetValues<ExecutionMode>())
            {
                using var fixture = new PatchExecutionFixture(mode);
                await using var execution = fixture.Execution();
                var original = fixture.ProjectSnapshot();
                var preview = await execution.ApplyPatchAsync(Patch("*** Add File: nested/new.md\n+new\n*** Update File: sample.md\n@@\n-old\n+changed"), true);
                Assert(preview.DryRun && preview.Commit is null && preview.Decision.CanCommit == (mode != ExecutionMode.ReadOnly) &&
                    original.SequenceEqual(fixture.ProjectSnapshot()) && !Directory.Exists(fixture.RecoveryDirectory), "Eligibility preview neither preflights nor creates backups/workspace directories");
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                await Reject<OperationCanceledException>(() => execution.ApplyPatchAsync(Patch("*** Add File: cancelled/new.md\n+new"), cancellationToken: cancelled.Token));
                Assert(!Directory.Exists(fixture.RecoveryDirectory) && !Directory.Exists(fixture.PathOf("cancelled")), "Pre-cancelled actual writes create no record or workspace state");
                if (mode == ExecutionMode.ReadOnly)
                {
                    await Reject<WorkspaceException>(() => execution.ApplyPatchAsync(Patch("*** Add File: nested/new.md\n+new")));
                    Assert(!Directory.Exists(fixture.RecoveryDirectory) && original.SequenceEqual(fixture.ProjectSnapshot()), "ReadOnly fails before record preparation");
                    continue;
                }
                var result = await execution.ApplyPatchAsync(Patch("*** Add File: nested/new.md\n+new\n*** Update File: sample.md\n@@\n-old\n+changed"));
                var saved = await fixture.Load(result);
                Assert(result.Commit!.RunId == execution.RunId && result.Decision.RequiresRecoveryRecord && !result.Commit.CleanupIncomplete &&
                    fixture.Get("sample.md") == "changed\n" && fixture.Get("nested/new.md") == "new\n" &&
                    Encoding.UTF8.GetString(saved.Backups.Single().Bytes.AsSpan()) == "old\n" && execution.ActiveCalls == 0, "Actual installed contents, original backup and session identity agree");
            }
        });

        await check("ProjectEdit rejects every high-risk mixed batch before preparing records or executing its ordinary member", async () =>
        {
            var cases = new (string Body, ProjectEditLimits Limits)[]
            {
                ("*** Add File: Directory.Build.cs\n+build", new()), ("*** Add File: unknown.data\n+data", new()),
                ("*** Delete File: sample.md", new()), ("*** Update File: sample.md\n*** Move to: moved.md", new()),
                ("*** Add File: second.md", new() { MaxPaths = 1 }),
                ("*** Add File: second.md\n+new", new() { MaxChangedLines = 0 }),
                ("*** Add File: second.md\n+new", new() { MaxChangedBytes = 0 })
            };
            foreach (var (body, limits) in cases)
            {
                using var fixture = new PatchExecutionFixture(ExecutionMode.ProjectEdit, limits);
                var preparations = 0;
                await using var execution = fixture.Execution(prepare: (_, _) => { preparations++; return Task.CompletedTask; });
                var original = fixture.ProjectSnapshot();
                var patch = Patch("*** Add File: nested/ordinary.md\n+ordinary\n" + body);
                var preview = await execution.ApplyPatchAsync(patch, true);
                Assert(preview.Decision.Disposition == "Denied" && preview.Decision.Blockers.SequenceEqual(["ProjectEditRestriction"]), "Entire batch uses the highest risk");
                await Reject<WorkspaceException>(() => execution.ApplyPatchAsync(patch));
                Assert(preparations == 0 && !Directory.Exists(fixture.RecoveryDirectory) && original.SequenceEqual(fixture.ProjectSnapshot()), "No ordinary subset, backup or approval placeholder executes");
            }
        });

        await check("FullAccess records mixed high-risk writes even with zero approval limits and retains exact existing local bytes", async () =>
        {
            using var fixture = new PatchExecutionFixture(ExecutionMode.FullAccess, new() { MaxPaths = 0, MaxChangedLines = 0, MaxChangedBytes = 0 });
            fixture.Put("sample.md", "\ufeffold\r\nlocal edit\r\n");
            fixture.Put("delete.md", ""); fixture.Put("move.md", "move\n");
            await using var execution = fixture.Execution();
            var result = await execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new\n*** Delete File: delete.md\n" +
                "*** Update File: move.md\n*** Move to: nested/moved.md\n*** Add File: unknown.data\n+data\n*** Add File: Directory.Build.cs\n+build"));
            var saved = await fixture.Load(result);
            Assert(result.Decision.CanCommit && result.Risk.Reasons.Contains("Move") && result.Risk.Reasons.Contains("CriticalFile") &&
                saved.Backups[0].Bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("\ufeffold\r\nlocal edit\r\n")) && saved.Backups[1].Bytes.IsEmpty &&
                fixture.Get("sample.md") == "new\r\nlocal edit\r\n" && fixture.Get("nested/moved.md") == "move\n" && !File.Exists(fixture.PathOf("delete.md")), "FullAccess skips approval while preserving real recovery for the complete batch");
        });

        await check("shared execution never falls back to unrecorded writes when backup IO fails at any preparation checkpoint", async () =>
        {
            foreach (var boundary in Enum.GetValues<RecoveryPreparationStage>())
            {
                using var fixture = new PatchExecutionFixture();
                var installs = 0;
                var failure = new IOException("Injected original backup failure.");
                await using var execution = fixture.Execution((_, _) => { installs++; return Task.CompletedTask; },
                    prepare: (stage, _) => stage == boundary ? Task.FromException(failure) : Task.CompletedTask);
                var before = fixture.ProjectSnapshot();
                var error = await Reject<IOException>(() => execution.ApplyPatchAsync(Patch("*** Add File: nested/new.md\n+new\n*** Update File: sample.md\n@@\n-old\n+new")));
                Assert(ReferenceEquals(error, failure) && installs == 0 && before.SequenceEqual(fixture.ProjectSnapshot()) &&
                    fixture.RecordIds().Length == 0 && execution.ActiveCalls == 0, "Backup failure precedes every workspace write and call tracking drains");
            }
        });

        await check("preview eligibility does not preflight storage and an unusable recovery location cannot authorize a write", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            await using var execution = fixture.Execution();
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.RecoveryDirectory)!);
            File.WriteAllText(fixture.RecoveryDirectory, "foreign occupant");
            var patch = Patch("*** Add File: nested/new.md\n+new");
            var original = fixture.ProjectSnapshot();
            var preview = await execution.ApplyPatchAsync(patch, true);
            Assert(preview.Decision.CanCommit && preview.Commit is null && File.ReadAllText(fixture.RecoveryDirectory) == "foreign occupant", "Pure eligibility does not promise storage readiness or change foreign data");
            await Reject<InvalidDataException>(() => execution.ApplyPatchAsync(patch));
            Assert(original.SequenceEqual(fixture.ProjectSnapshot()) && File.ReadAllText(fixture.RecoveryDirectory) == "foreign occupant", "No unrecorded fallback when actual preparation rejects storage");
        });

        await check("shared patch entry refuses a full recovery store before any workspace staging", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            for (var index = 0; index < 1000; index++) Directory.CreateDirectory(Path.Combine(fixture.RecoveryDirectory, $"foreign-{index}"));
            await using var execution = fixture.Execution();
            var before = fixture.ProjectSnapshot();
            await Reject<McpException>(() => new WorkspaceTools(execution).ApplyPatch(Patch("*** Add File: nested/new.md\n+new"), CancellationToken.None));
            Assert(before.SequenceEqual(fixture.ProjectSnapshot()) && fixture.RecordIds().Length == 0 &&
                Directory.EnumerateDirectories(fixture.RecoveryDirectory).Count() == 1000, "Quota failure never discards older data or skips recovery");
        });

        await check("shared execution preserves successful committed results under late caller cancellation at engine and final-save boundaries", async () =>
        {
            foreach (var boundary in new[] { "BeforeInstall", "Installed", "BeforeVerification", "FinalSave" })
            {
                using var fixture = new PatchExecutionFixture();
                using var cancelled = new CancellationTokenSource();
                await using var execution = fixture.Execution((stage, _) => { if (stage.ToString() == boundary) cancelled.Cancel(); return Task.CompletedTask; },
                    replace: (stage, _) =>
                    {
                        if (boundary == "FinalSave" && stage == RecoveryManifestReplacementStage.BeforeReturn &&
                            RecoveryManifestJson.Parse(File.ReadAllBytes(Path.Combine(fixture.RecoveryDirectory, fixture.RecordIds().Single().ToString("N"), RecoveryStore.ManifestFileName))).State == RecoveryRecordState.Committed)
                            cancelled.Cancel();
                        return Task.CompletedTask;
                    });
                var result = await execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new\n*** Add File: nested/new.md\n+new"), cancellationToken: cancelled.Token);
                await fixture.Load(result);
                Assert(cancelled.IsCancellationRequested && result.Commit!.State == "Committed" && execution.ActiveCalls == 0 && fixture.Get("sample.md") == "new\n", "Execution wrapper cannot replace real finalized success with cancellation");
            }
        });

        await check("shared execution persists cancelled staged work as verified rollback before reporting its actual failure", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            using var cancelled = new CancellationTokenSource();
            await using var execution = fixture.Execution((stage, _) => { if (stage == PatchCommitStage.Staged) cancelled.Cancel(); return Task.CompletedTask; });
            var before = fixture.ProjectSnapshot();
            var error = await Reject<RecordedPatchExecutionException>(() => execution.ApplyPatchAsync(Patch("*** Add File: nested/new.md\n+new\n*** Update File: sample.md\n@@\n-old\n+new"), cancellationToken: cancelled.Token));
            Assert(error.Commit.Outcome.Failure is OperationCanceledException && error.Commit.Outcome.VerifiedState == PatchCommitState.RolledBack &&
                (await fixture.Load(error.Commit.Record.Manifest.ChangeId)).Manifest.State == RecoveryRecordState.RolledBack &&
                before.SequenceEqual(fixture.ProjectSnapshot()) && execution.ActiveCalls == 0, "Cancellation is retained as engine failure after real cleanup and finalization");
        });

        await check("tool adapter reports actual rollback, incomplete rollback and post-install verification failure without exposing raw errors", async () =>
        {
            foreach (var scenario in new[] { "rollback", "incomplete", "verification" })
            {
                using var fixture = new PatchExecutionFixture();
                const string secret = "DO_NOT_EXPOSE_RAW_ENGINE_ERROR";
                await using var execution = fixture.Execution((stage, _) =>
                {
                    if (scenario == "verification" && stage == PatchCommitStage.BeforeVerification) fixture.Put("sample.md", "external\n");
                    if (scenario != "verification" && stage == PatchCommitStage.Installed)
                    {
                        if (scenario == "incomplete") { File.Delete(fixture.PathOf("sample.md")); Directory.CreateDirectory(fixture.PathOf("sample.md")); }
                        return Task.FromException(new IOException(secret + fixture.Root));
                    }
                    return Task.CompletedTask;
                });
                var error = await Reject<McpException>(() => new WorkspaceTools(execution).ApplyPatch(Patch("*** Update File: sample.md\n@@\n-old\n+new"), CancellationToken.None));
                var saved = await fixture.Load(fixture.RecordIds().Single());
                Assert(saved.Manifest.State == (scenario == "rollback" ? RecoveryRecordState.RolledBack : RecoveryRecordState.Incomplete) &&
                    error.Message.Contains(saved.Manifest.ChangeId.ToString("N")) && !error.Message.Contains(secret) && !error.Message.Contains(fixture.Root), "Bound safe diagnostic carries record identity and truthful state");
                if (scenario == "verification") Assert(fixture.Get("sample.md") == "external\n" && error.Message.Contains("engine result: Committed"), "Installed state and verification failure stay distinct; no extra rollback");
            }
        });

        await check("tool final-save errors expose actual commit state and record identity while preserving installed contents and originals", async () =>
        {
            foreach (var boundary in new[] { RecoveryManifestReplacementStage.BeforeReplace, RecoveryManifestReplacementStage.Replaced })
            {
                using var fixture = new PatchExecutionFixture();
                var finishing = false;
                var installs = 0;
                await using var execution = fixture.Execution((stage, _) => { if (stage == PatchCommitStage.Installed) installs++; return Task.CompletedTask; },
                    replace: (stage, path) =>
                    {
                        if (stage == RecoveryManifestReplacementStage.TemporaryWritten) finishing = RecoveryManifestJson.Parse(File.ReadAllBytes(path)).State == RecoveryRecordState.Committed;
                        return finishing && stage == boundary ? Task.FromException(new IOException("SECRET " + fixture.Root)) : Task.CompletedTask;
                    });
                var error = await Reject<McpException>(() => new WorkspaceTools(execution).ApplyPatch(Patch("*** Update File: sample.md\n@@\n-old\n+new"), CancellationToken.None));
                var saved = await fixture.Load(fixture.RecordIds().Single());
                Assert(error.Message.Contains("engine result: Committed") && error.Message.Contains("could not be saved and verified") &&
                    error.Message.Contains(saved.Manifest.ChangeId.ToString("N")) && !error.Message.Contains("SECRET") && !error.Message.Contains(fixture.Root) &&
                    saved.Manifest.State == (boundary == RecoveryManifestReplacementStage.Replaced ? RecoveryRecordState.Committed : RecoveryRecordState.Applying) &&
                    fixture.Get("sample.md") == "new\n" && Encoding.UTF8.GetString(saved.Backups.Single().Bytes.AsSpan()) == "old\n" && installs == 1, "No false no-write claim, secret/raw exception disclosure or replay after metadata failure");
            }
        });

        await check("successful shared patch results include engine cleanup warnings without claiming rollback", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            await using var execution = fixture.Execution((stage, _) =>
            {
                if (stage == PatchCommitStage.Installed)
                {
                    var backup = Directory.EnumerateFiles(fixture.Root, ".minimcp-*.bak").Single();
                    File.Move(backup, Path.Combine(fixture.DirectoryPath, "detached-original"));
                    Directory.CreateDirectory(backup);
                }
                return Task.CompletedTask;
            });
            var result = await execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
            Assert(result.Commit!.CleanupIncomplete && fixture.Get("sample.md") == "new\n", "Committed result retains cleanup diagnostics");
            await fixture.Load(result);
        });

        await check("public patch serialization exposes only bounded review data and finalized commit summary", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            await using var execution = fixture.Execution();
            var result = await execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var parsed = JsonDocument.Parse(json);
            Assert(parsed.RootElement.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(new[] { "changes", "commit", "decision", "diff", "dryRun", "risk", "statistics" }) &&
                parsed.RootElement.GetProperty("commit").EnumerateObject().Select(property => property.Name).Order().SequenceEqual(new[] { "changeId", "cleanupIncomplete", "runId", "state" }) &&
                !json.Contains(fixture.Root.Replace("\\", "\\\\")) && !json.Contains("before-0000.bin") && !json.Contains("executionContext", StringComparison.OrdinalIgnoreCase), "No internal capability, raw backup, ownership or absolute path enters the public result");
        });
    }

    private static void Assert(bool condition, string message) => PatchExecutionFixture.Assert(condition, message);
}
