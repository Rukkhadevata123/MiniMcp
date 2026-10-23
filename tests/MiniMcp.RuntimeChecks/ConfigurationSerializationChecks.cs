using System.Text.Json;
using MiniMcp.Configuration;

internal static class ConfigurationSerializationChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        void Assert(bool condition)
        {
            if (!condition) throw new Exception("Configuration serialization assertion failed.");
        }
        void Reject<TException>(Action action) where TException : Exception
        {
            try { action(); }
            catch (TException) { return; }
            throw new Exception($"Expected {typeof(TException).Name} during configuration serialization.");
        }

        await check("configuration examples survive serialization and strict parsing without changing values", async () =>
        {
            var examples = Path.Combine(AppContext.BaseDirectory, "examples");
            var settings = ConfigurationJson.ParseSettings(await File.ReadAllTextAsync(Path.Combine(examples, "settings.local.json")));
            var policy = ConfigurationJson.ParsePolicy(await File.ReadAllTextAsync(Path.Combine(examples, "policy.readonly.json")));
            var settingsJson = ConfigurationJson.SerializeSettings(settings);
            var policyJson = ConfigurationJson.SerializePolicy(policy);
            Assert(ConfigurationJson.ParseSettings(settingsJson) == settings && ConfigurationJson.ParsePolicy(policyJson) == policy);
            Assert(settingsJson.EndsWith('\n') && policyJson.EndsWith('\n') && !settingsJson.Contains('\r'));
            using var settingsDocument = JsonDocument.Parse(settingsJson);
            using var policyDocument = JsonDocument.Parse(policyJson);
            Assert(settingsDocument.RootElement.GetProperty("authentication").GetProperty("mode").GetString() == "Local");
            Assert(!settingsDocument.RootElement.TryGetProperty("mode", out _) && !settingsDocument.RootElement.TryGetProperty("recoveryDirectory", out _));
            Assert(policyDocument.RootElement.GetProperty("mode").GetString() == "ReadOnly");
            Assert(!policyDocument.RootElement.TryGetProperty("projectRoot", out _));
        });

        await check("serialization preserves Unicode, escaped strings, relative paths and automatic discovery values", () =>
        {
            var settings = new AppSettings
            {
                ProjectRoot = "../项目 with spaces/👋/\"quoted\"",
                BashPath = @"C:\Program Files\Git\bin\bash.exe",
                Authentication = new AuthenticationSettings { Mode = AuthenticationMode.Local },
                Tunnel = new TunnelSettings { TokenFilePath = "./token\tfile" }
            };
            var policy = new ExecutionPolicy { RecoveryDirectory = "../恢复记录" };
            var json = ConfigurationJson.SerializeSettings(settings);
            var restored = ConfigurationJson.ParseSettings(json);
            Assert(restored == settings && json.Contains("项目", StringComparison.Ordinal));
            Assert(restored.Tunnel.ExecutablePath == "" && restored.Tunnel.TokenFilePath == "./token\tfile");
            Assert(ConfigurationJson.ParsePolicy(ConfigurationJson.SerializePolicy(policy)) == policy);
            Assert(settings.ProjectRoot.StartsWith("../", StringComparison.Ordinal));
            Assert(ConfigurationJson.SerializeSettings(restored) == json);
            return Task.CompletedTask;
        });

        await check("serialization writes exact named modes and rejects undefined enum values", () =>
        {
            foreach (var mode in Enum.GetValues<ExecutionMode>())
            {
                var policy = new ExecutionPolicy { Mode = mode };
                using var document = JsonDocument.Parse(ConfigurationJson.SerializePolicy(policy));
                Assert(document.RootElement.GetProperty("mode").GetString() == mode.ToString());
                Assert(ConfigurationJson.ParsePolicy(ConfigurationJson.SerializePolicy(policy)) == policy);
            }
            foreach (var mode in Enum.GetValues<AuthenticationMode>())
            {
                var settings = new AppSettings { Authentication = new AuthenticationSettings { Mode = mode } };
                Assert(ConfigurationJson.ParseSettings(ConfigurationJson.SerializeSettings(settings)) == settings);
            }
            Reject<JsonException>(() => ConfigurationJson.SerializePolicy(new ExecutionPolicy { Mode = (ExecutionMode)999 }));
            Reject<JsonException>(() => ConfigurationJson.SerializeSettings(new AppSettings
            { Authentication = new AuthenticationSettings { Mode = (AuthenticationMode)999 } }));
            return Task.CompletedTask;
        });

        await check("serialization rejects unsupported versions and null nonnullable fields", () =>
        {
            Reject<ArgumentNullException>(() => ConfigurationJson.SerializeSettings(null!));
            Reject<ArgumentNullException>(() => ConfigurationJson.SerializePolicy(null!));
            Reject<JsonException>(() => ConfigurationJson.SerializeSettings(new AppSettings { SchemaVersion = 2 }));
            Reject<JsonException>(() => ConfigurationJson.SerializePolicy(new ExecutionPolicy { SchemaVersion = 2 }));
            foreach (var settings in new[]
            {
                new AppSettings { ProjectRoot = null! }, new AppSettings { BashPath = null! },
                new AppSettings { Authentication = null! }, new AppSettings { Tunnel = null! },
                new AppSettings { Tunnel = new TunnelSettings { ExecutablePath = null! } },
                new AppSettings { Tunnel = new TunnelSettings { TokenFilePath = null! } }
            }) Reject<JsonException>(() => ConfigurationJson.SerializeSettings(settings));
            Reject<JsonException>(() => ConfigurationJson.SerializePolicy(new ExecutionPolicy { RecoveryDirectory = null! }));
            return Task.CompletedTask;
        });
    }
}
