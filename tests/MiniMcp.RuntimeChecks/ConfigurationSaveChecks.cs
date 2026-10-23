using System.Security.Cryptography;
using System.Text;
using MiniMcp.Configuration;

internal static class ConfigurationSaveChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"minimcp-save-{Guid.NewGuid():N}");
        var root = Path.Combine(temporary, "project");
        var source = Path.Combine(temporary, "settings.json");
        var paths = new ConfigurationPaths(Path.Combine(temporary, "local config"), Path.Combine(temporary, "state"));
        var store = new ConfigurationStore(paths);
        var draft = new AppSettings { ProjectRoot = "./project", Authentication = new AuthenticationSettings { Mode = AuthenticationMode.Local } };
        Directory.CreateDirectory(root);
        void Assert(bool condition)
        {
            if (!condition) throw new Exception("Configuration save assertion failed.");
        }
        async Task<TException> Reject<TException>(Func<Task> action) where TException : Exception
        {
            try { await action(); }
            catch (TException exception) { return exception; }
            throw new Exception($"Expected {typeof(TException).Name} while saving configuration.");
        }
        async Task<ConfigurationFile<AppSettings>> Seed()
        {
            await File.WriteAllTextAsync(source, ConfigurationJson.SerializeSettings(draft).Replace("\n", "\r\n"), new UTF8Encoding(true));
            return await store.ReadSettingsAsync(source, temporary);
        }
        void NoTemporaryFiles() => Assert(!Directory.EnumerateFiles(temporary, ".minimcp-config-*.tmp", SearchOption.AllDirectories).Any());
        try
        {
            await check("settings saves create UTF-8 files and preserve draft paths with accurate revisions", async () =>
            {
                var path = Path.Combine(temporary, "new", "nested", "settings.json");
                var value = draft with { ProjectRoot = "../../project" };
                var saved = await store.SaveSettingsAsync(new ConfigurationFile<AppSettings>(path, value, null));
                var bytes = await File.ReadAllBytesAsync(path);
                Assert(saved.Value.ProjectRoot == "../../project" && !bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
                Assert(saved.Revision == Convert.ToHexString(SHA256.HashData(bytes)));
                Assert((await store.ReadSettingsAsync(path, temporary)) == saved);
                Assert((await store.LoadAsync(path, temporary)).Settings.ProjectRoot == root);
                Assert(!File.Exists(paths.PolicyFile) && !Directory.Exists(paths.RecoveryDirectory));
                if (OperatingSystem.IsLinux())
                {
                    Assert(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite));
                    Assert(File.GetUnixFileMode(Path.GetDirectoryName(path)!) ==
                        (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
                }
                NoTemporaryFiles();
            });

            await check("settings updates replace a complete file and return the revision needed for another save", async () =>
            {
                var original = await Seed();
                if (OperatingSystem.IsLinux())
                    File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
                var changed = original with { Value = original.Value with { Port = 8123 } };
                var saved = await store.SaveSettingsAsync(changed);
                Assert(saved.Value == changed.Value && saved.Revision != original.Revision);
                Assert((await store.ReadSettingsAsync(source, temporary)) == saved);
                if (OperatingSystem.IsLinux())
                    Assert(File.GetUnixFileMode(source) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead));
                var again = await store.SaveSettingsAsync(saved with { Value = saved.Value with { Port = 8124 } });
                Assert((await store.ReadSettingsAsync(source, temporary)) == again);
                NoTemporaryFiles();
            });

            await check("settings saves reject changed, deleted and unexpectedly created targets", async () =>
            {
                var original = await Seed();
                var external = Encoding.UTF8.GetBytes(ConfigurationJson.SerializeSettings(draft));
                await File.WriteAllBytesAsync(source, external); // The same values with different bytes still conflict.
                await Reject<ConfigurationConflictException>(() => store.SaveSettingsAsync(original));
                Assert((await File.ReadAllBytesAsync(source)).SequenceEqual(external));
                File.Delete(source);
                await Reject<ConfigurationConflictException>(() => store.SaveSettingsAsync(original));
                Assert(!File.Exists(source));
                await File.WriteAllBytesAsync(source, external);
                await Reject<ConfigurationConflictException>(() => store.SaveSettingsAsync(new ConfigurationFile<AppSettings>(source, draft, null)));
                Assert((await File.ReadAllBytesAsync(source)).SequenceEqual(external));
                NoTemporaryFiles();
            });

            await check("settings and policy revisions are rechecked after staging", async () =>
            {
                var original = await Seed();
                var external = ConfigurationJson.SerializeSettings(draft with { Port = 9000 });
                var changedDuringSave = new ConfigurationStore(paths, () => File.WriteAllTextAsync(source, external));
                await Reject<ConfigurationConflictException>(() => changedDuringSave.SaveSettingsAsync(original));
                Assert(await File.ReadAllTextAsync(source) == external);
                original = await Seed();
                var oldBytes = await File.ReadAllBytesAsync(source);
                Directory.CreateDirectory(Path.GetDirectoryName(paths.PolicyFile)!);
                await File.WriteAllTextAsync(paths.PolicyFile, "{}");
                var policyChanged = new ConfigurationStore(paths, () => File.WriteAllTextAsync(paths.PolicyFile, """{"mode":"ReadOnly"}"""));
                var conflict = await Reject<ConfigurationConflictException>(() => policyChanged.SaveSettingsAsync(original));
                Assert(conflict.FilePath == paths.PolicyFile && (await File.ReadAllBytesAsync(source)).SequenceEqual(oldBytes));
                NoTemporaryFiles();
            });

            await check("cancelled settings saves keep the old file and remove staged files and unused directories", async () =>
            {
                var original = await Seed();
                var oldBytes = await File.ReadAllBytesAsync(source);
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                await Reject<OperationCanceledException>(() => store.SaveSettingsAsync(original, cancelled.Token));
                using var stagedCancellation = new CancellationTokenSource();
                var cancelStore = new ConfigurationStore(paths, () => { stagedCancellation.Cancel(); return Task.CompletedTask; });
                await Reject<OperationCanceledException>(() => cancelStore.SaveSettingsAsync(original, stagedCancellation.Token));
                Assert((await File.ReadAllBytesAsync(source)).SequenceEqual(oldBytes));
                using var creationCancellation = new CancellationTokenSource();
                var createStore = new ConfigurationStore(paths, () => { creationCancellation.Cancel(); return Task.CompletedTask; });
                var path = Path.Combine(temporary, "cancelled", "nested", "settings.json");
                await Reject<OperationCanceledException>(() => createStore.SaveSettingsAsync(
                    new ConfigurationFile<AppSettings>(path, draft with { ProjectRoot = root }, null), creationCancellation.Token));
                Assert(!Directory.Exists(Path.Combine(temporary, "cancelled")));
                NoTemporaryFiles();
            });

            await check("failed preparation and replacement preserve the old settings and clean temporary files", async () =>
            {
                var original = await Seed();
                var oldBytes = await File.ReadAllBytesAsync(source);
                var failureStore = new ConfigurationStore(paths, () => throw new IOException("Injected preparation failure."));
                await Reject<ConfigurationSaveException>(() => failureStore.SaveSettingsAsync(original));
                Assert((await File.ReadAllBytesAsync(source)).SequenceEqual(oldBytes));
                if (OperatingSystem.IsWindows())
                {
                    FileStream? locked = null;
                    var lockedStore = new ConfigurationStore(paths, () =>
                    {
                        locked = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                        return Task.CompletedTask;
                    });
                    try { await Reject<ConfigurationSaveException>(() => lockedStore.SaveSettingsAsync(original)); }
                    finally { locked?.Dispose(); }
                    Assert((await File.ReadAllBytesAsync(source)).SequenceEqual(oldBytes));
                }
                NoTemporaryFiles();
            });

            await check("invalid and oversized settings are rejected before creating storage or changing existing files", async () =>
            {
                var original = await Seed();
                var oldBytes = await File.ReadAllBytesAsync(source);
                await Reject<ConfigurationValidationException>(() => store.SaveSettingsAsync(original with { Value = draft with { Port = 0 } }));
                Assert((await File.ReadAllBytesAsync(source)).SequenceEqual(oldBytes));
                var huge = draft with { ProjectRoot = root, Authentication = draft.Authentication with { Audience = new string('汉', 25000) } };
                Assert(ConfigurationJson.SerializeSettings(huge).Length < ConfigurationStore.MaxFileBytes);
                var path = Path.Combine(temporary, "oversized", "nested", "settings.json");
                var size = await Reject<ConfigurationSaveException>(() => store.SaveSettingsAsync(new ConfigurationFile<AppSettings>(path, huge, null)));
                Assert(size.InnerException is InvalidDataException && !Directory.Exists(Path.Combine(temporary, "oversized")));
                foreach (var reserved in new[] { paths.PolicyFile, paths.RecoveryDirectory })
                {
                    var failure = await Reject<ConfigurationValidationException>(() => store.SaveSettingsAsync(
                        new ConfigurationFile<AppSettings>(reserved, draft with { ProjectRoot = root }, null)));
                    Assert(failure.Field == "settingsFile");
                }
                await File.WriteAllTextAsync(paths.PolicyFile, """{"mode":"Unknown"}""");
                try { await Reject<ConfigurationLoadException>(() => store.SaveSettingsAsync(original)); }
                finally { await File.WriteAllTextAsync(paths.PolicyFile, "{}"); }
                Assert((await File.ReadAllBytesAsync(source)).SequenceEqual(oldBytes));
                NoTemporaryFiles();
            });

            await check("concurrent saves across store instances allow one writer per original revision", async () =>
            {
                var original = await Seed();
                async Task<bool> Attempt(int port)
                {
                    try
                    {
                        await new ConfigurationStore(paths).SaveSettingsAsync(original with { Value = draft with { Port = port } });
                        return true;
                    }
                    catch (ConfigurationConflictException) { return false; }
                }
                var results = await Task.WhenAll(Attempt(8123), Attempt(8124));
                Assert(results.Count(success => success) == 1);
                Assert((await store.ReadSettingsAsync(source, temporary)).Value.Port is 8123 or 8124);
                NoTemporaryFiles();
            });
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }
}
