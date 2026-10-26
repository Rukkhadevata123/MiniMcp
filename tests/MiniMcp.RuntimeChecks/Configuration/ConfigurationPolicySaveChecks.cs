using System.Security.Cryptography;
using System.Text;
using MiniMcp.Configuration;

internal static class ConfigurationPolicySaveChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"minimcp-policy-save-{Guid.NewGuid():N}");
        var root = Path.Combine(temporary, "project");
        var source = Path.Combine(temporary, "settings.json");
        var paths = new ConfigurationPaths(Path.Combine(temporary, "user", "config"), Path.Combine(temporary, "state"));
        var store = new ConfigurationStore(paths);
        var draft = new AppSettings
        {
            ProjectRoot = "./project", BashPath = "./tools/bash",
            Authentication = new AuthenticationSettings { Mode = AuthenticationMode.Local }
        };
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(temporary, "tools"));
        var bash = Path.Combine(temporary, "tools", "bash");
        File.WriteAllText(bash, "Dependency fixture; never execute this file.");
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(bash, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        void Assert(bool condition)
        {
            if (!condition) throw new Exception("Policy save assertion failed.");
        }
        async Task<TException> Reject<TException>(Func<Task> action) where TException : Exception
        {
            try { await action(); }
            catch (TException exception) { return exception; }
            throw new Exception($"Expected {typeof(TException).Name} while saving local policy.");
        }
        async Task<ConfigurationFile<AppSettings>> Settings()
        {
            await File.WriteAllTextAsync(source, ConfigurationJson.SerializeSettings(draft));
            return await store.ReadSettingsAsync(source, temporary);
        }
        void NoTemporaryFiles() => Assert(!Directory.EnumerateFiles(temporary, ".minimcp-config-*.tmp", SearchOption.AllDirectories).Any());
        try
        {
            await check("policy saves create only the fixed local file and preserve relative recovery paths", async () =>
            {
                var settings = await Settings();
                var original = await store.ReadPolicyAsync();
                Assert(original.Revision is null);
                var value = original.Value with { RecoveryDirectory = "../history" };
                var saved = await store.SavePolicyAsync(original with { Value = value }, settings);
                var bytes = await File.ReadAllBytesAsync(paths.PolicyFile);
                Assert(saved.FilePath == paths.PolicyFile && saved.Value == value && (await store.ReadPolicyAsync()) == saved);
                Assert(saved.Revision == Convert.ToHexString(SHA256.HashData(bytes)) && !bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
                Assert((await store.LoadAsync(source, temporary)).Policy.RecoveryDirectory == Path.Combine(temporary, "user", "history"));
                Assert(!Directory.Exists(Path.Combine(temporary, "user", "history")));
                if (OperatingSystem.IsLinux())
                    Assert(File.GetUnixFileMode(paths.PolicyFile) == (UnixFileMode.UserRead | UnixFileMode.UserWrite));
                NoTemporaryFiles();
            });

            await check("policy updates support each execution mode without changing saved settings", async () =>
            {
                var settings = await Settings();
                var oldSettings = await File.ReadAllBytesAsync(source);
                var original = await store.ReadPolicyAsync();
                foreach (var mode in new[] { ExecutionMode.ProjectEdit, ExecutionMode.FullAccess, ExecutionMode.ReadOnly })
                {
                    original = await store.SavePolicyAsync(original with { Value = original.Value with { Mode = mode } }, settings);
                    Assert((await store.ReadPolicyAsync()) == original && original.Value.Mode == mode);
                }
                Assert((await File.ReadAllBytesAsync(source)).SequenceEqual(oldSettings));
                NoTemporaryFiles();
            });

            await check("policy saves reject alternate destinations and contexts without a saved revision", async () =>
            {
                var settings = await Settings();
                var original = await store.ReadPolicyAsync();
                var oldPolicy = await File.ReadAllBytesAsync(paths.PolicyFile);
                foreach (var path in new[] { "policy.json", source, Path.Combine(root, "policy.json") })
                {
                    var error = await Reject<ConfigurationValidationException>(() => store.SavePolicyAsync(original with { FilePath = path }, settings));
                    Assert(error.Field == "policyFile");
                }
                var context = await Reject<ConfigurationValidationException>(() => store.SavePolicyAsync(original, settings with { Revision = null }));
                Assert(context.Field == "settingsFile");
                Assert((await File.ReadAllBytesAsync(paths.PolicyFile)).SequenceEqual(oldPolicy) && !File.Exists(Path.Combine(root, "policy.json")));
                NoTemporaryFiles();
            });

            await check("policy saves reject changed, deleted or unexpectedly created policy files", async () =>
            {
                var settings = await Settings();
                var original = await store.ReadPolicyAsync();
                await File.WriteAllTextAsync(paths.PolicyFile, "{}");
                await Reject<ConfigurationConflictException>(() => store.SavePolicyAsync(original, settings));
                Assert(await File.ReadAllTextAsync(paths.PolicyFile) == "{}");
                original = await store.ReadPolicyAsync();
                File.Delete(paths.PolicyFile);
                await Reject<ConfigurationConflictException>(() => store.SavePolicyAsync(original, settings));
                Assert(!File.Exists(paths.PolicyFile));
                original = await store.ReadPolicyAsync();
                const string external = """{"mode":"FullAccess"}""";
                await File.WriteAllTextAsync(paths.PolicyFile, external);
                await Reject<ConfigurationConflictException>(() => store.SavePolicyAsync(original, settings));
                Assert(await File.ReadAllTextAsync(paths.PolicyFile) == external);
                await File.WriteAllTextAsync(paths.PolicyFile, "{}");
                NoTemporaryFiles();
            });

            await check("policy saves detect settings changes before and after staging", async () =>
            {
                var settings = await Settings();
                var original = await store.ReadPolicyAsync();
                var oldPolicy = await File.ReadAllBytesAsync(paths.PolicyFile);
                await File.WriteAllTextAsync(source, "invalid externally edited JSON");
                var stale = await Reject<ConfigurationConflictException>(() => store.SavePolicyAsync(original, settings));
                Assert(stale.FilePath == source);
                settings = await Settings();
                var changed = new ConfigurationStore(paths, () => File.WriteAllTextAsync(source, ConfigurationJson.SerializeSettings(draft with { Port = 9000 })));
                stale = await Reject<ConfigurationConflictException>(() => changed.SavePolicyAsync(original, settings));
                Assert(stale.FilePath == source && (await File.ReadAllBytesAsync(paths.PolicyFile)).SequenceEqual(oldPolicy));
                NoTemporaryFiles();
            });

            await check("policy validation uses persisted settings and rejects invalid modes or recovery boundaries", async () =>
            {
                var settings = await Settings();
                var original = await store.ReadPolicyAsync();
                var oldPolicy = await File.ReadAllBytesAsync(paths.PolicyFile);
                foreach (var value in new[]
                {
                    original.Value with { Mode = (ExecutionMode)999 },
                    original.Value with { SchemaVersion = 2 },
                    original.Value with { RecoveryDirectory = Path.Combine(root, "recovery") }
                }) await Reject<ConfigurationValidationException>(() => store.SavePolicyAsync(original with { Value = value }, settings));
                var otherRoot = Path.Combine(temporary, "other-project");
                Directory.CreateDirectory(otherRoot);
                var unsavedContext = settings with { Value = settings.Value with { ProjectRoot = otherRoot } };
                var boundary = await Reject<ConfigurationValidationException>(() => store.SavePolicyAsync(original with
                { Value = original.Value with { RecoveryDirectory = Path.Combine(root, "recovery") } }, unsavedContext));
                Assert(boundary.Field == "recoveryDirectory" && (await File.ReadAllBytesAsync(paths.PolicyFile)).SequenceEqual(oldPolicy));
                NoTemporaryFiles();
            });

            await check("cancelled or failed policy saves leave the original file and clean temporary storage", async () =>
            {
                var settings = await Settings();
                var original = await store.ReadPolicyAsync();
                var oldPolicy = await File.ReadAllBytesAsync(paths.PolicyFile);
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                await Reject<OperationCanceledException>(() => store.SavePolicyAsync(original, settings, cancelled.Token));
                var failed = new ConfigurationStore(paths, () => throw new IOException("Injected policy preparation failure."));
                await Reject<ConfigurationSaveException>(() => failed.SavePolicyAsync(original, settings));
                Assert((await File.ReadAllBytesAsync(paths.PolicyFile)).SequenceEqual(oldPolicy));
                using var stagedCancellation = new CancellationTokenSource();
                var newPaths = new ConfigurationPaths(Path.Combine(temporary, "cancelled", "config"), Path.Combine(temporary, "cancelled-state"));
                var newStore = new ConfigurationStore(newPaths, () => { stagedCancellation.Cancel(); return Task.CompletedTask; });
                var missing = await newStore.ReadPolicyAsync();
                await Reject<OperationCanceledException>(() => newStore.SavePolicyAsync(missing, settings, stagedCancellation.Token));
                Assert(!Directory.Exists(Path.Combine(temporary, "cancelled")));
                NoTemporaryFiles();
            });

            await check("concurrent policy saves across store instances allow one writer per revision", async () =>
            {
                var settings = await Settings();
                var original = await store.ReadPolicyAsync();
                async Task<bool> Attempt(string recovery)
                {
                    try
                    {
                        await new ConfigurationStore(paths).SavePolicyAsync(original with
                        { Value = original.Value with { RecoveryDirectory = recovery } }, settings);
                        return true;
                    }
                    catch (ConfigurationConflictException) { return false; }
                }
                var results = await Task.WhenAll(Attempt("../history-one"), Attempt("../history-two"));
                Assert(results.Count(success => success) == 1);
                Assert((await store.ReadPolicyAsync()).Value.RecoveryDirectory is "../history-one" or "../history-two");
                NoTemporaryFiles();
            });
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }
}
