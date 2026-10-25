using System.Text;
using MiniMcp.Recovery;
using MiniMcp.Services;
using static PatchExecutionFixture;
using static RecoveryRestoreChecks;

internal static class RecoveryRestoreFaultChecks
{
    private const string SourceBody = "*** Update File: sample.md\n@@\n-old\n+new\n*** Add File: nested/added.md\n+added";

    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("restore backup failures at every preparation checkpoint leave the entire current workspace and source record untouched", async () =>
        {
            foreach (var boundary in Enum.GetValues<RecoveryPreparationStage>())
            {
                using var fixture = new PatchExecutionFixture();
                var armed = false; var installs = 0;
                var failure = new IOException("Injected restore backup failure.");
                await using var execution = fixture.Execution((stage, _) => { if (armed && stage == PatchCommitStage.Installed) installs++; return Task.CompletedTask; },
                    prepare: (stage, _) => armed && stage == boundary ? Task.FromException(failure) : Task.CompletedTask);
                var source = await execution.ApplyPatchAsync(Patch(SourceBody));
                var plan = await execution.PrepareRecoveryAsync(source.Commit!.ChangeId);
                var current = Files(fixture); var sourceData = RecordFiles(fixture, source.Commit.ChangeId);
                armed = true;
                var error = await Reject<IOException>(() => execution.CommitRecoveryAsync(plan));
                Assert(ReferenceEquals(error, failure) && installs == 0 && current.SequenceEqual(Files(fixture)) &&
                    sourceData.SequenceEqual(RecordFiles(fixture, source.Commit.ChangeId)) && fixture.RecordIds().Length == 1 && execution.ActiveCalls == 0,
                    "No restore installation or unrecorded fallback before complete before-image persistence");
            }
        });

        await check("restore Applying persistence failures preserve truthful disk states and never start the engine", async () =>
        {
            foreach (var boundary in new[] { RecoveryManifestReplacementStage.BeforeReplace, RecoveryManifestReplacementStage.Replaced })
            {
                using var fixture = new PatchExecutionFixture();
                var armed = false; var engineCalls = 0;
                await using var execution = fixture.Execution((_, _) => { if (armed) engineCalls++; return Task.CompletedTask; },
                    replace: (stage, _) => armed && stage == boundary ? Task.FromException(new IOException("Injected restore Applying error.")) : Task.CompletedTask);
                var source = await execution.ApplyPatchAsync(Patch(SourceBody));
                var plan = await execution.PrepareRecoveryAsync(source.Commit!.ChangeId);
                var current = Files(fixture); var sourceData = RecordFiles(fixture, source.Commit.ChangeId);
                armed = true;
                await Reject<IOException>(() => execution.CommitRecoveryAsync(plan));
                var record = await fixture.Load(fixture.RecordIds().Single(id => id != source.Commit.ChangeId));
                Assert(record.Manifest.State == (boundary == RecoveryManifestReplacementStage.Replaced ? RecoveryRecordState.Applying : RecoveryRecordState.Prepared) &&
                    engineCalls == 0 && current.SequenceEqual(Files(fixture)) && sourceData.SequenceEqual(RecordFiles(fixture, source.Commit.ChangeId)),
                    "Unverified Applying denies all workspace staging; actual persisted metadata is left inspectable");
                await Reject<InvalidOperationException>(() => execution.CommitRecoveryAsync(plan));
            }
        });

