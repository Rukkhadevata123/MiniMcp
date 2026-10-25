using MiniMcp.Configuration;
using MiniMcp.Recovery;

internal static class RecoveryStorageGuardChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("recovery storage preflight treats missing directories as empty without creating any data", () =>
        {
            using var fixture = new Fixture();
            var before = fixture.Snapshot();
            var guard = new RecoveryStorageGuard(fixture.Configuration());
            Assert(guard.Inspect(512) == new RecoveryStorageUsage(0, 0), "Missing storage usage");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Reject<OperationCanceledException>(() => guard.Inspect(512, cancelled.Token));
            Assert(before.SequenceEqual(fixture.Snapshot()) && !Directory.Exists(fixture.Storage), "No directory or lock marker created");
            return Task.CompletedTask;
        });

        await check("recovery storage counts all file bytes and unknown or partial top-level entries without deleting them", () =>
        {
            using var fixture = new Fixture();
            var guard = new RecoveryStorageGuard(fixture.Configuration());
            fixture.Put("record/manifest.json", "meta");
            fixture.Put("record/before-0000.bin", "\ufeff中\r\n");
            fixture.Put("record/nested/unknown", "u");
            fixture.Put(".preparing-record/partial.bin", "partial");
            fixture.Put("stray.dat", "abc");
            fixture.Put(RecoveryStorageLimits.LockFileName, "");
            var before = fixture.Snapshot();
            Assert(guard.Inspect(512) == new RecoveryStorageUsage(23, 3), "Raw bytes, partial records and unknown data all count");
            Assert(before.SequenceEqual(fixture.Snapshot()), "Existing data is never cleaned automatically");
            return Task.CompletedTask;
        });

        await check("recovery capacity accepts exact byte and slot boundaries and rejects invalid or over-limit requests", () =>
        {
            // Numeric boundaries are checked without allocating a GiB of test data.
            var exact = new RecoveryStorageUsage(RecoveryStorageLimits.MaxStoreBytes - 512, RecoveryStorageLimits.MaxRecordSlots - 1);
            exact.EnsureCanAdd(512);
            Reject<IOException>(() => exact.EnsureCanAdd(513));
            Reject<IOException>(() => new RecoveryStorageUsage(0, RecoveryStorageLimits.MaxRecordSlots).EnsureCanAdd(1));
            Reject<IOException>(() => new RecoveryStorageUsage(RecoveryStorageLimits.MaxStoreBytes + 1, 0).EnsureCanAdd(1));
            new RecoveryStorageUsage(0, 0).EnsureCanAdd(RecoveryStorageLimits.MaxRecordBytes);
            foreach (var bytes in new[] { -1L, 0, RecoveryStorageLimits.MaxRecordBytes + 1, long.MaxValue })
                Reject<ArgumentOutOfRangeException>(() => new RecoveryStorageUsage(0, 0).EnsureCanAdd(bytes));
            Reject<InvalidDataException>(() => new RecoveryStorageUsage(-1, 0).EnsureCanAdd(1));
            Reject<InvalidDataException>(() => new RecoveryStorageUsage(0, -1).EnsureCanAdd(1));
            using var fixture = new Fixture();
            var guard = new RecoveryStorageGuard(fixture.Configuration());
            for (var i = 0; i < RecoveryStorageLimits.MaxRecordSlots - 1; i++) Directory.CreateDirectory(Path.Combine(fixture.Storage, $"unknown-{i}"));
            Assert(guard.Inspect(512).RecordSlots == 999, "Physical exact slot boundary");
            Directory.CreateDirectory(Path.Combine(fixture.Storage, "one-more"));
            Reject<IOException>(() => guard.Inspect(512));
            Assert(Directory.EnumerateDirectories(fixture.Storage).Count() == 1000, "Quota rejection preserves all entries");
            return Task.CompletedTask;
        });

        await check("recovery storage rechecks directory types and ancestors after configuration validation", () =>
        {
            using var fixture = new Fixture();
            var guard = new RecoveryStorageGuard(fixture.Configuration());
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.Storage)!);
            File.WriteAllText(fixture.Storage, "blocker");
            Reject<InvalidDataException>(() => guard.Inspect(512));
            Assert(File.ReadAllText(fixture.Storage) == "blocker", "Storage-file collision unchanged");
            File.Delete(fixture.Storage);
            var parent = Path.Combine(fixture.DirectoryPath, "blocked-parent");
            var nested = new RecoveryStorageGuard(fixture.Configuration(Path.Combine(parent, "recovery")));
            File.WriteAllText(parent, "ancestor");
            Reject<InvalidDataException>(() => nested.Inspect(512));
            Assert(File.ReadAllText(parent) == "ancestor", "File ancestor unchanged");
            Directory.Delete(fixture.Root, recursive: true);
            File.WriteAllText(fixture.Root, "workspace blocker");
            Reject<InvalidDataException>(() => guard.Inspect(512));
            Assert(File.ReadAllText(fixture.Root) == "workspace blocker", "Workspace-file collision unchanged");
            return Task.CompletedTask;
        });

        await check("recovery storage scope rejects overlapping or noncanonical snapshots and accepts prefix siblings", () =>
        {
            using var fixture = new Fixture();
            var configuration = fixture.Configuration();
            var before = fixture.Snapshot();
            foreach (var storage in new[] { fixture.Root, Path.Combine(fixture.Root, "recovery"), fixture.DirectoryPath, "relative", fixture.Storage + Path.DirectorySeparatorChar })
            {
                // Defensive check of malformed internal inputs; production callers receive only fully validated snapshots.
                var invalid = new RuntimeConfiguration(configuration.Settings, configuration.Policy with { RecoveryDirectory = storage }, configuration.SettingsFilePath);
                Reject<ArgumentException>(() => new RecoveryStorageGuard(invalid));
            }
            var sibling = fixture.Root + "-history";
            Assert(new RecoveryStorageGuard(fixture.Configuration(sibling)).Inspect(512) == new RecoveryStorageUsage(0, 0), "Containment uses path boundaries");
            Assert(before.SequenceEqual(fixture.Snapshot()) && !Directory.Exists(sibling), "Scope checks never create storage");
            return Task.CompletedTask;
        });

        await check("recovery lock markers must have canonical file layout and are inspectable while held exclusively", () =>
        {
            using var fixture = new Fixture();
            var guard = new RecoveryStorageGuard(fixture.Configuration());
            fixture.Put(RecoveryStorageLimits.LockFileName, "");
            var marker = Path.Combine(fixture.Storage, RecoveryStorageLimits.LockFileName);
            using (var locked = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert(guard.Inspect(512) == new RecoveryStorageUsage(0, 0), "A zero-length lock does not occupy a record slot");
            File.WriteAllText(marker, "unexpected content");
            Reject<InvalidDataException>(() => guard.Inspect(512));
            Assert(File.ReadAllText(marker) == "unexpected content", "Invalid marker content preserved");
            File.Delete(marker);
            Directory.CreateDirectory(marker);
            Reject<InvalidDataException>(() => guard.Inspect(512));
            Assert(Directory.Exists(marker), "Marker directory preserved");
            return Task.CompletedTask;
        });

        await check("recovery inspection bounds its work and directory depth and observes cancellation", () =>
        {
            var budget = new RecoveryStorageScanBudget();
            for (var i = 0; i < RecoveryStorageLimits.MaxScanEntries; i++) budget.Visit(CancellationToken.None);
            Reject<IOException>(() => budget.Visit(CancellationToken.None));
            RecoveryStorageScanBudget.DirectoryDepth(RecoveryStorageLimits.MaxDirectoryDepth);
            Reject<IOException>(() => RecoveryStorageScanBudget.DirectoryDepth(RecoveryStorageLimits.MaxDirectoryDepth + 1));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Reject<OperationCanceledException>(() => new RecoveryStorageScanBudget().Visit(cancelled.Token));
            using var fixture = new Fixture();
            var guard = new RecoveryStorageGuard(fixture.Configuration());
            var current = fixture.Storage;
            for (var i = 0; i < RecoveryStorageLimits.MaxDirectoryDepth; i++) current = Directory.CreateDirectory(Path.Combine(current, "d")).FullName;
            Assert(guard.Inspect(512) == new RecoveryStorageUsage(0, 1), "Exact directory depth");
            var over = Directory.CreateDirectory(Path.Combine(current, "d")).FullName;
            Reject<IOException>(() => guard.Inspect(512));
            Assert(Directory.Exists(over), "Work-limit rejection does not clean old trees");
            return Task.CompletedTask;
        });

        await check("recovery inspection rejects directory, ancestor, file and dangling links without following their contents", () =>
        {
            using var fixture = new Fixture();
            var guard = new RecoveryStorageGuard(fixture.Configuration());
            var outside = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "outside")).FullName;
            File.WriteAllText(Path.Combine(outside, "secret"), "unchanged");
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.Storage)!);
            try { Directory.CreateSymbolicLink(fixture.Storage, outside); }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
            {
                Console.WriteLine("SKIP recovery link checks: Windows symbolic link creation requires Developer Mode or permission.");
                return Task.CompletedTask;
            }
            try { Reject<InvalidDataException>(() => guard.Inspect(512)); }
            finally { Directory.Delete(fixture.Storage); }
            var parent = Path.Combine(fixture.DirectoryPath, "linked-parent");
            var throughParent = new RecoveryStorageGuard(fixture.Configuration(Path.Combine(parent, "missing-recovery")));
            Directory.CreateSymbolicLink(parent, outside);
            try { Reject<InvalidDataException>(() => throughParent.Inspect(512)); }
            finally { Directory.Delete(parent); }
            Directory.CreateDirectory(fixture.Storage);
            var directoryLink = Path.Combine(fixture.Storage, "workspace-link");
            Directory.CreateSymbolicLink(directoryLink, fixture.Root);
            try { Reject<InvalidDataException>(() => guard.Inspect(512)); }
            finally { Directory.Delete(directoryLink); }
            foreach (var target in new[] { Path.Combine(outside, "secret"), Path.Combine(outside, "missing") })
            {
                var link = Path.Combine(fixture.Storage, "file-link");
                File.CreateSymbolicLink(link, target);
                try { Reject<InvalidDataException>(() => guard.Inspect(512)); }
                finally { File.Delete(link); }
            }
            Assert(File.ReadAllText(Path.Combine(outside, "secret")) == "unchanged" && Directory.EnumerateFileSystemEntries(outside).Count() == 1, "Link targets unchanged");
            return Task.CompletedTask;
        });
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception("Recovery storage guard: " + message); }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name} during recovery preflight.");
    }

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"minimcp-recovery-guard-{Guid.NewGuid():N}");
        public string Root { get; }
        public string Storage => Path.Combine(DirectoryPath, "archive", "recovery");
        private readonly ConfigurationPaths _paths;
        public Fixture()
        {
            Root = Directory.CreateDirectory(Path.Combine(DirectoryPath, "project")).FullName;
            File.WriteAllText(Path.Combine(Root, "sample.md"), "original");
            _paths = new ConfigurationPaths(Path.Combine(DirectoryPath, "user"), Path.Combine(DirectoryPath, "state"));
        }
        public RuntimeConfiguration Configuration(string? storage = null) => new ConfigurationValidator(_paths).Validate(new AppSettings
        { ProjectRoot = Root, Authentication = new AuthenticationSettings { Mode = AuthenticationMode.Local } },
            Path.Combine(DirectoryPath, "settings.json"), new ExecutionPolicy { RecoveryDirectory = storage ?? Storage });
        public void Put(string relative, string text)
        {
            var path = Path.Combine(Storage, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
        }
        public string[] Snapshot() => Directory.EnumerateFileSystemEntries(DirectoryPath, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(DirectoryPath, path) + (Directory.Exists(path) ? "/" : ":" + Convert.ToBase64String(File.ReadAllBytes(path)))).ToArray();
        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
