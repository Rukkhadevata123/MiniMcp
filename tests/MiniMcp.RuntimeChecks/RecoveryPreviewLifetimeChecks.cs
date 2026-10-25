using MiniMcp.Configuration;
using MiniMcp.Recovery;
using MiniMcp.Services;
using MiniMcp.Runtime;
using static PatchExecutionFixture;

internal static class RecoveryPreviewLifetimeChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("recovery preview cancellation at each observation checkpoint releases tracking and modification locks without changing records", async () =>
        {
            foreach (var boundary in Enum.GetValues<RecoveryPreviewStage>())
            {
                using var fixture = new PatchExecutionFixture();
                using var cancelled = new CancellationTokenSource();
                await using var execution = fixture.Execution(preview: (stage, _) => { if (stage == boundary) cancelled.Cancel(); return Task.CompletedTask; });
                var committed = await execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
                var before = fixture.ProjectSnapshot(); var journal = RecoveryPreviewChecks.StoreSnapshot(fixture);
                await Reject<OperationCanceledException>(() => execution.PreviewRecoveryAsync(committed.Commit!.ChangeId, cancelled.Token));
                var ordinary = await execution.ApplyPatchAsync(Patch("*** Add File: probe.md\n+probe"), dryRun: true);
                Assert(ordinary.DryRun && execution.ActiveCalls == 0 && before.SequenceEqual(fixture.ProjectSnapshot()) &&
                    journal.SequenceEqual(RecoveryPreviewChecks.StoreSnapshot(fixture)), "Cancelled inspection leaves the session and modification lock usable");
            }
        });

        await check("recovery preview retains the workspace lock through final observations while queued modification cancellation remains independent", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            var entered = Signal(); var release = Signal(); var blockedOnce = false;
            await using var execution = fixture.Execution(preview: async (stage, _) =>
            {
                if (stage == RecoveryPreviewStage.BeforeReturn && !blockedOnce) { blockedOnce = true; entered.TrySetResult(); await release.Task; }
            });
            var committed = await execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
            var preview = execution.PreviewRecoveryAsync(committed.Commit!.ChangeId);
            using var cancelled = new CancellationTokenSource();
            Task<PatchApplyResult>? rejected = null; Task<PatchApplyResult>? writer = null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                rejected = execution.ApplyPatchAsync(Patch("*** Add File: cancelled/new.md\n+new"), cancellationToken: cancelled.Token);
                writer = execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-new\n+next"));
                Assert(!rejected.IsCompleted && !writer.IsCompleted && execution.ActiveCalls == 3, "Both mutation attempts share the preview's workspace mutex");
                cancelled.Cancel();
                await Reject<OperationCanceledException>(() => rejected);
                Assert(!preview.IsCompleted && !writer.IsCompleted && fixture.RecordIds().Length == 1 && !Directory.Exists(fixture.PathOf("cancelled")), "Cancelling another caller neither ends preview nor prepares a backup");
                release.TrySetResult();
                var first = await preview;
                var second = await writer;
                Assert(!first.HasConflicts && first.Diff is not null && second.Commit is not null && fixture.Get("sample.md") == "next\n" && fixture.RecordIds().Length == 2,
                    "Preview completes its observation before another writer is admitted");
                var later = await execution.PreviewRecoveryAsync(committed.Commit.ChangeId);
                Assert(later.HasConflicts && later.Diff is null, "An earlier observation does not reserve or authorize restoration after the next writer");
            }
            finally { release.TrySetResult(); await Drain(preview); if (rejected is not null) await Drain(rejected); if (writer is not null) await Drain(writer); }
        });

        await check("cancelling recovery preview while waiting for the workspace mutex never reaches record loading or releases the active owner's lock", async () =>
        {
            using var fixture = new RecordedPatchCommitChecks.Fixture();
            var store = fixture.Store();
            var committed = await fixture.Service.CommitWithRecoveryAsync(await fixture.Plan(), store);
            var loaded = 0;
            var previews = new RecoveryPreviewService(fixture.Workspace, store, (stage, _) => { if (stage == RecoveryPreviewStage.RecordLoaded) loaded++; return Task.CompletedTask; });
            await fixture.Workspace.Mutations.WaitAsync();
            try
            {
                using var cancelled = new CancellationTokenSource();
                var queued = previews.PreviewAsync(committed.Record.Manifest.ChangeId, cancelled.Token);
                Assert(!queued.IsCompleted, "Inspection waits for the same lock used by recorded mutation");
                cancelled.Cancel();
                await Reject<OperationCanceledException>(() => queued);
                Assert(loaded == 0 && fixture.Workspace.Mutations.CurrentCount == 0, "A cancelled waiter cannot read metadata or release someone else's mutex");
            }
            finally { fixture.Workspace.Mutations.Release(); }
            Assert((await previews.PreviewAsync(committed.Record.Manifest.ChangeId)).Diff is not null, "Lock owner can finish and inspection can be retried");
        });

        await check("recovery preview cancellation while waiting for storage releases only its workspace ownership and preserves the active storage writer", async () =>
        {
            using var fixture = new RecordedPatchCommitChecks.Fixture();
            using var blocker = new RecordedPatchCommitChecks.Fixture();
            var store = fixture.Store();
            var committed = await fixture.Service.CommitWithRecoveryAsync(await fixture.Plan(), store);
            var entered = Signal(); var release = Signal();
            var blockingStore = blocker.Store(prepare: async (stage, _) =>
            {
                if (stage == RecoveryPreparationStage.BeforeDirectory) { entered.TrySetResult(); await release.Task; }
            });
            var preparing = blockingStore.PrepareAsync(await blocker.Plan());
            Task<RecoveryPreviewResult>? queued = null;
            using var cancelled = new CancellationTokenSource();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                queued = new RecoveryPreviewService(fixture.Workspace, store).PreviewAsync(committed.Record.Manifest.ChangeId, cancelled.Token);
                await Until(() => fixture.Workspace.Mutations.CurrentCount == 0);
                cancelled.Cancel();
                await Reject<OperationCanceledException>(() => queued).WaitAsync(TimeSpan.FromSeconds(5));
                Assert(!preparing.IsCompleted && fixture.Workspace.Mutations.CurrentCount == 1 && committed.Record.Manifest.State == RecoveryRecordState.Committed,
                    "Storage-wait cancellation frees this workspace, leaving the other store's real task intact");
            }
            finally { release.TrySetResult(); await Drain(preparing); if (queued is not null) await Drain(queued); }
        });

        await check("busy native storage locks reject recovery preview without creating or modifying project and record entries", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            await using var execution = fixture.Execution();
            var committed = await execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
            var before = fixture.ProjectSnapshot(); var journal = RecoveryPreviewChecks.StoreSnapshot(fixture);
            using (var held = RecoveryFileSystem.OpenRead(Path.Combine(fixture.RecoveryDirectory, RecoveryStorageLimits.LockFileName)))
            {
                RecoveryFileSystem.Lock(held);
                await Reject<IOException>(() => execution.PreviewRecoveryAsync(committed.Commit!.ChangeId));
                Assert(execution.ActiveCalls == 0 && before.SequenceEqual(fixture.ProjectSnapshot()), "Busy metadata is never bypassed or promoted to restore readiness");
            }
            Assert(journal.SequenceEqual(RecoveryPreviewChecks.StoreSnapshot(fixture)) && (await execution.PreviewRecoveryAsync(committed.Commit!.ChangeId)).Diff is not null,
                "Storage remains intact and inspection retries only after the actual lock is free");
        });

        await check("local runtime recovery previews drain on stop, retain historical run identity after restart and add no MCP tools", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            var entered = Signal(); var release = Signal(); var blockedOnce = false;
            await using var runtime = fixture.Runtime(preview: async (stage, _) =>
            {
                if (stage == RecoveryPreviewStage.BeforeReturn && !blockedOnce) { blockedOnce = true; entered.TrySetResult(); await release.Task; }
            });
            await Reject<InvalidOperationException>(() => runtime.PreviewRecoveryAsync(Guid.NewGuid()));
            await runtime.StartAsync(fixture.Configuration);
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
            await using var client = await McpHttpChecks.ConnectAsync(runtime.Address!, http);
            Assert((await client.ListToolsAsync()).Select(tool => tool.Name).Order().SequenceEqual(new[] { "apply_patch", "bash", "read_file" }), "Historical inspection is an in-process API, not an Agent tool or HTTP management endpoint");
            var oldExecution = runtime.Execution!;
            var committed = await oldExecution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
            var recordId = committed.Commit!.ChangeId;
            var originalRun = runtime.Snapshot.RunId;
            var preview = runtime.PreviewRecoveryAsync(recordId);
            Task? stopping = null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                stopping = runtime.StopAsync();
                await Until(() => runtime.Snapshot.State == RuntimeState.Stopping);
                await Reject<WorkspaceException>(() => runtime.PreviewRecoveryAsync(recordId));
                Assert(!stopping.IsCompleted && !preview.IsCompleted && oldExecution.ActiveCalls == 1 && !oldExecution.IsDisposed,
                    "Stop closes admission but retains the actual observer until cancellation has drained");
                release.TrySetResult();
                await Reject<OperationCanceledException>(() => preview);
                await stopping;
                Assert(oldExecution.IsDisposed && oldExecution.ActiveCalls == 0 && (await fixture.Load(recordId)).Manifest.State == RecoveryRecordState.Committed,
                    "Cancelled preview never changes a completed record or outlives session disposal");
                await runtime.StartAsync(fixture.ConfigurationFor(ExecutionMode.ReadOnly));
                var current = await runtime.PreviewRecoveryAsync(recordId);
                Assert(current.Diff is not null && current.RunId == runtime.Snapshot.RunId && current.RunId != originalRun && current.RecordRunId == originalRun &&
                    current.Blockers.SequenceEqual(["ReadOnlyMode"]) && fixture.RecordIds().Length == 1, "Fresh read-only session inspects history without inheriting old run identity, write authority or subscriptions");
                await Reject<ObjectDisposedException>(() => oldExecution.PreviewRecoveryAsync(recordId));
                await runtime.DisposeAsync();
                await Reject<ObjectDisposedException>(() => runtime.PreviewRecoveryAsync(recordId));
            }
            finally { release.TrySetResult(); await Drain(preview); if (stopping is not null) await Drain(stopping); }
        });
    }

    private static async Task Until(Func<bool> condition)
    { for (var index = 0; index < 500; index++) { if (condition()) return; await Task.Delay(10); } throw new Exception("Timed out waiting for recovery observation ownership."); }
    private static void Assert(bool condition, string message) => PatchExecutionFixture.Assert(condition, message);
}
