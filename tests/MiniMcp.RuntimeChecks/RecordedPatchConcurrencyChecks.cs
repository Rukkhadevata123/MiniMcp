using MiniMcp.Recovery;
using MiniMcp.Services;
using static RecordedPatchFaultChecks;
using Fixture = RecordedPatchCommitChecks.Fixture;

internal static class RecordedPatchConcurrencyChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("cancelling a recorded commit queued behind preparation creates no record and cannot interrupt its active owner", async () =>
        {
            using var fixture = new Fixture();
            var peerService = new PatchService(fixture.Workspace);
            var peerPlan = await peerService.PrepareAsync("*** Begin Patch\n*** Add File: peer.md\n+peer\n*** End Patch");
            var peerPreparations = 0;
            var peerStore = fixture.Store(prepare: (_, _) => { peerPreparations++; return Task.CompletedTask; });
            var entered = Signal(); var release = Signal();
            var store = fixture.Store(prepare: async (stage, _) =>
            {
                if (stage == RecoveryPreparationStage.BackupWritten) { entered.TrySetResult(); await release.Task; }
            });
            var original = fixture.ProjectSnapshot();
            var committing = fixture.Service.CommitWithRecoveryAsync(await fixture.Plan(), store);
            Task<RecordedPatchCommit>? queued = null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                using var cancelled = new CancellationTokenSource();
                queued = peerService.CommitWithRecoveryAsync(peerPlan, peerStore, cancelled.Token);
                Assert(!queued.IsCompleted && fixture.Workspace.Mutations.CurrentCount == 0, "Another service using this workspace must wait before preparing its record");
                cancelled.Cancel();
                await Reject<OperationCanceledException>(() => queued);
                Assert(peerPreparations == 0 && !committing.IsCompleted && original.SequenceEqual(fixture.ProjectSnapshot()), "Queued cancellation neither stages files nor cancels the active backup");
                release.TrySetResult();
                var result = await committing;
                await VerifyAsync(store, result);
                Assert(fixture.RecordDirectories().Count() == 1 && !File.Exists(fixture.Path("peer.md")), "Only the admitted operation gets a durable record");
            }
            finally { release.TrySetResult(); await Drain(committing); if (queued is not null) await Drain(queued); }
        });

        await check("concurrent duplicate recorded commits wait for final metadata then reject the stale plan without a second backup", async () =>
        {
            using var fixture = new Fixture();
            var plan = await fixture.Plan();
            var entered = Signal(); var release = Signal();
            var store = fixture.Store(replacement: async (stage, _) =>
            {
                if (stage == RecoveryManifestReplacementStage.BeforeTemporaryFile && fixture.ReadManifest().State == RecoveryRecordState.Applying)
                { entered.TrySetResult(); await release.Task; }
            });
            var duplicatePreparations = 0;
            var peerStore = fixture.Store(prepare: (_, _) => { duplicatePreparations++; return Task.CompletedTask; });
            var committing = fixture.Service.CommitWithRecoveryAsync(plan, store);
            Task<RecordedPatchCommit>? duplicate = null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                duplicate = fixture.Service.CommitWithRecoveryAsync(plan, peerStore);
                Assert(!duplicate.IsCompleted && duplicatePreparations == 0 && fixture.Get("sample.md") == "new\n" &&
                    fixture.ReadManifest().State == RecoveryRecordState.Applying, "Second commit cannot enter between workspace installation and final save");
                release.TrySetResult();
                var result = await committing;
                await Reject<WorkspaceException>(() => duplicate);
                Assert(duplicatePreparations == 0 && fixture.RecordDirectories().Count() == 1 && fixture.Workspace.Mutations.CurrentCount == 1, "Duplicate revalidates under the lock and cannot replay its stale plan");
                await VerifyAsync(store, result);
            }
            finally { release.TrySetResult(); await Drain(committing); if (duplicate is not null) await Drain(duplicate); }
        });

        await check("disjoint recorded commits from two services sharing a workspace serialize through final save and keep independent outcomes", async () =>
        {
            using var fixture = new Fixture();
            var firstPlan = await fixture.Plan();
            var peerService = new PatchService(fixture.Workspace);
            var peerPlan = await peerService.PrepareAsync("*** Begin Patch\n*** Add File: peer.md\n+peer\n*** End Patch");
            var entered = Signal(); var release = Signal();
            var firstStore = fixture.Store(replacement: async (stage, _) =>
            {
                if (stage == RecoveryManifestReplacementStage.BeforeTemporaryFile && fixture.ReadManifest().State == RecoveryRecordState.Applying)
                { entered.TrySetResult(); await release.Task; }
            });
            var peerPreparations = 0;
            var peerStore = fixture.Store(prepare: (_, _) => { peerPreparations++; return Task.CompletedTask; });
            var first = fixture.Service.CommitWithRecoveryAsync(firstPlan, firstStore);
            Task<RecordedPatchCommit>? second = null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                second = peerService.CommitWithRecoveryAsync(peerPlan, peerStore);
                Assert(!second.IsCompleted && peerPreparations == 0 && !File.Exists(fixture.Path("peer.md")), "Peer cannot prepare or stage while first finalization owns the workspace");
                release.TrySetResult();
                var a = await first;
                var b = await second;
                Assert(a.Record.Manifest.ChangeId != b.Record.Manifest.ChangeId && a.Outcome.AttemptId != b.Outcome.AttemptId &&
                    a.Record.Manifest.RunId == b.Record.Manifest.RunId && fixture.RecordDirectories().Count() == 2 && fixture.Get("peer.md") == "peer\n", "Run identity is shared but change and execution identities stay separate");
                await VerifyAsync(firstStore, a);
                await VerifyAsync(peerStore, b);
                await Reject<InvalidOperationException>(() => peerStore.CompleteAsync(a.Record, peerPlan, b.Outcome));
            }
            finally { release.TrySetResult(); await Drain(first); if (second is not null) await Drain(second); }
        });

        await check("a recorded plan changed while waiting for the workspace lock is rejected before recovery preparation", async () =>
        {
            using var fixture = new Fixture();
            var plan = await fixture.Plan();
            var preparations = 0;
            var store = fixture.Store(prepare: (_, _) => { preparations++; return Task.CompletedTask; });
            await fixture.Workspace.Mutations.WaitAsync();
            Task<RecordedPatchCommit> queued;
            try
            {
                queued = fixture.Service.CommitWithRecoveryAsync(plan, store);
                Assert(!queued.IsCompleted, "Mutation owner excludes another commit");
                fixture.Put("sample.md", "external while waiting\n");
            }
            finally { fixture.Workspace.Mutations.Release(); }
            await Reject<WorkspaceException>(() => queued);
            Assert(preparations == 0 && !Directory.Exists(fixture.Storage) && fixture.Get("sample.md") == "external while waiting\n", "Content validation occurs after admission, preserving external changes");
        });

        await check("independent workspaces can complete recorded commits while another workspace engine remains paused", async () =>
        {
            var entered = Signal(); var release = Signal();
            using var firstFixture = new Fixture(async (stage, _) =>
            {
                if (stage == PatchCommitStage.Staged) { entered.TrySetResult(); await release.Task; }
            });
            using var secondFixture = new Fixture();
            var firstStore = firstFixture.Store();
            var secondStore = secondFixture.Store();
            var first = firstFixture.Service.CommitWithRecoveryAsync(await firstFixture.Plan(), firstStore);
            Task<RecordedPatchCommit>? second = null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                second = secondFixture.Service.CommitWithRecoveryAsync(await secondFixture.Plan(), secondStore);
                var b = await second.WaitAsync(TimeSpan.FromSeconds(15));
                Assert(!first.IsCompleted && firstFixture.Get("sample.md") == "old\n" && firstFixture.ReadManifest().State == RecoveryRecordState.Applying &&
                    b.Record.Manifest.RunId != firstFixture.RunId, "Workspace mutation locks and session records are independent");
                await VerifyAsync(secondStore, b);
                release.TrySetResult();
                await VerifyAsync(firstStore, await first);
            }
            finally { release.TrySetResult(); await Drain(first); if (second is not null) await Drain(second); }
        });

        await check("cancelled finalization waiting for the storage gate retains its workspace lock until the actual metadata task finishes", async () =>
        {
            using var cancelled = new CancellationTokenSource();
            using var blocker = new Fixture();
            var blockerPlan = await blocker.Plan();
            var entered = Signal(); var release = Signal(); var verifying = Signal();
            var blockerStore = blocker.Store(prepare: async (stage, _) =>
            {
                if (stage == RecoveryPreparationStage.BeforeDirectory) { entered.TrySetResult(); await release.Task; }
            });
            Task<RecoveryStore.PreparedRecord>? blocking = null;
            var gateStarted = false;
            using var fixture = new Fixture(async (stage, _) =>
            {
                if (stage == PatchCommitStage.Staged && !gateStarted)
                {
                    gateStarted = true;
                    blocking = blockerStore.PrepareAsync(blockerPlan);
                    await entered.Task;
                }
                if (stage == PatchCommitStage.Installed) cancelled.Cancel();
                if (stage == PatchCommitStage.BeforeVerification) verifying.TrySetResult();
            });
            var plan = await fixture.Plan();
            var peerPlan = await fixture.Plan("*** Add File: peer.md\n+peer");
            var store = fixture.Store();
            var committing = fixture.Service.CommitWithRecoveryAsync(plan, store, cancelled.Token);
            Task<RecordedPatchCommit>? peer = null;
            try
            {
                await verifying.Task.WaitAsync(TimeSpan.FromSeconds(15));
                peer = fixture.Service.CommitWithRecoveryAsync(peerPlan, fixture.Store());
                await Reject<OperationCanceledException>(() => committing.WaitAsync(cancelled.Token));
                Assert(blocking is { IsCompleted: false } && !committing.IsCompleted && !peer.IsCompleted && fixture.Workspace.Mutations.CurrentCount == 0 &&
                    fixture.Get("sample.md") == "new\n" && fixture.ReadManifest().State == RecoveryRecordState.Applying, "Cancellation cannot drop a real final task queued on storage or admit another workspace mutation");
                release.TrySetResult();
                var result = await committing;
                var next = await peer;
                await blocking!;
                await VerifyAsync(store, result);
                await VerifyAsync(next.Record.Owner, next);
                Assert(fixture.Workspace.Mutations.CurrentCount == 1 && blocker.ReadManifest().State == RecoveryRecordState.Prepared, "Storage waiter and peer drain with truthful independent record states");
            }
            finally
            {
                release.TrySetResult();
                if (blocking is not null) await Drain(blocking);
                await Drain(committing);
                if (peer is not null) await Drain(peer);
            }
        });
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Assert(bool condition, string message) => RecordedPatchFaultChecks.Assert(condition, message);
    private static async Task Drain(Task task) { try { await task; } catch { /* Preserve the original check failure while draining owned work. */ } }
}
