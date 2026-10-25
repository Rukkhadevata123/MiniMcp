using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MiniMcp.Recovery;
using MiniMcp.Services;
using Fixture = RecordedPatchCommitChecks.Fixture;

internal static class RecordedPatchFaultChecks
{
    private const string MixedPatch = "*** Add File: nested/new.md\n+added\n*** Update File: sample.md\n@@\n-old\n+new\n" +
        "*** Delete File: delete.md\n*** Update File: move.md\n*** Move to: nested/moved.md\n@@\n-move\n+moved\n*** Add File: empty.md";

    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("recorded fault matrix rolls mixed operations back at staging and every installation boundary", async () =>
        {
            var boundaries = new[] { (PatchCommitStage.Staged, -1) }.Concat(Enumerable.Range(0, 5)
                .SelectMany(index => new[] { (PatchCommitStage.BeforeInstall, index), (PatchCommitStage.Installed, index) }));
            foreach (var boundary in boundaries)
            {
                using var cancelled = new CancellationTokenSource();
                var failure = new IOException("Injected mixed installation failure.");
                using var fixture = new Fixture((stage, index) =>
                {
                    if ((stage, index) != boundary) return Task.CompletedTask;
                    cancelled.Cancel();
                    return Task.FromException(failure);
                });
                SeedMixed(fixture);
                var original = fixture.ProjectSnapshot();
                var store = fixture.Store();
                var result = await fixture.Service.CommitWithRecoveryAsync(await fixture.Plan(MixedPatch), store, cancelled.Token);
                Assert(result.Outcome.VerifiedState == PatchCommitState.RolledBack && ReferenceEquals(result.Outcome.Failure, failure) &&
                    result.Outcome.RollbackErrors.IsEmpty && result.Outcome.CleanupErrors.IsEmpty && original.SequenceEqual(fixture.ProjectSnapshot()), "Every partial prefix restores exact original bytes, directories and modes");
                await VerifyAsync(store, result);
                Assert(!fixture.Artifacts().Any(), "All owned staging and rollback files cleaned");
            }
        });

        await check("recorded preparation cancellation at every checkpoint preserves older records and writes no workspace files", async () =>
        {
            foreach (var boundary in Enum.GetValues<RecoveryPreparationStage>())
            {
                using var cancelled = new CancellationTokenSource();
                using var fixture = new Fixture();
                SeedMixed(fixture);
                var plan = await fixture.Plan(MixedPatch);
                var older = await fixture.Store().PrepareAsync(plan);
                var journal = JournalSnapshot(fixture);
                var original = fixture.ProjectSnapshot();
                var store = fixture.Store(prepare: (stage, _) => { if (stage == boundary) cancelled.Cancel(); return Task.CompletedTask; });
                await Reject<OperationCanceledException>(() => fixture.Service.CommitWithRecoveryAsync(plan, store, cancelled.Token));
                Assert(original.SequenceEqual(fixture.ProjectSnapshot()) && journal.SequenceEqual(JournalSnapshot(fixture)) &&
                    fixture.Workspace.Mutations.CurrentCount == 1 && older.Manifest.State == RecoveryRecordState.Prepared, "Only this cancelled preparation is cleaned; older backup and workspace untouched");
            }
        });

