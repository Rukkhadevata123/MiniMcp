using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MiniMcp.Configuration;
using MiniMcp.Recovery;
using MiniMcp.Services;

internal static class RecoveryCompletionChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("completion proposals use bound mixed commit observations and actual new-file permissions without writing final state", async () =>
        {
            using var fixture = new Fixture();
            fixture.Put("sample.md", "\ufeffold\r\n");
            fixture.Put("delete.md", "delete\n");
            fixture.Put("move.md", "move\n");
            var plan = await fixture.Plan("*** Update File: sample.md\n@@\n-old\n+new\n*** Delete File: delete.md\n" +
                "*** Update File: move.md\n*** Move to: moved.md\n*** Add File: empty.md");
            var store = fixture.Store();
            var record = await store.PrepareAsync(plan);
            RejectSync<InvalidOperationException>(() => _ = record.ExecutionContext);
            await store.BeginApplyingAsync(record, plan);
            var journal = fixture.JournalSnapshot();
            var context = record.ExecutionContext;
            var outcome = await fixture.Service.CommitWithContextAsync(plan, context);
            fixture.Service.ValidateOutcome(plan, outcome, context);
            Assert(outcome.State == PatchCommitState.Committed && outcome.VerifiedState == PatchCommitState.Committed &&
                outcome.AttemptId == context.AttemptId && outcome.Paths.Length == 5, "Complete engine facts match the applying attempt");
            var completed = RecoveryCompletionBuilder.Create(record, plan, outcome);
            RecoveryManifestValidator.Validate(completed, fixture.Root, record.Manifest.ChangeId);
            Assert(completed.State == RecoveryRecordState.Committed && completed.Paths.All(path => path.After?.UnixModeIsCreateRequest != true), "Only observed final permissions are proposed");
            Assert(outcome.Paths.Single(path => path.Path == "sample.md").File!.Bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("\ufeffnew\r\n")) &&
                outcome.Paths.Single(path => path.Path == "empty.md").File!.Bytes.IsEmpty, "Original byte representation and empty-file existence observed");
            Assert(outcome.Paths.Single(path => path.Path == "move.md").File is null && outcome.Paths.Single(path => path.Path == "delete.md").File is null,
                "Move source and deletion absence verified");
            Assert(journal.SequenceEqual(fixture.JournalSnapshot()) && record.Manifest.State == RecoveryRecordState.Applying, "Proposal construction neither writes final state nor advances live journal state");
            await store.RevalidatePreparedAsync(record, plan);
        });

        await check("verified outcomes reject another plan, service, applying context and fabricated issuer", async () =>
        {
            using var fixture = new Fixture();
            var store = fixture.Store();
            var plan = await fixture.Plan("*** Add File: new.md\n+new");
            var record = await store.PrepareAsync(plan);
            var peer = await store.PrepareAsync(plan);
            await store.BeginApplyingAsync(record, plan);
            await store.BeginApplyingAsync(peer, plan);
            var outcome = await fixture.Service.CommitWithContextAsync(plan, record.ExecutionContext);
            RejectSync<InvalidOperationException>(() => RecoveryCompletionBuilder.Create(peer, plan, outcome));
            RejectSync<InvalidOperationException>(() => fixture.Service.ValidateOutcome(new PatchPlan(plan.Owner, plan.Changes), outcome));
            RejectSync<InvalidOperationException>(() => new PatchService(fixture.Workspace).ValidateOutcome(plan, outcome));
            var issuer = new object();
            var fabricated = new PatchCommitOutcome(plan, issuer, record.ExecutionContext, PatchCommitState.Committed, null, [], [])
                .WithVerification(issuer, outcome.Paths, null);
            RejectSync<InvalidOperationException>(() => RecoveryCompletionBuilder.Create(record, plan, fabricated));
            RejectSync<NotSupportedException>(() => JsonSerializer.Deserialize<PatchCommitOutcome>("{\"state\":\"Committed\"}"));
            RejectSync<ArgumentNullException>(() => RecoveryCompletionBuilder.Create(null!, plan, outcome));
            RejectSync<ArgumentNullException>(() => RecoveryCompletionBuilder.Create(record, null!, outcome));
            RejectSync<ArgumentNullException>(() => RecoveryCompletionBuilder.Create(record, plan, null!));
            Assert(record.ExecutionContext.AttemptId != peer.ExecutionContext.AttemptId && record.Manifest.State == RecoveryRecordState.Applying, "Different records cannot replay one execution");
        });

        await check("an unbound engine success cannot finalize an applying recovery record", async () =>
        {
            using var fixture = new Fixture();
            var plan = await fixture.Plan();
            var store = fixture.Store();
            var record = await store.PrepareAsync(plan);
            await store.BeginApplyingAsync(record, plan);
            var result = await fixture.Service.CommitWithOutcomeAsync(plan);
            Assert(result.VerifiedState == PatchCommitState.Committed, "Engine succeeded and observed its output");
            fixture.Service.ValidateOutcome(plan, result);
            RejectSync<InvalidOperationException>(() => RecoveryCompletionBuilder.Create(record, plan, result));
        });

        await check("rollback observations retain the original failure and allow only one execution per applying context", async () =>
        {
            var failure = new IOException("Injected installation failure.");
            using var fixture = new Fixture((stage, index) => stage == PatchCommitStage.Installed && index == 0 ? Task.FromException(failure) : Task.CompletedTask);
            fixture.Put("sample.md", "\ufeffold\r\n");
            var plan = await fixture.Plan("*** Update File: sample.md\n@@\n-old\n+new\n*** Add File: nested/new.md\n+new");
            var store = fixture.Store();
            var record = await store.PrepareAsync(plan);
            await store.BeginApplyingAsync(record, plan);
            var outcome = await fixture.Service.CommitWithContextAsync(plan, record.ExecutionContext);
            Assert(outcome.State == PatchCommitState.RolledBack && outcome.VerifiedState == PatchCommitState.RolledBack && ReferenceEquals(outcome.Failure, failure), "Rollback checked without losing original error");
            Assert(outcome.Paths.Single(path => path.Path == "sample.md").File!.Bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("\ufeffold\r\n")) &&
                outcome.Paths.Single(path => path.Path == "nested/new.md").File is null, "Restored and absent paths observed");
            var completed = RecoveryCompletionBuilder.Create(record, plan, outcome);
            Assert(completed.State == RecoveryRecordState.RolledBack && record.Manifest.State == RecoveryRecordState.Applying, "Pure rollback proposal only");
            var before = fixture.Snapshot();
            await Reject<InvalidOperationException>(() => fixture.Service.CommitWithContextAsync(plan, record.ExecutionContext));
            Assert(before.SequenceEqual(fixture.Snapshot()), "Consumed context rejected before staging");
        });

        await check("incomplete rollback produces an incomplete proposal without claiming planned after-images were observed", async () =>
        {
            Fixture? fixtureRef = null;
            using var fixture = new Fixture((stage, index) =>
            {
                if (stage != PatchCommitStage.Installed || index != 0) return Task.CompletedTask;
                File.Delete(Path.Combine(fixtureRef!.Root, "sample.md"));
                Directory.CreateDirectory(Path.Combine(fixtureRef.Root, "sample.md"));
                return Task.FromException(new IOException("Injected failed rollback."));
            });
            fixtureRef = fixture;
            var plan = await fixture.Plan();
            var store = fixture.Store();
            var record = await store.PrepareAsync(plan);
            await store.BeginApplyingAsync(record, plan);
            var outcome = await fixture.Service.CommitWithContextAsync(plan, record.ExecutionContext);
            Assert(outcome.State == PatchCommitState.Incomplete && outcome.VerifiedState == PatchCommitState.Incomplete && outcome.Paths.IsEmpty && !outcome.RollbackErrors.IsEmpty,
                "No invented complete snapshot for partial rollback");
            Assert(RecoveryCompletionBuilder.Create(record, plan, outcome).State == RecoveryRecordState.Incomplete &&
                fixture.Backups(record).Single().Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("old\n")), StringComparison.Ordinal), "Original journal backup remains available");
        });

        await check("post-install verification failure retains engine success facts but cannot create a committed proposal or legacy success", async () =>
        {
            foreach (var legacy in new[] { false, true })
            {
                Fixture? fixtureRef = null;
                using var fixture = new Fixture((stage, _) =>
                {
                    if (stage == PatchCommitStage.BeforeVerification) fixtureRef!.Put("sample.md", "external\n");
                    return Task.CompletedTask;
                });
                fixtureRef = fixture;
                var plan = await fixture.Plan();
                var store = fixture.Store();
                var record = await store.PrepareAsync(plan);
                await store.BeginApplyingAsync(record, plan);
                if (legacy) await Reject<WorkspaceException>(() => fixture.Service.CommitAsync(plan));
                else
                {
                    var outcome = await fixture.Service.CommitWithContextAsync(plan, record.ExecutionContext);
                    Assert(outcome.State == PatchCommitState.Committed && outcome.VerifiedState == PatchCommitState.Incomplete && outcome.Failure is null &&
                        outcome.VerificationFailure is not null && outcome.Paths.IsEmpty, "Installation and failed observation kept distinct");
                    Assert(RecoveryCompletionBuilder.Create(record, plan, outcome).State == RecoveryRecordState.Incomplete, "Failed output check cannot become Committed");
                }
                Assert(File.ReadAllText(Path.Combine(fixture.Root, "sample.md")) == "external\n", "Verification never starts a second rollback or overwrites external changes");
            }
        });

        await check("failed rollback observation cannot be promoted to a completed rollback", async () =>
        {
            Fixture? fixtureRef = null;
            var failure = new IOException("Original engine error.");
            using var fixture = new Fixture((stage, index) =>
            {
                if (stage == PatchCommitStage.Installed && index == 0) return Task.FromException(failure);
                if (stage == PatchCommitStage.BeforeVerification) fixtureRef!.Put("sample.md", "later change\n");
                return Task.CompletedTask;
            });
            fixtureRef = fixture;
            var plan = await fixture.Plan();
            var store = fixture.Store();
            var record = await store.PrepareAsync(plan);
            await store.BeginApplyingAsync(record, plan);
            var outcome = await fixture.Service.CommitWithContextAsync(plan, record.ExecutionContext);
            Assert(outcome.State == PatchCommitState.RolledBack && outcome.VerifiedState == PatchCommitState.Incomplete && ReferenceEquals(outcome.Failure, failure) && outcome.VerificationFailure is not null,
                "Engine rollback and post-rollback mismatch reported separately");
            Assert(RecoveryCompletionBuilder.Create(record, plan, outcome).State == RecoveryRecordState.Incomplete &&
                File.ReadAllText(Path.Combine(fixture.Root, "sample.md")) == "later change\n", "No false completed rollback or extra writes");
        });

        await check("outcome observation retains the workspace lock, ignores late cancellation and captures immutable historical facts", async () =>
        {
            using var cancelled = new CancellationTokenSource();
            Fixture? fixtureRef = null;
            using var fixture = new Fixture((stage, _) =>
            {
                if (stage == PatchCommitStage.BeforeVerification)
                {
                    Assert(fixtureRef!.Workspace.Mutations.CurrentCount == 0, "Same workspace lock still held during observation");
                    cancelled.Cancel();
                }
                return Task.CompletedTask;
            });
            fixtureRef = fixture;
            var plan = await fixture.Plan();
            var store = fixture.Store();
            var record = await store.PrepareAsync(plan);
            await store.BeginApplyingAsync(record, plan);
            var outcome = await fixture.Service.CommitWithContextAsync(plan, record.ExecutionContext, cancelled.Token);
            Assert(cancelled.IsCancellationRequested && outcome.VerifiedState == PatchCommitState.Committed && fixture.Workspace.Mutations.CurrentCount == 1, "Late cancellation does not replace committed observation");
            var observed = outcome.Paths.Single().File!;
            var copied = observed.Bytes.ToArray();
            copied[0] = (byte)'X';
            fixture.Put("sample.md", "changed after return\n");
            Assert(observed.Bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("new\n")), "Observation owns immutable original bytes");
            Assert(RecoveryCompletionBuilder.Create(record, plan, outcome).State == RecoveryRecordState.Committed, "Proposal uses observed historical result; later restore must check current files separately");
        });

        await check("a revoked applying context cannot execute a plan or be replaced by another plan context", async () =>
        {
            using var fixture = new Fixture();
            var plan = await fixture.Plan();
            var otherPlan = await fixture.Plan("*** Add File: unrelated.md\n+new");
            var store = fixture.Store();
            var record = await store.PrepareAsync(plan);
            await store.BeginApplyingAsync(record, plan);
            var context = record.ExecutionContext;
            var before = fixture.Snapshot();
            await Reject<InvalidOperationException>(() => fixture.Service.CommitWithContextAsync(otherPlan, context));
            Assert(before.SequenceEqual(fixture.Snapshot()), "Wrong plan did not consume context or write files");
            record.Invalidate();
            await Reject<InvalidOperationException>(() => fixture.Service.CommitWithContextAsync(plan, context));
            Assert(before.SequenceEqual(fixture.Snapshot()), "Revoked context rejected before engine staging");
        });

        await check("an applying attempt rejected before staging carries bound evidence without claiming external changes were rolled back", async () =>
        {
            using var fixture = new Fixture();
            var plan = await fixture.Plan("*** Add File: nested/new.md\n+new\n*** Update File: sample.md\n@@\n-old\n+new");
            var store = fixture.Store();
            var record = await store.PrepareAsync(plan);
            await store.BeginApplyingAsync(record, plan);
            fixture.Put("sample.md", "external before engine\n");
            var before = fixture.Snapshot();
            var result = await fixture.Service.CommitWithContextAsync(plan, record.ExecutionContext);
            Assert(result.State == PatchCommitState.RolledBack && result.Failure is WorkspaceException && result.VerifiedState == PatchCommitState.Incomplete && result.VerificationFailure is not null,
                "Declined attempt retains both validation error and current-state mismatch");
            Assert(RecoveryCompletionBuilder.Create(record, plan, result).State == RecoveryRecordState.Incomplete && before.SequenceEqual(fixture.Snapshot()) && !Directory.Exists(Path.Combine(fixture.Root, "nested")),
                "No staging or false rollback of external edits");
        });

        await check("post-commit links fail observation without following or modifying their target", async () =>
        {
            Fixture? fixtureRef = null;
            using var fixture = new Fixture((stage, _) =>
            {
                if (stage != PatchCommitStage.BeforeVerification) return Task.CompletedTask;
                var path = Path.Combine(fixtureRef!.Root, "sample.md");
                File.Delete(path);
                File.CreateSymbolicLink(path, Path.Combine(fixtureRef.DirectoryPath, "outside.md"));
                return Task.CompletedTask;
            });
            fixtureRef = fixture;
            var target = Path.Combine(fixture.DirectoryPath, "outside.md");
            File.WriteAllText(target, "outside data");
            var probe = Path.Combine(fixture.Root, "probe");
            try { File.CreateSymbolicLink(probe, target); }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { Console.WriteLine("SKIP commit observation link check: symbolic link permission unavailable."); return; }
            File.Delete(probe);
            var plan = await fixture.Plan();
            var result = await fixture.Service.CommitWithOutcomeAsync(plan);
            Assert(result.State == PatchCommitState.Committed && result.VerifiedState == PatchCommitState.Incomplete && result.VerificationFailure is not null && File.ReadAllText(target) == "outside data",
                "Link rejected before reading target, installation history retained");
            File.Delete(Path.Combine(fixture.Root, "sample.md"));
        });

        if (OperatingSystem.IsLinux())
            await check("post-commit FIFO replacements are rejected before blocking file reads", async () =>
            {
                Fixture? fixtureRef = null;
                using var fixture = new Fixture((stage, _) =>
                {
                    if (stage != PatchCommitStage.BeforeVerification) return Task.CompletedTask;
                    var path = Path.Combine(fixtureRef!.Root, "sample.md");
                    File.Delete(path);
                    Assert(MakeFifo(path, 0x180) == 0, "FIFO created");
                    return Task.CompletedTask;
                });
                fixtureRef = fixture;
                var result = await fixture.Service.CommitWithOutcomeAsync(await fixture.Plan()).WaitAsync(TimeSpan.FromSeconds(3));
                Assert(result.State == PatchCommitState.Committed && result.VerifiedState == PatchCommitState.Incomplete, "No blocking FIFO read or false success");
            });
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception("Recovery completion: " + message); }
    private static void RejectSync<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
    private static async Task<T> Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new Exception($"Expected {typeof(T).Name}."); }
    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"minimcp-completion-{Guid.NewGuid():N}");
        public string Root { get; }
        public string Storage => Path.Combine(DirectoryPath, "archive", "recovery");
        public ProjectWorkspace Workspace { get; }
        public PatchService Service { get; }
        private readonly ConfigurationPaths _paths;
        private readonly string _bash;
        public Fixture(Func<PatchCommitStage, int, Task>? checkpoint = null)
        {
            Root = Directory.CreateDirectory(Path.Combine(DirectoryPath, "project")).FullName;
            Put("sample.md", "old\n");
            Workspace = new ProjectWorkspace(Root);
            Service = new PatchService(Workspace, checkpoint);
            _paths = new ConfigurationPaths(Path.Combine(DirectoryPath, "user"), Path.Combine(DirectoryPath, "state"));
            _bash = Path.Combine(DirectoryPath, "bash-fixture");
            File.WriteAllText(_bash, "Dependency fixture; never execute.");
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(_bash, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
        public RecoveryStore Store() => new(Guid.NewGuid(), new ConfigurationValidator(_paths).Validate(new AppSettings { ProjectRoot = Root, BashPath = _bash,
            Authentication = new AuthenticationSettings { Mode = AuthenticationMode.Local } }, Path.Combine(DirectoryPath, "settings.json"),
            new ExecutionPolicy { Mode = ExecutionMode.FullAccess, RecoveryDirectory = Storage }));
        public Task<PatchPlan> Plan(string body = "*** Update File: sample.md\n@@\n-old\n+new") => Service.PrepareAsync("*** Begin Patch\n" + body + "\n*** End Patch");
        public void Put(string path, string text) => File.WriteAllText(Path.Combine(Root, path), text, new UTF8Encoding(false));
        public string[] Backups(RecoveryStore.PreparedRecord record) => Directory.EnumerateFiles(record.DirectoryPath, "*").Where(path => Path.GetFileName(path).StartsWith("before-", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(path => Path.GetFileName(path) + ":" + Convert.ToBase64String(File.ReadAllBytes(path))).ToArray();
        public string[] JournalSnapshot() => Directory.EnumerateFiles(Storage, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(Storage, path) + ":" + Convert.ToBase64String(File.ReadAllBytes(path))).ToArray();
        public string[] Snapshot() => Directory.EnumerateFileSystemEntries(DirectoryPath, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(DirectoryPath, path) + ":" + (Directory.Exists(path) ? "directory" : Convert.ToBase64String(File.ReadAllBytes(path)))).ToArray();
        public void Dispose() { Workspace.Mutations.Dispose(); Directory.Delete(DirectoryPath, recursive: true); }
    }
}
