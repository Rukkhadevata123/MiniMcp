using System.Net;
using MiniMcp.Configuration;
using MiniMcp.Recovery;
using MiniMcp.Runtime;
using MiniMcp.Services;
using static PatchExecutionFixture;
using static RecoveryRestoreChecks;

internal static class RecoveryRestoreLifetimeChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("duplicate restore evidence cannot replay an active engine and cancelling another queued restore cannot interrupt its owner", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            var armed = false; var entered = Signal(); var release = Signal(); var installs = 0;
            await using var execution = fixture.Execution(async (stage, _) =>
            {
                if (!armed) return;
                if (stage == PatchCommitStage.Staged) { entered.TrySetResult(); await release.Task; }
                if (stage == PatchCommitStage.Installed) installs++;
            });
            var source = await execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
            var plan = await execution.PrepareRecoveryAsync(source.Commit!.ChangeId);
            var peer = await execution.PrepareRecoveryAsync(source.Commit.ChangeId);
            armed = true;
            var active = execution.CommitRecoveryAsync(plan);
            Task<RecoveryRestoreCommit>? queued = null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await Reject<InvalidOperationException>(() => execution.CommitRecoveryAsync(plan));
                using var cancelled = new CancellationTokenSource();
                queued = execution.CommitRecoveryAsync(peer, cancelled.Token);
                Assert(!queued.IsCompleted && !active.IsCompleted && execution.ActiveCalls == 2, "An active consumed plan rejects duplicates while a distinct preflight waits on the shared lock");
                cancelled.Cancel();
                await Reject<OperationCanceledException>(() => queued);
                Assert(!active.IsCompleted && installs == 0 && fixture.Get("sample.md") == "new\n", "Queued cancellation cannot cancel staging owned by the first operation");
                release.TrySetResult();
                await VerifyCommit(fixture, await active, source.Commit.ChangeId, execution.RunId);
                await Reject<WorkspaceException>(() => execution.CommitRecoveryAsync(peer));
                Assert(installs == 1 && fixture.RecordIds().Length == 2 && execution.ActiveCalls == 0, "The peer is unused until admitted, then its stale current-file state prevents a second restore record");
            }
            finally { release.TrySetResult(); await Drain(active); if (queued is not null) await Drain(queued); }
        });

        await check("distinct restore plans sharing a workspace serialize through final metadata and retain independent source and execution identities", async () =>
        {
            using var fixture = new PatchExecutionFixture(); fixture.Put("second.md", "old\n");
            var armed = false; var entered = Signal(); var release = Signal(); var preparedRecords = 0;
            await using var execution = fixture.Execution(prepare: async (stage, _) =>
            {
                if (!armed || stage != RecoveryPreparationStage.BeforeDirectory) return;
                preparedRecords++;
                if (preparedRecords == 1) { entered.TrySetResult(); await release.Task; }
            });
            var firstSource = await execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
            var secondSource = await execution.ApplyPatchAsync(Patch("*** Update File: second.md\n@@\n-old\n+new"));
            var firstPlan = await execution.PrepareRecoveryAsync(firstSource.Commit!.ChangeId);
            var secondPlan = await execution.PrepareRecoveryAsync(secondSource.Commit!.ChangeId);
            armed = true;
            var first = execution.CommitRecoveryAsync(firstPlan);
            Task<RecoveryRestoreCommit>? second = null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                second = execution.CommitRecoveryAsync(secondPlan);
                Assert(!second.IsCompleted && preparedRecords == 1 && fixture.RecordIds().Length == 2, "Peer cannot prepare a new record while another restoration owns the workspace");
                release.TrySetResult();
                var a = await first; var b = await second;
                await VerifyCommit(fixture, a, firstSource.Commit.ChangeId, execution.RunId);
                await VerifyCommit(fixture, b, secondSource.Commit.ChangeId, execution.RunId);
                Assert(a.Commit.Outcome.AttemptId != b.Commit.Outcome.AttemptId && a.Commit.Record.Manifest.ChangeId != b.Commit.Record.Manifest.ChangeId &&
                    fixture.Get("sample.md") == "old\n" && fixture.Get("second.md") == "old\n" && fixture.RecordIds().Length == 4, "Each source, before-image and actual outcome remains bound to its own restoration");
            }
            finally { release.TrySetResult(); await Drain(first); if (second is not null) await Drain(second); }
        });

        await check("restoration in another workspace can finish while one restore engine remains paused", async () =>
        {
            using var firstFixture = new PatchExecutionFixture(); using var secondFixture = new PatchExecutionFixture();
            var armed = false; var entered = Signal(); var release = Signal();
            await using var firstExecution = firstFixture.Execution(async (stage, _) =>
            {
                if (armed && stage == PatchCommitStage.BeforeInstall) { entered.TrySetResult(); await release.Task; }
            });
            await using var secondExecution = secondFixture.Execution();
            var a = await firstExecution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
            var b = await secondExecution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
            var aPlan = await firstExecution.PrepareRecoveryAsync(a.Commit!.ChangeId);
            var bPlan = await secondExecution.PrepareRecoveryAsync(b.Commit!.ChangeId);
            armed = true;
            var first = firstExecution.CommitRecoveryAsync(aPlan);
            Task<RecoveryRestoreCommit>? second = null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                second = secondExecution.CommitRecoveryAsync(bPlan);
                await VerifyCommit(secondFixture, await second.WaitAsync(TimeSpan.FromSeconds(15)), b.Commit.ChangeId, secondExecution.RunId);
                Assert(!first.IsCompleted && firstFixture.Get("sample.md") == "new\n", "Only metadata operations share the store gate; workspace modification ownership remains independent");
                release.TrySetResult();
                await VerifyCommit(firstFixture, await first, a.Commit.ChangeId, firstExecution.RunId);
            }
            finally { release.TrySetResult(); await Drain(first); if (second is not null) await Drain(second); }
        });

        await check("runtime stop deadline retains the real restore finalization, blocks another host and invalidates old-session plans after drain and restart", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            var armed = false; var entered = Signal(); var release = Signal();
            await using var runtime = fixture.Runtime(replace: async (stage, path) =>
            {
                if (armed && stage == RecoveryManifestReplacementStage.TemporaryWritten && RecoveryManifestJson.Parse(File.ReadAllBytes(path)).State == RecoveryRecordState.Committed)
                { entered.TrySetResult(); await release.Task; }
            });
            await runtime.StartAsync(fixture.Configuration);
            var execution = runtime.Execution!; var oldRun = execution.RunId;
            var source = await execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
            var plan = await execution.PrepareRecoveryAsync(source.Commit!.ChangeId);
            var unused = await execution.PrepareRecoveryAsync(source.Commit.ChangeId);
            armed = true;
            var restoring = execution.CommitRecoveryAsync(plan);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await Reject<OperationCanceledException>(() => runtime.StopAsync()).WaitAsync(TimeSpan.FromSeconds(15));
                Assert(!restoring.IsCompleted && execution.ActiveCalls == 1 && !execution.IsDisposed && runtime.Snapshot.State == RuntimeState.Faulted &&
                    fixture.Get("sample.md") == "old\n", "An installed restore remains owned while final metadata exceeds the actual shutdown deadline");
                await Reject<InvalidOperationException>(() => runtime.StartAsync(fixture.Configuration));
                await Reject<WorkspaceException>(() => execution.CommitRecoveryAsync(unused));
                release.TrySetResult();
                var result = await restoring;
                await VerifyCommit(fixture, result, source.Commit.ChangeId, oldRun);
                await runtime.StopAsync();
                Assert(execution.IsDisposed && execution.ActiveCalls == 0 && runtime.Snapshot.State == RuntimeState.Stopped, "Retry releases the old host only after the real restore task has completed");
                await runtime.StartAsync(fixture.Configuration);
                Assert(runtime.Execution!.RunId != oldRun, "Restart creates a fresh preparation issuer and session");
                await Reject<InvalidOperationException>(() => runtime.Execution!.CommitRecoveryAsync(unused));
                await Reject<ObjectDisposedException>(() => execution.CommitRecoveryAsync(unused));
                var history = await runtime.PreviewRecoveryAsync(result.Commit.Record.Manifest.ChangeId);
                Assert(history.Diff is not null && history.RunId == runtime.Snapshot.RunId && history.RecordRunId == oldRun && fixture.RecordIds().Length == 2,
                    "History can cross sessions, but executable preflight evidence and callbacks cannot");
            }
            finally { release.TrySetResult(); await Drain(restoring); }
        });

        await check("runtime stop cancels restore backup preparation and another waiting restore before disposing the shared session", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            var armed = false; var entered = Signal(); var release = Signal();
            await using var runtime = fixture.Runtime(prepare: async (stage, _) =>
            {
                if (armed && stage == RecoveryPreparationStage.BackupWritten) { entered.TrySetResult(); await release.Task; }
            });
            await runtime.StartAsync(fixture.Configuration);
            var execution = runtime.Execution!;
            var source = await execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
            var firstPlan = await execution.PrepareRecoveryAsync(source.Commit!.ChangeId);
            var secondPlan = await execution.PrepareRecoveryAsync(source.Commit.ChangeId);
            var current = Files(fixture); var originalRecord = RecordFiles(fixture, source.Commit.ChangeId); armed = true;
            var first = execution.CommitRecoveryAsync(firstPlan);
            Task<RecoveryRestoreCommit>? second = null; Task? stopping = null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                second = execution.CommitRecoveryAsync(secondPlan);
                Assert(execution.ActiveCalls == 2 && !second.IsCompleted, "Both internal calls belong to the real runtime admission/drain boundary");
                stopping = runtime.StopAsync();
                await Reject<OperationCanceledException>(() => second).WaitAsync(TimeSpan.FromSeconds(5));
                Assert(!stopping.IsCompleted && !first.IsCompleted && !execution.IsDisposed, "Stop keeps owned backup cleanup alive until the writer releases its files");
                release.TrySetResult();
                await Reject<OperationCanceledException>(() => first);
                await stopping;
                Assert(execution.IsDisposed && execution.ActiveCalls == 0 && current.SequenceEqual(Files(fixture)) &&
                    originalRecord.SequenceEqual(RecordFiles(fixture, source.Commit.ChangeId)) && fixture.RecordIds().Length == 1, "No queued, partial or unrecorded restore survives shutdown");
            }
            finally { release.TrySetResult(); await Drain(first); if (second is not null) await Drain(second); if (stopping is not null) await Drain(stopping); }
        });

        await check("runtime stop waits for actual failed-restore rollback and its final manifest without converting the result into cancellation", async () =>
        {
            using var fixture = new PatchExecutionFixture(); fixture.Put("sample.md", "\ufeffold\r\n");
            var armed = false; var installed = Signal(); var fail = Signal(); var finishing = Signal(); var save = Signal();
            var failure = new IOException("Injected restore installation failure after session stop.");
            await using var runtime = fixture.Runtime(async (stage, _) =>
            {
                if (armed && stage == PatchCommitStage.Installed) { installed.TrySetResult(); await fail.Task; throw failure; }
            }, replace: async (stage, path) =>
            {
                if (armed && stage == RecoveryManifestReplacementStage.TemporaryWritten && RecoveryManifestJson.Parse(File.ReadAllBytes(path)).State == RecoveryRecordState.RolledBack)
                { finishing.TrySetResult(); await save.Task; }
            });
            await runtime.StartAsync(fixture.Configuration);
            var execution = runtime.Execution!;
            var source = await execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
            var plan = await execution.PrepareRecoveryAsync(source.Commit!.ChangeId);
            var current = Files(fixture); armed = true;
            var restoring = execution.CommitRecoveryAsync(plan);
            Task? stopping = null;
            try
            {
                await installed.Task.WaitAsync(TimeSpan.FromSeconds(15));
                stopping = runtime.StopAsync(); fail.TrySetResult();
                await finishing.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Assert(!stopping.IsCompleted && !restoring.IsCompleted && execution.ActiveCalls == 1 && current.SequenceEqual(Files(fixture)),
                    "Rollback is complete but the runtime must also await its truthful final record");
                save.TrySetResult();
                var result = await restoring;
                await stopping;
                Assert(result.Commit.Outcome.VerifiedState == PatchCommitState.RolledBack && ReferenceEquals(result.Commit.Outcome.Failure, failure) &&
                    (await fixture.Load(result.Commit.Record.Manifest.ChangeId)).Manifest.State == RecoveryRecordState.RolledBack && execution.IsDisposed &&
                    (await fixture.Load(source.Commit.ChangeId)).Manifest.State == RecoveryRecordState.Committed, "Actual rollback and source history remain intact after session cancellation and disposal");
            }
            finally { fail.TrySetResult(); save.TrySetResult(); await Drain(restoring); if (stopping is not null) await Drain(stopping); }
        });

        await check("restoration stays an internal primitive with no public Runtime write, CLI confirmation stub or Agent tool", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            await using var runtime = fixture.Runtime();
            await runtime.StartAsync(fixture.Configuration);
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
            await using var client = await McpHttpChecks.ConnectAsync(runtime.Address!, http);
            Assert((await client.ListToolsAsync()).Select(tool => tool.Name).Order().SequenceEqual(new[] { "apply_patch", "bash", "git_diff", "git_log", "git_status", "list_files", "read_file", "search_text" }) &&
                !typeof(McpRuntime).GetMethods().Any(method => method.Name is "CommitRecoveryAsync" or "PrepareRecoveryAsync" or "RestoreAsync"), "Local confirmation has no automatic-approved placeholder or remote invocation surface");
            var source = await runtime.Execution!.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
            var preview = await client.CallToolAsync("apply_patch", new Dictionary<string, object?>
            {
                ["patch"] = Patch("*** Update File: sample.md\n@@\n-new\n+different"), ["dry_run"] = true,
                ["sourceChangeId"] = source.Commit!.ChangeId, ["restore"] = true, ["confirmed"] = true, ["approved"] = true
            });
            Assert(McpHttpChecks.Structured(preview).GetProperty("dryRun").GetBoolean() && fixture.Get("sample.md") == "new\n" && fixture.RecordIds().Length == 1,
                "Forged restore and confirmation fields cannot invoke the private restore coordinator or modify the workspace");
            using var route = await http.PostAsync(new Uri(runtime.Address!, "restore"), new StringContent("{}"));
            Assert(route.StatusCode == HttpStatusCode.NotFound, "No restoration management endpoint was added");
        });
    }

    private static void Assert(bool condition, string message) => PatchExecutionFixture.Assert(condition, message);
}