        await check("recorded Applying cancellation matrix distinguishes unattempted replacement from a verified Applying attempt", async () =>
        {
            foreach (var boundary in Enum.GetValues<RecoveryManifestReplacementStage>())
            {
                using var cancelled = new CancellationTokenSource();
                using var fixture = new Fixture();
                var store = fixture.Store(replacement: (stage, _) => { if (stage == boundary) cancelled.Cancel(); return Task.CompletedTask; });
                var original = fixture.ProjectSnapshot();
                var plan = await fixture.Plan();
                if (boundary is RecoveryManifestReplacementStage.Replaced or RecoveryManifestReplacementStage.BeforeReturn)
                {
                    var result = await fixture.Service.CommitWithRecoveryAsync(plan, store, cancelled.Token);
                    Assert(result.Outcome.VerifiedState == PatchCommitState.RolledBack && result.Outcome.Failure is OperationCanceledException, "Verified Applying gets a bound no-install result despite cancellation");
                    await VerifyAsync(store, result);
                }
                else
                {
                    await Reject<OperationCanceledException>(() => fixture.Service.CommitWithRecoveryAsync(plan, store, cancelled.Token));
                    Assert(fixture.ReadManifest().State == RecoveryRecordState.Prepared &&
                        (await store.LoadAsync(fixture.ReadManifest().ChangeId)).Backups.Length == 1, "Cancellation before replacement leaves the original Prepared record");
                }
                Assert(original.SequenceEqual(fixture.ProjectSnapshot()) && !fixture.Artifacts().Any(), "No workspace staging or metadata orphan remains");
            }
        });

        await check("recorded staging cancellation removes all mixed staging before saving RolledBack", async () =>
        {
            using var cancelled = new CancellationTokenSource();
            using var fixture = new Fixture((stage, _) => { if (stage == PatchCommitStage.Staged) cancelled.Cancel(); return Task.CompletedTask; });
            SeedMixed(fixture);
            var original = fixture.ProjectSnapshot();
            var store = fixture.Store();
            var result = await fixture.Service.CommitWithRecoveryAsync(await fixture.Plan(MixedPatch), store, cancelled.Token);
            Assert(result.Outcome.Failure is OperationCanceledException && result.Outcome.VerifiedState == PatchCommitState.RolledBack &&
                original.SequenceEqual(fixture.ProjectSnapshot()) && !fixture.Artifacts().Any(), "Cancellation finishes staging cleanup and exact original-state observation");
            await VerifyAsync(store, result);
        });

        await check("recorded late cancellation at every installation boundary commits the whole mixed patch and final metadata", async () =>
        {
            var boundaries = Enumerable.Range(0, 5).SelectMany(index => new[] { (PatchCommitStage.BeforeInstall, index), (PatchCommitStage.Installed, index) })
                .Append((PatchCommitStage.BeforeVerification, -1));
            foreach (var boundary in boundaries)
            {
                using var cancelled = new CancellationTokenSource();
                using var fixture = new Fixture((stage, index) => { if ((stage, index) == boundary) cancelled.Cancel(); return Task.CompletedTask; });
                SeedMixed(fixture);
                var store = fixture.Store();
                var result = await fixture.Service.CommitWithRecoveryAsync(await fixture.Plan(MixedPatch), store, cancelled.Token);
                Assert(cancelled.IsCancellationRequested && result.Outcome.VerifiedState == PatchCommitState.Committed && result.Outcome.Failure is null &&
                    fixture.Get("sample.md") == "new\r\nlocal\nlast" && fixture.Get("nested/moved.md") == "moved\n" &&
                    fixture.Get("nested/new.md") == "added\n" && !File.Exists(fixture.Path("delete.md")) && !File.Exists(fixture.Path("move.md")) &&
                    File.ReadAllBytes(fixture.Path("empty.md")).Length == 0, "Every operation completes after the installation boundary");
                await VerifyAsync(store, result);
            }
        });