        await check("source record changes during restore preparation, Applying, staging and partial installation prevent or roll back the complete restore", async () =>
        {
            foreach (var phase in new[] { "backup", "applying", "staged", "installed-first", "installed-last" })
            {
                using var fixture = new PatchExecutionFixture();
                var armed = false; var changed = false; var id = Guid.Empty;
                void Change() { if (!changed) { changed = true; RewriteRun(fixture, id); } }
                await using var execution = fixture.Execution((stage, index) =>
                {
                    if (armed && ((phase == "staged" && stage == PatchCommitStage.Staged) ||
                        (stage == PatchCommitStage.Installed && ((phase == "installed-first" && index == 0) || (phase == "installed-last" && index == 1))))) Change();
                    return Task.CompletedTask;
                }, prepare: (stage, _) => { if (armed && phase == "backup" && stage == RecoveryPreparationStage.BeforeReturn) Change(); return Task.CompletedTask; },
                    replace: (stage, path) =>
                    {
                        if (armed && phase == "applying" && stage == RecoveryManifestReplacementStage.BeforeReturn &&
                            RecoveryManifestJson.Parse(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(path)!, RecoveryStore.ManifestFileName))).State == RecoveryRecordState.Applying) Change();
                        return Task.CompletedTask;
                    });
                var source = await execution.ApplyPatchAsync(Patch(SourceBody)); id = source.Commit!.ChangeId;
                var plan = await execution.PrepareRecoveryAsync(id);
                var current = Files(fixture);
                armed = true;
                if (phase == "backup") await Reject<WorkspaceException>(() => execution.CommitRecoveryAsync(plan));
                else
                {
                    var failure = await execution.CommitRecoveryAsync(plan);
                    Assert(failure.Commit.Outcome.VerifiedState == PatchCommitState.RolledBack && failure.Commit.Outcome.Failure is WorkspaceException,
                        "Actual installed prefixes roll back and source validation failure stays attached");
                }
                var newRecord = await fixture.Load(fixture.RecordIds().Single(recordId => recordId != id));
                Assert(changed && current.SequenceEqual(Files(fixture)) && newRecord.Manifest.State ==
                    (phase == "backup" ? RecoveryRecordState.Prepared : RecoveryRecordState.RolledBack) &&
                    (await fixture.Load(id)).Manifest.RunId != source.Commit.RunId, "Revalidation preserves externally rewritten source metadata and prevents partial restoration: " + phase);
            }
        });

        await check("restore mixed-failure matrix rolls back every installed prefix with exact current bytes and a finalized new rollback record", async () =>
        {
            var boundaries = new[] { (PatchCommitStage.Staged, -1) }.Concat(Enumerable.Range(0, 6).SelectMany(index =>
                new[] { (PatchCommitStage.BeforeInstall, index), (PatchCommitStage.Installed, index) }));
            foreach (var boundary in boundaries)
            {
                using var fixture = new PatchExecutionFixture(); Seed(fixture);
                using var cancel = new CancellationTokenSource();
                var armed = false; var failure = new IOException("Injected restore installation failure.");
                await using var execution = fixture.Execution((stage, index) =>
                {
                    if (!armed || (stage, index) != boundary) return Task.CompletedTask;
                    cancel.Cancel(); return Task.FromException(failure);
                });
                var source = await execution.ApplyPatchAsync(Patch(MixedBody));
                Directory.Delete(fixture.PathOf("gone"));
                var plan = await execution.PrepareRecoveryAsync(source.Commit!.ChangeId);
                var current = Files(fixture); var sourceData = RecordFiles(fixture, source.Commit.ChangeId);
                armed = true;
                var result = await execution.CommitRecoveryAsync(plan, cancel.Token);
                Assert(result.Commit.Outcome.VerifiedState == PatchCommitState.RolledBack && ReferenceEquals(result.Commit.Outcome.Failure, failure) &&
                    result.Commit.Outcome.RollbackErrors.IsEmpty && result.Commit.Outcome.CleanupErrors.IsEmpty && current.SequenceEqual(Files(fixture)) &&
                    !Directory.Exists(fixture.PathOf("gone")) && sourceData.SequenceEqual(RecordFiles(fixture, source.Commit.ChangeId)), "Failure restores the state before restoration, including removal of directories this attempt created");
                await RecordedPatchFaultChecks.VerifyAsync(result.Commit.Record.Owner, result.Commit);
            }
        });

        await check("restore cancellation boundaries distinguish unprepared, rolled-back and genuinely committed operations", async () =>
        {
            foreach (var boundary in new[] { "backup", "applying", "Staged", "BeforeInstall", "Installed", "BeforeVerification", "final-save" })
            {
                using var fixture = new PatchExecutionFixture();
                using var cancel = new CancellationTokenSource(); var armed = false;
                var original = Files(fixture);
                await using var execution = fixture.Execution((stage, _) => { if (armed && stage.ToString() == boundary) cancel.Cancel(); return Task.CompletedTask; },
                    prepare: (stage, _) => { if (armed && boundary == "backup" && stage == RecoveryPreparationStage.BeforeManifest) cancel.Cancel(); return Task.CompletedTask; },
                    replace: (stage, path) =>
                    {
                        if (armed && ((boundary == "applying" && stage == RecoveryManifestReplacementStage.Replaced) ||
                            (boundary == "final-save" && stage == RecoveryManifestReplacementStage.BeforeReturn &&
                            RecoveryManifestJson.Parse(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(path)!, RecoveryStore.ManifestFileName))).State == RecoveryRecordState.Committed))) cancel.Cancel();
                        return Task.CompletedTask;
                    });
                var source = await execution.ApplyPatchAsync(Patch(SourceBody));
                var plan = await execution.PrepareRecoveryAsync(source.Commit!.ChangeId);
                var current = Files(fixture); armed = true;
                if (boundary == "backup")
                {
                    await Reject<OperationCanceledException>(() => execution.CommitRecoveryAsync(plan, cancel.Token));
                    Assert(fixture.RecordIds().Length == 1 && current.SequenceEqual(Files(fixture)), "Cancellation cleans only its unfinished backup and does not install workspace files");
                }
                else
                {
                    var result = await execution.CommitRecoveryAsync(plan, cancel.Token);
                    var early = boundary is "applying" or "Staged";
                    Assert(result.Commit.Outcome.VerifiedState == (early ? PatchCommitState.RolledBack : PatchCommitState.Committed) &&
                        (early ? current : original).SequenceEqual(Files(fixture)) && cancel.IsCancellationRequested && execution.ActiveCalls == 0,
                        "Late cancellation cannot interrupt installation or overwrite the actual outcome: " + boundary);
                    await RecordedPatchFaultChecks.VerifyAsync(result.Commit.Record.Owner, result.Commit);
                }
            }
        });

        await check("restore final-manifest fault matrix keeps all actual terminal states and forbids replay even when only metadata can retry", async () =>
        {
            foreach (var terminal in Enum.GetValues<PatchCommitState>())
                foreach (var boundary in Enum.GetValues<RecoveryManifestReplacementStage>())
                {
                    using var fixture = new PatchExecutionFixture();
                    var armed = false; var finishing = false; var failOnce = true;
                    await using var execution = fixture.Execution((stage, _) =>
                    {
                        if (!armed || stage != PatchCommitStage.Installed || terminal == PatchCommitState.Committed) return Task.CompletedTask;
                        if (terminal == PatchCommitState.Incomplete) { File.Delete(fixture.PathOf("sample.md")); Directory.CreateDirectory(fixture.PathOf("sample.md")); }
                        return Task.FromException(new IOException("Injected restore rollback trigger."));
                    }, replace: (stage, path) =>
                    {
                        if (armed && stage == RecoveryManifestReplacementStage.BeforeTemporaryFile)
                            finishing = RecoveryManifestJson.Parse(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(path)!, RecoveryStore.ManifestFileName))).State == RecoveryRecordState.Applying;
                        if (!armed || !finishing || !failOnce || stage != boundary) return Task.CompletedTask;
                        failOnce = false;
                        return Task.FromException(boundary == RecoveryManifestReplacementStage.BeforeReturn ?
                            new OperationCanceledException("Injected restore final-readback cancellation error.") : new IOException("Injected restore final-state persistence failure."));
                    });
                    var source = await execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
                    var plan = await execution.PrepareRecoveryAsync(source.Commit!.ChangeId);
                    var sourceData = RecordFiles(fixture, source.Commit.ChangeId); armed = true;
                    var error = await Reject<RecoveryFinalizationException>(() => execution.CommitRecoveryAsync(plan));
                    var uncertain = boundary is RecoveryManifestReplacementStage.Replaced or RecoveryManifestReplacementStage.BeforeReturn;
                    var expected = terminal switch { PatchCommitState.Committed => RecoveryRecordState.Committed, PatchCommitState.RolledBack => RecoveryRecordState.RolledBack, _ => RecoveryRecordState.Incomplete };
                    var saved = await fixture.Load(error.Record.Manifest.ChangeId);
                    Assert(error.Outcome.VerifiedState == terminal && saved.Manifest.State == (uncertain ? expected : RecoveryRecordState.Applying) &&
                        error.Record.IsInvalidated == uncertain && error.Record.Manifest.State == RecoveryRecordState.Applying &&
                        sourceData.SequenceEqual(RecordFiles(fixture, source.Commit.ChangeId)) && Encoding.UTF8.GetString(saved.Backups.Single().Bytes.AsSpan()) == "new\n",
                        "Actual installation, rollback, uncertain disk state and complete pre-restore bytes remain available");
                    Assert(terminal == PatchCommitState.Incomplete ? Directory.Exists(fixture.PathOf("sample.md")) :
                        fixture.Get("sample.md") == (terminal == PatchCommitState.Committed ? "old\n" : "new\n"), "Final-state failure does not perform another rollback");
                    await Reject<InvalidOperationException>(() => execution.CommitRecoveryAsync(plan));
                    if (!uncertain)
                    {
                        await error.Record.Owner.CompleteAsync(error.Record, error.Record.Plan, error.Outcome);
                        await RecordedPatchFaultChecks.VerifyAsync(error.Record.Owner, new(error.Record, error.Outcome));
                    }
                }
        });

        await check("full or natively locked recovery storage cannot make restoration skip its own before-image record", async () =>
        {
            foreach (var locked in new[] { false, true })
            {
                using var fixture = new PatchExecutionFixture();
                await using var execution = fixture.Execution();
                var source = await execution.ApplyPatchAsync(Patch(SourceBody));
                if (!locked) for (var index = 0; index < 999; index++) Directory.CreateDirectory(Path.Combine(fixture.RecoveryDirectory, $"foreign-{index}"));
                var plan = await execution.PrepareRecoveryAsync(source.Commit!.ChangeId);
                var current = Files(fixture); var originalRecord = RecordFiles(fixture, source.Commit.ChangeId);
                FileStream? held = null;
                try
                {
                    if (locked) { held = RecoveryFileSystem.OpenRead(Path.Combine(fixture.RecoveryDirectory, RecoveryStorageLimits.LockFileName)); RecoveryFileSystem.Lock(held); }
                    await Reject<IOException>(() => execution.CommitRecoveryAsync(plan));
                    Assert(current.SequenceEqual(Files(fixture)) && originalRecord.SequenceEqual(RecordFiles(fixture, source.Commit.ChangeId)) &&
                        fixture.RecordIds().Length == 1 && execution.ActiveCalls == 0, "Backup capacity/lock failure performs no workspace staging, eviction or unrecorded fallback");
                }
                finally { held?.Dispose(); }
            }
        });

        await check("quota growth after restoration installation reports the committed engine result and preserves both original records and current bytes", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            var armed = false;
            await using var execution = fixture.Execution((stage, _) =>
            {
                if (armed && stage == PatchCommitStage.BeforeVerification)
                    for (var index = 0; index < 1000; index++) Directory.CreateDirectory(Path.Combine(fixture.RecoveryDirectory, $"foreign-{index}"));
                return Task.CompletedTask;
            });
            var source = await execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
            var plan = await execution.PrepareRecoveryAsync(source.Commit!.ChangeId); armed = true;
            var error = await Reject<RecoveryFinalizationException>(() => execution.CommitRecoveryAsync(plan));
            Assert(error.Outcome.VerifiedState == PatchCommitState.Committed && error.Record.Manifest.State == RecoveryRecordState.Applying &&
                fixture.Get("sample.md") == "old\n" && (await fixture.Load(source.Commit.ChangeId)).Manifest.State == RecoveryRecordState.Committed &&
                Encoding.UTF8.GetString((await fixture.Load(error.Record.Manifest.ChangeId)).Backups.Single().Bytes.AsSpan()) == "new\n" &&
                Directory.EnumerateDirectories(fixture.RecoveryDirectory).Count() == 1002, "Late metadata capacity failure cannot be described as no restoration or automatic rollback");
            await Reject<InvalidOperationException>(() => execution.CommitRecoveryAsync(plan));
        });

        await check("restore staging cleanup and rollback failures persist Incomplete and retain complete pre-restore recovery bytes", async () =>
        {
            foreach (var rollback in new[] { false, true })
            {
                using var fixture = new PatchExecutionFixture();
                var armed = false; string? retained = null;
                await using var execution = fixture.Execution((stage, _) =>
                {
                    if (!armed || stage != (rollback ? PatchCommitStage.Installed : PatchCommitStage.Staged)) return Task.CompletedTask;
                    if (rollback) { File.Delete(fixture.PathOf("sample.md")); Directory.CreateDirectory(fixture.PathOf("sample.md")); }
                    else
                    {
                        retained = Directory.EnumerateFiles(fixture.Root, ".minimcp-*.tmp").Single();
                        File.Move(retained, Path.Combine(fixture.DirectoryPath, "detached-stage")); Directory.CreateDirectory(retained);
                    }
                    return Task.FromException(new IOException("Injected incomplete restore cleanup/rollback."));
                });
                var source = await execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
                var plan = await execution.PrepareRecoveryAsync(source.Commit!.ChangeId); armed = true;
                var result = await execution.CommitRecoveryAsync(plan);
                Assert(result.Commit.Outcome.VerifiedState == PatchCommitState.Incomplete && result.Commit.Record.Manifest.State == RecoveryRecordState.Incomplete &&
                    (rollback ? !result.Commit.Outcome.RollbackErrors.IsEmpty && Directory.Exists(fixture.PathOf("sample.md")) :
                        !result.Commit.Outcome.CleanupErrors.IsEmpty && Directory.Exists(retained) && fixture.Get("sample.md") == "new\n"), "Incomplete cleanup is never disguised as full rollback or success");
                Assert(Encoding.UTF8.GetString((await fixture.Load(result.Commit.Record.Manifest.ChangeId)).Backups.Single().Bytes.AsSpan()) == "new\n" &&
                    Encoding.UTF8.GetString((await fixture.Load(source.Commit.ChangeId)).Backups.Single().Bytes.AsSpan()) == "old\n", "Original change and restoration each retain their own independent full before-image");
            }
        });

        await check("post-restore verification failure preserves external content and cannot report Committed or trigger a second rollback", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            var armed = false;
            await using var execution = fixture.Execution((stage, _) => { if (armed && stage == PatchCommitStage.BeforeVerification) fixture.Put("sample.md", "external\n"); return Task.CompletedTask; });
            var source = await execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
            var plan = await execution.PrepareRecoveryAsync(source.Commit!.ChangeId); armed = true;
            var result = await execution.CommitRecoveryAsync(plan);
            Assert(result.Commit.Outcome.State == PatchCommitState.Committed && result.Commit.Outcome.VerifiedState == PatchCommitState.Incomplete &&
                result.Commit.Record.Manifest.State == RecoveryRecordState.Incomplete && fixture.Get("sample.md") == "external\n", "Installation facts and failed final observation remain separate");
            if (OperatingSystem.IsLinux())
            {
                using var permissions = new PatchExecutionFixture(); Directory.CreateDirectory(permissions.PathOf("gone")); permissions.Put("gone/script.cs", "source\n");
                File.SetUnixFileMode(permissions.PathOf("gone/script.cs"), (UnixFileMode)0x1ed);
                var restoring = false;
                await using var permissionExecution = permissions.Execution((stage, _) =>
                {
                    if (OperatingSystem.IsLinux() && restoring && stage == PatchCommitStage.Installed) File.SetUnixFileMode(permissions.PathOf("gone/script.cs"), (UnixFileMode)0x1a4);
                    return Task.CompletedTask;
                });
                var deletion = await permissionExecution.ApplyPatchAsync(Patch("*** Delete File: gone/script.cs"));
                var preparation = await permissionExecution.PrepareRecoveryAsync(deletion.Commit!.ChangeId); restoring = true;
                var observed = await permissionExecution.CommitRecoveryAsync(preparation);
                Assert(observed.Commit.Outcome.VerifiedState == PatchCommitState.Incomplete && observed.Commit.Outcome.VerificationFailure is not null,
                    "Restored additions must match exact recorded permissions; merely being a subset of a creation request is insufficient");
            }
        });

        await check("restore installation success retains cleanup warnings and its actual Committed history", async () =>
        {
            using var fixture = new PatchExecutionFixture(); var armed = false;
            await using var execution = fixture.Execution((stage, _) =>
            {
                if (armed && stage == PatchCommitStage.Installed)
                {
                    var backup = Directory.EnumerateFiles(fixture.Root, ".minimcp-*.bak").Single();
                    File.Move(backup, Path.Combine(fixture.DirectoryPath, "detached-before-restore")); Directory.CreateDirectory(backup);
                }
                return Task.CompletedTask;
            });
            var source = await execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
            var plan = await execution.PrepareRecoveryAsync(source.Commit!.ChangeId); armed = true;
            var result = await execution.CommitRecoveryAsync(plan);
            Assert(result.Commit.Outcome.VerifiedState == PatchCommitState.Committed && result.Commit.Outcome.CleanupErrors.Length == 1 &&
                fixture.Get("sample.md") == "old\n" && (await fixture.Load(result.Commit.Record.Manifest.ChangeId)).Manifest.State == RecoveryRecordState.Committed,
                "An old engine-backup cleanup warning does not pretend the restore was rolled back");
        });

        await check("failed restore rollback preserves new external content in an installed path and retains both recovery and engine backups", async () =>
        {
            using var fixture = new PatchExecutionFixture(); var armed = false;
            var failure = new IOException("Injected failure after an external edit to the installed restore path.");
            await using var execution = fixture.Execution((stage, index) =>
            {
                if (!armed || stage != PatchCommitStage.Installed || index != 0) return Task.CompletedTask;
                fixture.Put("sample.md", "external newer content\n");
                return Task.FromException(failure);
            });
            var source = await execution.ApplyPatchAsync(Patch("*** Add File: added.md\n+added\n*** Update File: sample.md\n@@\n-old\n+new"));
            var plan = await execution.PrepareRecoveryAsync(source.Commit!.ChangeId); armed = true;
            var result = await execution.CommitRecoveryAsync(plan);
            Assert(result.Commit.Outcome.VerifiedState == PatchCommitState.Incomplete && ReferenceEquals(result.Commit.Outcome.Failure, failure) &&
                !result.Commit.Outcome.RollbackErrors.IsEmpty && fixture.Get("sample.md") == "external newer content\n" && fixture.Get("added.md") == "added\n",
                "Rollback never replaces a detected external edit with the pre-restore image");
            Assert(File.ReadAllText(Directory.EnumerateFiles(fixture.Root, ".minimcp-*.bak").Single()) == "new\n" &&
                Encoding.UTF8.GetString((await fixture.Load(result.Commit.Record.Manifest.ChangeId)).PathsBeforeBytesFor("sample.md")) == "new\n" &&
                (await fixture.Load(result.Commit.Record.Manifest.ChangeId)).Manifest.State == RecoveryRecordState.Incomplete,
                "Failed rollback keeps the actual engine original and durable before-restore bytes for local inspection");
        });
    }

    private static void Assert(bool condition, string message) => PatchExecutionFixture.Assert(condition, message);
    private static byte[] PathsBeforeBytesFor(this RecoveryRecordSnapshot record, string path) => record.Backups.Single(backup =>
        backup.FileName == record.Manifest.Paths.Single(state => state.Path == path).BackupFile).Bytes.ToArray();
}
