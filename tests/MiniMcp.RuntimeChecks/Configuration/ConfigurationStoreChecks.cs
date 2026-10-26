using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MiniMcp.Configuration;

internal static class ConfigurationStoreChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"minimcp-configuration-{Guid.NewGuid():N}");
        var paths = new ConfigurationPaths(Path.Combine(temporary, "user config"), Path.Combine(temporary, "user state"));
        var loader = new ConfigurationStore(paths);
        var selected = Path.Combine(temporary, "project", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(selected)!);
        void Assert(bool condition)
        {
            if (!condition) throw new Exception("Configuration file loading assertion failed.");
        }
        async Task<TException> Reject<TException>(Func<Task> action) where TException : Exception
        {
            try { await action(); }
            catch (TException exception) { return exception; }
            throw new Exception($"Expected {typeof(TException).Name} while loading configuration.");
        }

        try
        {
            await check("configuration loader reads UTF-8 with BOM and fingerprints the exact original bytes", async () =>
            {
                const string json = """{"projectRoot":"./项目 with spaces","authentication":{"mode":"Local"}}""";
                await File.WriteAllTextAsync(selected, json, new UTF8Encoding(true));
                var bytes = await File.ReadAllBytesAsync(selected);
                var loaded = await loader.ReadSettingsAsync("project/settings.json", temporary);
                Assert(loaded.FilePath == selected && loaded.Value.ProjectRoot == "./项目 with spaces");
                Assert(loaded.Value.Authentication.Mode == AuthenticationMode.Local);
                Assert(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
                Assert(loaded.Revision == Convert.ToHexString(SHA256.HashData(bytes)));
                Assert((await File.ReadAllBytesAsync(selected)).SequenceEqual(bytes));
            });

            await check("missing local policy defaults to read-only without reading a neighboring project policy", async () =>
            {
                await File.WriteAllTextAsync(Path.Combine(temporary, "project", "policy.json"), """{"mode":"FullAccess"}""");
                var policy = await loader.ReadPolicyAsync();
                Assert(policy.FilePath == paths.PolicyFile && policy.Value.Mode == ExecutionMode.ReadOnly);
                Assert(policy.Revision is null && !Directory.Exists(Path.GetDirectoryName(paths.PolicyFile)));
                Assert(!Directory.Exists(Path.GetDirectoryName(paths.RecoveryDirectory)));
                await loader.ReadSettingsAsync(selected, temporary);
                Assert((await loader.ReadPolicyAsync()).Value.Mode == ExecutionMode.ReadOnly);
            });

            await check("configuration loader uses default settings and the fixed local policy file", async () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFile)!);
                await File.WriteAllTextAsync(paths.SettingsFile, """{"port":8123}""");
                await File.WriteAllTextAsync(paths.PolicyFile, """{"mode":"ProjectEdit"}""");
                Assert((await loader.ReadSettingsAsync(null, temporary)).Value.Port == 8123);
                var policy = await loader.ReadPolicyAsync();
                Assert(policy.Value.Mode == ExecutionMode.ProjectEdit && policy.Revision is not null);
            });

            await check("missing settings, invalid policy and policy read errors are reported instead of defaulted", async () =>
            {
                var missing = await Reject<ConfigurationLoadException>(() => loader.ReadSettingsAsync("missing.json", temporary));
                Assert(missing.InnerException is FileNotFoundException);
                foreach (var json in new[] { "", "{", """{"mode":"Unknown"}""", """{"schemaVersion":99}""" })
                {
                    await File.WriteAllTextAsync(paths.PolicyFile, json);
                    var invalid = await Reject<ConfigurationLoadException>(() => loader.ReadPolicyAsync());
                    Assert(invalid.FilePath == paths.PolicyFile && invalid.InnerException is JsonException);
                }
                if (OperatingSystem.IsWindows())
                {
                    using var locked = new FileStream(paths.PolicyFile, FileMode.Open, FileAccess.Read, FileShare.None);
                    var unreadable = await Reject<ConfigurationLoadException>(() => loader.ReadPolicyAsync());
                    Assert(unreadable.InnerException is IOException);
                }
                File.Delete(paths.PolicyFile);
                Directory.CreateDirectory(paths.PolicyFile);
                try { await Reject<ConfigurationLoadException>(() => loader.ReadPolicyAsync()); }
                finally { Directory.Delete(paths.PolicyFile); }
            });

            await check("configuration loader enforces a byte limit and strict UTF-8 encoding", async () =>
            {
                await File.WriteAllTextAsync(selected, "{}" + new string(' ', ConfigurationStore.MaxFileBytes - 2), new UTF8Encoding(false));
                Assert((await loader.ReadSettingsAsync(selected, temporary)).Revision is not null);
                await File.AppendAllTextAsync(selected, " ");
                var oversized = await Reject<ConfigurationLoadException>(() => loader.ReadSettingsAsync(selected, temporary));
                Assert(oversized.InnerException is InvalidDataException);
                await File.WriteAllBytesAsync(selected, [0xff, 0xfe, 0x7b, 0x00, 0x7d, 0x00]);
                var encoding = await Reject<ConfigurationLoadException>(() => loader.ReadSettingsAsync(selected, temporary));
                Assert(encoding.InnerException is DecoderFallbackException);
                await File.WriteAllBytesAsync(selected, [0x7b, 0xff, 0x7d]);
                await Reject<ConfigurationLoadException>(() => loader.ReadSettingsAsync(selected, temporary));
            });

            await check("file loading errors identify their source and retain JSON field and position diagnostics", async () =>
            {
                await File.WriteAllTextAsync(selected, "{\n  \"port\": \"8000\"\n}");
                var failure = await Reject<ConfigurationLoadException>(() => loader.ReadSettingsAsync(selected, temporary));
                Assert(failure.FilePath == selected && failure.Message.Contains(selected, StringComparison.Ordinal));
                Assert(failure.InnerException is JsonException { Path: "$.port", LineNumber: 1, BytePositionInLine: > 0 });
            });

            await check("configuration revisions change after external edits without changing earlier snapshots", async () =>
            {
                await File.WriteAllTextAsync(selected, """{"port":8000}""");
                var before = await loader.ReadSettingsAsync(selected, temporary);
                await File.WriteAllTextAsync(selected, """{"port":9000}""");
                var after = await loader.ReadSettingsAsync(selected, temporary);
                Assert(before.Value.Port == 8000 && after.Value.Port == 9000 && before.Revision != after.Revision);
            });

            await check("cancelled configuration loads propagate cancellation without applying defaults", async () =>
            {
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                await Reject<OperationCanceledException>(() => loader.ReadSettingsAsync(selected, temporary, cancelled.Token));
                await Reject<OperationCanceledException>(() => loader.ReadPolicyAsync(cancelled.Token));
                var absentPaths = new ConfigurationPaths(Path.Combine(temporary, "absent"), Path.Combine(temporary, "state"));
                var absentLoader = new ConfigurationStore(absentPaths);
                await Reject<OperationCanceledException>(() => absentLoader.ReadPolicyAsync(cancelled.Token));
                Assert(!Directory.Exists(Path.GetDirectoryName(absentPaths.PolicyFile)));
            });
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }
}