        await check("recorded quota checks reject a full store without workspace writes and allow the actual last record slot", async () =>
        {
            foreach (var slots in new[] { 1000, 999 })
            {
                var engineCalls = 0;
                using var fixture = new Fixture((_, _) => { engineCalls++; return Task.CompletedTask; });
                AddSlots(fixture, slots);
                File.WriteAllText(Path.Combine(fixture.Storage, "foreign-0", "keep"), "operator data");
                var original = fixture.ProjectSnapshot();
                var store = fixture.Store();
                var plan = await fixture.Plan("*** Add File: nested/new.md\n+new\n*** Update File: sample.md\n@@\n-old\n+new");
                if (slots == 1000)
                {
                    var journal = JournalSnapshot(fixture);
                    await Reject<IOException>(() => fixture.Service.CommitWithRecoveryAsync(plan, store));
                    Assert(engineCalls == 0 && original.SequenceEqual(fixture.ProjectSnapshot()) && journal.SequenceEqual(JournalSnapshot(fixture)), "Quota rejection creates no lock, record or workspace staging");
                }
                else await VerifyAsync(store, await fixture.Service.CommitWithRecoveryAsync(plan, store));
                Assert(Directory.EnumerateDirectories(fixture.Storage).Count() == 1000 && File.ReadAllText(Path.Combine(fixture.Storage, "foreign-0", "keep")) == "operator data", "No old record evicted or changed");
            }
        });

