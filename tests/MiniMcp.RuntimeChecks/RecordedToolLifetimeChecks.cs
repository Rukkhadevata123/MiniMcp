using MiniMcp.Configuration;
using MiniMcp.Recovery;
using MiniMcp.Runtime;
using MiniMcp.Services;
using static PatchExecutionFixture;

internal static class RecordedToolLifetimeChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("runtime stop deadline retains an installed patch and final task, forbids a second host and permits restart only after true drain", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            using var next = new PatchExecutionFixture(ExecutionMode.ReadOnly);
            var entered = Signal(); var release = Signal();
            await using var runtime = fixture.Runtime(replace: async (stage, path) =>
            {
                if (stage == RecoveryManifestReplacementStage.TemporaryWritten && RecoveryManifestJson.Parse(File.ReadAllBytes(path)).State == RecoveryRecordState.Committed)
                { entered.TrySetResult(); await release.Task; }
            });
            await runtime.StartAsync(fixture.Configuration);
            var before = runtime.Snapshot;
            var execution = runtime.Execution!;
            var call = execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await Reject<OperationCanceledException>(() => runtime.StopAsync()).WaitAsync(TimeSpan.FromSeconds(15));
                Assert(runtime.Snapshot.State == RuntimeState.Faulted && runtime.Snapshot.RunId == before.RunId &&
                    !call.IsCompleted && !execution.IsDisposed && execution.ActiveCalls == 1 && fixture.ReadManifest().State == RecoveryRecordState.Applying,
                    "Real shutdown deadline cannot label finalization stopped or dispose its owned services");
                await Reject<InvalidOperationException>(() => runtime.StartAsync(next.Configuration));
                await Reject<WorkspaceException>(() => execution.ApplyPatchAsync(Patch("*** Add File: forbidden.md\n+no")));
                release.TrySetResult();
                var committed = await call;
                await fixture.Load(committed);
                Assert(committed.Commit!.RunId == before.RunId && fixture.Get("sample.md") == "new\n", "Session cancellation cannot become a false cancelled write result");
                await runtime.StopAsync();
                Assert(execution.ActiveCalls == 0 && execution.IsDisposed && runtime.Snapshot.State == RuntimeState.Stopped, "Retry waits for the real owned drain then releases the old host");
                await runtime.StartAsync(next.Configuration);
                Assert(runtime.Snapshot.RunId != before.RunId && runtime.Execution!.Configuration == next.Configuration &&
                    runtime.Execution.RunId == runtime.Snapshot.RunId, "New host receives its own project, mode and recovery instance");
                await Reject<ObjectDisposedException>(() => execution.ApplyPatchAsync(Patch("*** Add File: stale.md\n+stale")));
                await Reject<WorkspaceException>(() => runtime.Execution!.ApplyPatchAsync(Patch("*** Add File: readonly.md\n+no")));
                Assert(!Directory.Exists(next.RecoveryDirectory) && !File.Exists(fixture.PathOf("stale.md")) &&
                    (await fixture.Load(committed.Commit.ChangeId)).Manifest.RunId == before.RunId, "Old calls cannot revive a session; old records remain associated with their actual run");
            }
            finally { release.TrySetResult(); await Drain(call); }
        });

        await check("runtime stop waits for cancelled backup cleanup and cancels a queued actual write before disposing the session", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            var entered = Signal(); var release = Signal();
            await using var runtime = fixture.Runtime(prepare: async (stage, _) =>
            {
                if (stage == RecoveryPreparationStage.BackupWritten) { entered.TrySetResult(); await release.Task; }
            });
            await runtime.StartAsync(fixture.Configuration);
            var execution = runtime.Execution!;
            var original = fixture.ProjectSnapshot();
            var call = execution.ApplyPatchAsync(Patch("*** Add File: nested/new.md\n+new\n*** Update File: sample.md\n@@\n-old\n+new"));
            Task<PatchApplyResult>? queued = null;
            Task? stopping = null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                queued = execution.ApplyPatchAsync(Patch("*** Add File: queued/new.md\n+new"));
                Assert(execution.ActiveCalls == 2 && !queued.IsCompleted, "Actual requests share one mutation lock");
                stopping = runtime.StopAsync();
                await Reject<OperationCanceledException>(() => queued).WaitAsync(TimeSpan.FromSeconds(5));
                Assert(!stopping.IsCompleted && !call.IsCompleted && !execution.IsDisposed && execution.ActiveCalls == 1, "Stop owns cleanup until active preparation releases its files");
                release.TrySetResult();
                await Reject<OperationCanceledException>(() => call);
                await stopping;
                Assert(execution.IsDisposed && execution.ActiveCalls == 0 && fixture.RecordIds().Length == 0 && original.SequenceEqual(fixture.ProjectSnapshot()), "Cleanup and drain finish before host disposal; neither write installs a file");
            }
            finally { release.TrySetResult(); await Drain(call); if (queued is not null) await Drain(queued); if (stopping is not null) await Drain(stopping); }
        });

        await check("runtime stop waits for actual mid-install rollback and its final manifest instead of reporting caller cancellation", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            fixture.Put("sample.md", "\ufeffold\r\n");
            var installed = Signal(); var failInstall = Signal(); var finalizing = Signal(); var saveFinal = Signal();
            var failure = new IOException("Injected mid-install failure after stop.");
            await using var runtime = fixture.Runtime(async (stage, index) =>
            {
                if (stage == PatchCommitStage.Installed && index == 0) { installed.TrySetResult(); await failInstall.Task; throw failure; }
            }, replace: async (stage, path) =>
            {
                if (stage == RecoveryManifestReplacementStage.TemporaryWritten && RecoveryManifestJson.Parse(File.ReadAllBytes(path)).State == RecoveryRecordState.RolledBack)
                { finalizing.TrySetResult(); await saveFinal.Task; }
            });
            await runtime.StartAsync(fixture.Configuration);
            var execution = runtime.Execution!;
            var original = fixture.ProjectSnapshot();
            var call = execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new\n*** Add File: nested/new.md\n+new"));
            Task? stopping = null;
            try
            {
                await installed.Task.WaitAsync(TimeSpan.FromSeconds(15));
                stopping = runtime.StopAsync();
                Assert(!stopping.IsCompleted && !call.IsCompleted, "Stop waits while the engine still owns an installed prefix");
                failInstall.TrySetResult();
                await finalizing.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Assert(!stopping.IsCompleted && !call.IsCompleted && original.SequenceEqual(fixture.ProjectSnapshot()) &&
                    fixture.ReadManifest().State == RecoveryRecordState.Applying && execution.ActiveCalls == 1, "Rollback restores bytes, but stopping still waits for final metadata");
                saveFinal.TrySetResult();
                var error = await Reject<RecordedPatchExecutionException>(() => call);
                await stopping;
                Assert(ReferenceEquals(error.Commit.Outcome.Failure, failure) && error.Commit.Outcome.VerifiedState == PatchCommitState.RolledBack &&
                    (await fixture.Load(error.Commit.Record.Manifest.ChangeId)).Manifest.State == RecoveryRecordState.RolledBack && execution.IsDisposed &&
                    runtime.Snapshot.State == RuntimeState.Stopped, "Real engine failure and verified rollback survive session cancellation and host drain");
            }
            finally { failInstall.TrySetResult(); saveFinal.TrySetResult(); await Drain(call); if (stopping is not null) await Drain(stopping); }
        });
    }

    private static void Assert(bool condition, string message) => PatchExecutionFixture.Assert(condition, message);
}