        await check("quota growth during Applying rejects installation while growth after installation retains its committed outcome", async () =>
        {
            foreach (var beforeEngine in new[] { true, false })
            {
                Fixture? fixtureRef = null;
                using var fixture = new Fixture((stage, _) =>
                {
                    if (!beforeEngine && stage == PatchCommitStage.BeforeVerification) AddSlots(fixtureRef!, 1000);
                    return Task.CompletedTask;
                });
                fixtureRef = fixture;
                var armed = true;
                var store = fixture.Store(replacement: (stage, _) =>
                {
                    if (beforeEngine && armed && stage == RecoveryManifestReplacementStage.BeforeTemporaryFile) { armed = false; AddSlots(fixture, 1000); }
                    return Task.CompletedTask;
                });
                var plan = await fixture.Plan();
                var original = fixture.ProjectSnapshot();
                if (beforeEngine)
                {
                    await Reject<IOException>(() => fixture.Service.CommitWithRecoveryAsync(plan, store));
                    Assert(original.SequenceEqual(fixture.ProjectSnapshot()) && fixture.ReadManifest().State == RecoveryRecordState.Prepared, "No workspace writes before verified Applying");
                }
                else
                {
                    var error = await Reject<RecoveryFinalizationException>(() => fixture.Service.CommitWithRecoveryAsync(plan, store));
                    Assert(error.Outcome.VerifiedState == PatchCommitState.Committed && fixture.Get("sample.md") == "new\n" && error.Record.Manifest.State == RecoveryRecordState.Applying, "Late quota error cannot become a false rollback");
                }
                Assert(Directory.EnumerateDirectories(fixture.Storage).Count() == 1001 &&
                    (await store.LoadAsync(fixture.ReadManifest().ChangeId)).Backups.Single().Bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("old\n")), "Unknown slots and original bytes preserved even above quota");
            }
        });

        await check("real storage locks block preparation and report post-install finalization failure without losing engine facts", async () =>
        {
            foreach (var beforeEngine in new[] { true, false })
            {
                Fixture? fixtureRef = null;
                FileStream? held = null;
                using var fixture = new Fixture((stage, _) =>
                {
                    if (!beforeEngine && stage == PatchCommitStage.BeforeVerification)
                    {
                        held = RecoveryFileSystem.OpenRead(Path.Combine(fixtureRef!.Storage, RecoveryStorageLimits.LockFileName));
                        RecoveryFileSystem.Lock(held);
                    }
                    return Task.CompletedTask;
                });
                fixtureRef = fixture;
                var store = fixture.Store();
                var plan = await fixture.Plan();
                var original = fixture.ProjectSnapshot();
                try
                {
                    if (beforeEngine)
                    {
                        Directory.CreateDirectory(fixture.Storage);
                        held = RecoveryFileSystem.OpenFile(Path.Combine(fixture.Storage, RecoveryStorageLimits.LockFileName), FileMode.OpenOrCreate);
                        RecoveryFileSystem.Lock(held);
                        await Reject<IOException>(() => fixture.Service.CommitWithRecoveryAsync(plan, store));
                        Assert(original.SequenceEqual(fixture.ProjectSnapshot()) && !fixture.RecordDirectories().Any(), "Busy storage blocks all workspace writes");
                    }
                    else
                    {
                        var error = await Reject<RecoveryFinalizationException>(() => fixture.Service.CommitWithRecoveryAsync(plan, store));
                        Assert(error.Outcome.VerifiedState == PatchCommitState.Committed && fixture.Get("sample.md") == "new\n" && !error.Record.IsInvalidated, "Busy final save preserves a retryable record and actual install");
                        held!.Dispose(); held = null;
                        await store.CompleteAsync(error.Record, plan, error.Outcome);
                        await VerifyAsync(store, new(error.Record, error.Outcome));
                    }
                }
                finally { held?.Dispose(); }
            }
        });

        await check("prepared state, backup and identity tampering are rejected before staging without overwriting foreign metadata", async () =>
        {
            foreach (var tamper in new[] { "state", "backup", "identity" })
            {
                var engineCalls = 0;
                using var fixture = new Fixture((_, _) => { engineCalls++; return Task.CompletedTask; });
                string[]? foreign = null;
                var store = fixture.Store(replacement: (stage, _) =>
                {
                    if (stage != RecoveryManifestReplacementStage.BeforeTemporaryFile) return Task.CompletedTask;
                    if (tamper == "state") File.WriteAllBytes(fixture.ManifestPath, RecoveryManifestJson.Serialize(fixture.ReadManifest() with { State = RecoveryRecordState.Incomplete }));
                    else if (tamper == "backup") File.WriteAllText(Path.Combine(fixture.RecordDirectories().Single(), "before-0000.bin"), "bad\n");
                    else
                    {
                        var bytes = File.ReadAllBytes(fixture.ManifestPath);
                        File.Move(fixture.ManifestPath, Path.Combine(fixture.DirectoryPath, "detached-manifest"));
                        File.WriteAllBytes(fixture.ManifestPath, bytes);
                    }
                    foreign = JournalSnapshot(fixture);
                    return Task.CompletedTask;
                });
                var original = fixture.ProjectSnapshot();
                var plan = await fixture.Plan("*** Add File: nested/new.md\n+new\n*** Update File: sample.md\n@@\n-old\n+new");
                await RejectInvalid(() => fixture.Service.CommitWithRecoveryAsync(plan, store));
                Assert(engineCalls == 0 && original.SequenceEqual(fixture.ProjectSnapshot()) && foreign is not null && foreign.SequenceEqual(JournalSnapshot(fixture)),
                    "Corrupt or replaced metadata cannot authorize staging and is not silently repaired");
            }
        });

        await check("incomplete preparation cleanup preserves foreign recovery entries and leaves the workspace untouched", async () =>
        {
            using var fixture = new Fixture();
            string? foreign = null;
            var failure = new IOException("Injected preparation failure with foreign occupancy.");
            var store = fixture.Store(prepare: (stage, draft) =>
            {
                if (stage != RecoveryPreparationStage.BeforeManifest) return Task.CompletedTask;
                foreign = Path.Combine(fixture.Storage, draft.Manifest.ChangeId.ToString("N"), "operator.txt");
                File.WriteAllText(foreign, "keep");
                return Task.FromException(failure);
            });
            var original = fixture.ProjectSnapshot();
            var plan = await fixture.Plan();
            var error = await Reject<AggregateException>(() => fixture.Service.CommitWithRecoveryAsync(plan, store));
            Assert(error.InnerExceptions.Any(error => ReferenceEquals(error, failure)) && error.InnerExceptions.Count > 1 &&
                original.SequenceEqual(fixture.ProjectSnapshot()) && File.ReadAllText(foreign!) == "keep" &&
                !File.Exists(Path.Combine(Path.GetDirectoryName(foreign!)!, RecoveryStore.ManifestFileName)), "Partial record and foreign data preserved; no workspace staging");
        });

        await check("incomplete staging cleanup persists Incomplete even when no target was installed", async () =>
        {
            Fixture? fixtureRef = null;
            string? occupied = null;
            var failure = new IOException("Injected staging failure.");
            using var fixture = new Fixture((stage, _) =>
            {
                if (stage != PatchCommitStage.Staged) return Task.CompletedTask;
                occupied = fixtureRef!.Artifacts().First(path => path.EndsWith(".tmp", StringComparison.Ordinal));
                File.Move(occupied, Path.Combine(fixtureRef.DirectoryPath, "detached-stage"));
                Directory.CreateDirectory(occupied);
                return Task.FromException(failure);
            });
            fixtureRef = fixture;
            SeedMixed(fixture);
            var store = fixture.Store();
            var result = await fixture.Service.CommitWithRecoveryAsync(await fixture.Plan(MixedPatch), store);
            Assert(result.Outcome.VerifiedState == PatchCommitState.Incomplete && result.Outcome.RollbackErrors.IsEmpty &&
                !result.Outcome.CleanupErrors.IsEmpty && ReferenceEquals(result.Outcome.Failure, failure) && Directory.Exists(occupied) &&
                fixture.Get("sample.md") == "old\r\nlocal\nlast" && !File.Exists(fixture.Path("nested/new.md")), "Cleanup failure is recorded separately from installation and rollback");
            await VerifyAsync(store, result);
        });

        await check("partial mixed rollback persists Incomplete while unaffected paths are restored and every original remains backed up", async () =>
        {
            Fixture? fixtureRef = null;
            using var fixture = new Fixture((stage, index) =>
            {
                if (stage != PatchCommitStage.Installed || index != 4) return Task.CompletedTask;
                File.Delete(fixtureRef!.Path("sample.md"));
                Directory.CreateDirectory(fixtureRef.Path("sample.md"));
                return Task.FromException(new IOException("Injected rollback occupancy."));
            });
            fixtureRef = fixture;
            SeedMixed(fixture);
            var store = fixture.Store();
            var result = await fixture.Service.CommitWithRecoveryAsync(await fixture.Plan(MixedPatch), store);
            Assert(result.Outcome.VerifiedState == PatchCommitState.Incomplete && !result.Outcome.RollbackErrors.IsEmpty &&
                Directory.Exists(fixture.Path("sample.md")) && File.ReadAllBytes(fixture.Path("delete.md")).Length == 0 &&
                fixture.Get("move.md") == "move\n" && !File.Exists(fixture.Path("empty.md")) && !Directory.Exists(fixture.Path("nested")), "Only occupied source fails rollback; all other operations restore their originals");
            Assert(File.ReadAllBytes(fixture.Artifacts().Single(path => path.EndsWith(".bak", StringComparison.Ordinal))).SequenceEqual(Encoding.UTF8.GetBytes("\ufeffold\r\nlocal\nlast")), "Failed engine rollback retains its own original too");
            await VerifyAsync(store, result);
        });

        await check("successful recorded installation remains Committed when only engine backup cleanup fails", async () =>
        {
            Fixture? fixtureRef = null;
            using var fixture = new Fixture((stage, _) =>
            {
                if (stage != PatchCommitStage.Installed) return Task.CompletedTask;
                var backup = fixtureRef!.Artifacts().Single(path => path.EndsWith(".bak", StringComparison.Ordinal));
                File.Move(backup, Path.Combine(fixtureRef.DirectoryPath, "saved-engine-original"));
                Directory.CreateDirectory(backup);
                return Task.CompletedTask;
            });
            fixtureRef = fixture;
            var store = fixture.Store();
            var result = await fixture.Service.CommitWithRecoveryAsync(await fixture.Plan(), store);
            Assert(result.Outcome.VerifiedState == PatchCommitState.Committed && result.Outcome.Failure is null &&
                result.Outcome.CleanupErrors.Length == 1 && fixture.Get("sample.md") == "new\n", "Backup cleanup diagnostic cannot relabel installed files as rolled back");
            await VerifyAsync(store, result);
        });

        await check("final metadata fault matrix retains actual outcomes for all terminal states and replacement boundaries", async () =>
        {
            foreach (var terminal in Enum.GetValues<PatchCommitState>())
                foreach (var boundary in Enum.GetValues<RecoveryManifestReplacementStage>())
                {
                    Fixture? fixtureRef = null;
                    using var fixture = new Fixture((stage, _) =>
                    {
                        if (stage != PatchCommitStage.Installed || terminal == PatchCommitState.Committed) return Task.CompletedTask;
                        if (terminal == PatchCommitState.Incomplete) { File.Delete(fixtureRef!.Path("sample.md")); Directory.CreateDirectory(fixtureRef.Path("sample.md")); }
                        return Task.FromException(new IOException("Injected engine failure before final save."));
                    });
                    fixtureRef = fixture;
                    var originalMode = OperatingSystem.IsWindows() ? (UnixFileMode?)null : File.GetUnixFileMode(fixture.Path("sample.md"));
                    var finishing = false;
                    var failOnce = true;
                    Exception failure = boundary == RecoveryManifestReplacementStage.BeforeReturn ? new OperationCanceledException("Injected late final-save cancellation error.") : new IOException("Injected final metadata error.");
                    var store = fixture.Store(replacement: (stage, _) =>
                    {
                        if (stage == RecoveryManifestReplacementStage.BeforeTemporaryFile)
                        {
                            finishing = fixture.ReadManifest().State == RecoveryRecordState.Applying;
                        }
                        if (finishing && failOnce && stage == boundary) { failOnce = false; return Task.FromException(failure); }
                        return Task.CompletedTask;
                    });
                    var plan = await fixture.Plan();
                    var error = await Reject<RecoveryFinalizationException>(() => fixture.Service.CommitWithRecoveryAsync(plan, store));
                    var uncertain = boundary is RecoveryManifestReplacementStage.Replaced or RecoveryManifestReplacementStage.BeforeReturn;
                    var expected = terminal switch { PatchCommitState.Committed => RecoveryRecordState.Committed, PatchCommitState.RolledBack => RecoveryRecordState.RolledBack, _ => RecoveryRecordState.Incomplete };
                    // Check the exact operation path directly, avoiding transient enumerated names
                    // after the engine's rename sequence. Missing files and read errors still fail.
                    var actualMatches = terminal == PatchCommitState.Incomplete ? Directory.Exists(fixture.Path("sample.md")) :
                        File.ReadAllBytes(fixture.Path("sample.md")).SequenceEqual(Encoding.UTF8.GetBytes(terminal == PatchCommitState.Committed ? "new\n" : "old\n")) &&
                        (OperatingSystem.IsWindows() || File.GetUnixFileMode(fixture.Path("sample.md")) == originalMode);
                    Assert(error.Outcome.VerifiedState == terminal && error.Record.Manifest.State == RecoveryRecordState.Applying &&
                        error.Record.IsInvalidated == uncertain && actualMatches && fixture.Workspace.Mutations.CurrentCount == 1,
                        "Final-save failure retains engine facts and actual workspace without another rollback");
                    var saved = await store.LoadAsync(error.Record.Manifest.ChangeId);
                    Assert(saved.Manifest.State == (uncertain ? expected : RecoveryRecordState.Applying) &&
                        saved.Backups.Single().Bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("old\n")), "Disk state is reported honestly and original bytes always survive");
                    if (!uncertain)
                    {
                        Assert(ReferenceEquals(error.InnerException, failure), "Original pre-replace failure retained");
                        await store.CompleteAsync(error.Record, plan, error.Outcome);
                        await VerifyAsync(store, new(error.Record, error.Outcome));
                    }
                    else Assert(error.InnerException is RecoveryManifestReplacementException replacement && ReferenceEquals(replacement.InnerException, failure), "Uncertain replacement retains its original cause");
                }
        });

        await check("foreign final-metadata temporary files cause explicit incomplete cleanup and never another patch execution", async () =>
        {
            using var fixture = new Fixture();
            var finishing = false;
            string? foreign = null;
            var store = fixture.Store(replacement: (stage, path) =>
            {
                if (stage == RecoveryManifestReplacementStage.BeforeTemporaryFile) finishing = fixture.ReadManifest().State == RecoveryRecordState.Applying;
                if (!finishing || stage != RecoveryManifestReplacementStage.BeforeReplace) return Task.CompletedTask;
                foreign = path;
                var bytes = File.ReadAllBytes(path);
                File.Move(path, Path.Combine(fixture.DirectoryPath, "detached-final"));
                File.WriteAllBytes(path, bytes);
                return Task.CompletedTask;
            });
            var plan = await fixture.Plan();
            var error = await Reject<RecoveryFinalizationException>(() => fixture.Service.CommitWithRecoveryAsync(plan, store));
            Assert(error.Outcome.VerifiedState == PatchCommitState.Committed && error.Record.IsInvalidated && fixture.Get("sample.md") == "new\n" &&
                error.InnerException is RecoveryManifestReplacementException { ReplacementAttempted: false } replacement && !replacement.CleanupErrors.IsEmpty && File.Exists(foreign), "Runtime preserves foreign file, committed workspace and original backup");
            File.Delete(foreign!); // Remove only this test's known foreign entry so the retained record can be inspected.
            Assert((await store.LoadAsync(error.Record.Manifest.ChangeId)).Manifest.State == RecoveryRecordState.Applying, "Uncertain handle never auto-promotes or rewrites the old disk revision");
        });

        await check("a move destination occupied immediately before install is preserved with truthful incomplete state", async () =>
        {
            Fixture? fixtureRef = null;
            using var fixture = new Fixture((stage, index) =>
            {
                if (stage == PatchCommitStage.BeforeInstall && index == 1) fixtureRef!.Put("nested/moved.md", "external destination\n");
                return Task.CompletedTask;
            });
            fixtureRef = fixture;
            var plan = await fixture.Plan("*** Add File: nested/new.md\n+new\n*** Update File: sample.md\n*** Move to: nested/moved.md");
            var store = fixture.Store();
            var result = await fixture.Service.CommitWithRecoveryAsync(plan, store);
            Assert(result.Outcome.VerifiedState == PatchCommitState.Incomplete && fixture.Get("sample.md") == "old\n" &&
                fixture.Get("nested/moved.md") == "external destination\n" && !File.Exists(fixture.Path("nested/new.md")), "External destination is neither moved over nor removed by rollback");
            await VerifyAsync(store, result);
        });

        await check("source links appearing during recorded preparation are rejected without following or overwriting their target", async () =>
        {
            using var fixture = new Fixture();
            var outside = Path.Combine(fixture.DirectoryPath, "outside.md");
            File.WriteAllText(outside, "outside\n");
            var store = fixture.Store(prepare: (stage, _) =>
            {
                if (stage == RecoveryPreparationStage.BeforeReturn)
                {
                    File.Delete(fixture.Path("sample.md"));
                    File.CreateSymbolicLink(fixture.Path("sample.md"), outside);
                }
                return Task.CompletedTask;
            });
            var plan = await fixture.Plan("*** Add File: nested/new.md\n+new\n*** Update File: sample.md\n@@\n-old\n+new");
            try
            {
                await Reject<WorkspaceException>(() => fixture.Service.CommitWithRecoveryAsync(plan, store));
                Assert(File.ReadAllText(outside) == "outside\n" && !Directory.Exists(fixture.Path("nested")) &&
                    (await store.LoadAsync(fixture.ReadManifest().ChangeId)).Manifest.State == RecoveryRecordState.Prepared, "No engine staging, link traversal or automatic record promotion");
            }
            finally { File.Delete(fixture.Path("sample.md")); }
        });
    }

    private static void SeedMixed(Fixture fixture)
    {
        fixture.Put("sample.md", "\ufeffold\r\nlocal\nlast");
        fixture.Put("delete.md", "");
        fixture.Put("move.md", "move\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fixture.Path("sample.md"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            File.SetUnixFileMode(fixture.Path("move.md"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    internal static async Task VerifyAsync(RecoveryStore store, RecordedPatchCommit result)
    {
        var saved = await store.LoadAsync(result.Record.Manifest.ChangeId);
        Assert(RecoveryManifestJson.Serialize(saved.Manifest).SequenceEqual(RecoveryManifestJson.Serialize(result.Record.Manifest)), "Live and durable terminal manifests match exactly");
        var expected = result.Outcome.VerifiedState switch { PatchCommitState.Committed => RecoveryRecordState.Committed, PatchCommitState.RolledBack => RecoveryRecordState.RolledBack, _ => RecoveryRecordState.Incomplete };
        Assert(saved.Manifest.State == expected, "Persisted terminal state comes from the verified engine result");
        foreach (var path in saved.Manifest.Paths)
        {
            if (path.Before is { } before)
            {
                var backup = saved.Backups.Single(backup => backup.FileName == path.BackupFile);
                var planned = result.Record.Plan.Changes.Single(change => change.Summary.Path == path.Path).Before!;
                Assert(backup.Bytes.AsSpan().SequenceEqual(planned.Bytes.AsSpan()) && before.UnixMode == planned.Mode &&
                    before.ByteLength == backup.Bytes.Length && before.Sha256 == Convert.ToHexString(SHA256.HashData(backup.Bytes.AsSpan())), "Persisted originals retain raw bytes and permissions from before the attempt");
            }
            if (expected == RecoveryRecordState.Incomplete) continue; // No complete post-state claim for partial rollback/verification.
            var image = expected == RecoveryRecordState.Committed ? path.After : path.Before;
            var full = Path.Combine(saved.Manifest.ProjectRoot, path.Path);
            if (image is null) Assert(!File.Exists(full) && !Directory.Exists(full), "Absent path matches the terminal state");
            else
            {
                var bytes = File.ReadAllBytes(full);
                Assert(bytes.Length == image.ByteLength && Convert.ToHexString(SHA256.HashData(bytes)) == image.Sha256 &&
                    (OperatingSystem.IsWindows() || File.GetUnixFileMode(full) == image.UnixMode), "Actual complete terminal files match recorded bytes, hashes and modes");
            }
        }
        Assert(result.Record.Plan.Owner.ProjectRoot == saved.Manifest.ProjectRoot && !result.Record.IsInvalidated, "Valid terminal record belongs to this workspace");
    }

    internal static string[] JournalSnapshot(Fixture fixture) => Directory.EnumerateFileSystemEntries(fixture.Storage, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(fixture.Storage, path) + ":" + (Directory.Exists(path) ? "directory" : Path.GetFileName(path) == RecoveryStorageLimits.LockFileName ? "lock-marker" : Convert.ToBase64String(File.ReadAllBytes(path)))).ToArray();
    private static void AddSlots(Fixture fixture, int count)
    { for (var index = 0; index < count; index++) Directory.CreateDirectory(Path.Combine(fixture.Storage, $"foreign-{index}")); }
    internal static void Assert(bool condition, string message) { if (!condition) throw new Exception("Recorded fault matrix: " + message); }
    internal static async Task<T> Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new Exception($"Expected {typeof(T).Name}."); }
    private static async Task RejectInvalid(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { return; }
        throw new Exception("Expected corrupt/replaced recovery metadata rejection.");
    }
}
