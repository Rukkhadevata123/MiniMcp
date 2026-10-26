using System.Text.Json;
using MiniMcp.Configuration;

internal static class ConfigurationPatchLimitChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("ProjectEdit limits retain defaults when the object or individual fields are omitted", async () =>
        {
            var defaults = new ProjectEditLimits { MaxPaths = 10, MaxChangedLines = 500, MaxChangedBytes = 65536 };
            foreach (var json in new[] { "{}", """{"projectEditLimits":{}}""" })
            {
                var policy = ConfigurationJson.ParsePolicy(json);
                Assert(policy.ProjectEditLimits == defaults && policy.Mode == ExecutionMode.ReadOnly, "Defaults");
            }
            var partial = ConfigurationJson.ParsePolicy("""{"projectEditLimits":{"maxPaths":0}}""");
            Assert(partial.ProjectEditLimits == (defaults with { MaxPaths = 0 }), "Partial object");
            var example = ConfigurationJson.ParsePolicy(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "examples", "policy.readonly.json")));
            Assert(example.ProjectEditLimits == defaults, "Example defaults");
        });

        await check("ProjectEdit limits JSON rejects malformed nested limits and preserves field diagnostics", () =>
        {
            foreach (var value in new[] { "null", "[]", "true", "1", "\"limits\"" })
                Reject<JsonException>(() => ConfigurationJson.ParsePolicy("{\"projectEditLimits\":" + value + "}"));
            foreach (var field in new[] { "maxPaths", "maxChangedLines", "maxChangedBytes" })
            {
                foreach (var value in new[] { "null", "true", "\"10\"", "1.5", "2147483648", "-2147483649" })
                {
                    var error = Reject<JsonException>(() => ConfigurationJson.ParsePolicy("{\"projectEditLimits\":{\"" + field + "\":" + value + "}}"));
                    Assert(error.Path == "$.projectEditLimits." + field, "Numeric error path");
                }
            }
            foreach (var json in new[]
            {
                """{"projectEditLimits":{"unknown":1}}""", """{"projectEditLimits":{"MaxPaths":1}}""",
                """{"ProjectEditLimits":{}}""", """{"projectEditLimits":{"maxPaths":1,"maxPaths":2}}""",
                """{"projectEditLimits":{"maxPaths":1,"max\u0050aths":2}}""", """{"projectEditLimits":{},"projectEditLimits":{}}"""
            }) Reject<JsonException>(() => ConfigurationJson.ParsePolicy(json));
            Reject<JsonException>(() => ConfigurationJson.ParseSettings("""{"projectEditLimits":{"maxPaths":100}}"""));
            return Task.CompletedTask;
        });

        await check("ProjectEdit limits value checks accept inclusive integer bounds and reject invalid drafts in every mode", () =>
        {
            var settings = new AppSettings { Authentication = new AuthenticationSettings { Mode = AuthenticationMode.Local } };
            foreach (var mode in Enum.GetValues<ExecutionMode>())
            {
                foreach (var value in new[] { 0, 1, int.MaxValue })
                    ConfigurationValidator.ValidateValues(settings, new ExecutionPolicy
                    { Mode = mode, ProjectEditLimits = new() { MaxPaths = value, MaxChangedLines = value, MaxChangedBytes = value } });
                foreach (var (field, limits) in InvalidLimits())
                {
                    var error = Reject<ConfigurationValidationException>(() => ConfigurationValidator.ValidateValues(settings,
                        new ExecutionPolicy { Mode = mode, ProjectEditLimits = limits }));
                    Assert(error.Field == field, "Value error field");
                }
                var missing = Reject<ConfigurationValidationException>(() => ConfigurationValidator.ValidateValues(settings,
                    new ExecutionPolicy { Mode = mode, ProjectEditLimits = null! }));
                Assert(missing.Field == "projectEditLimits", "Null draft");
            }
            return Task.CompletedTask;
        });

        await check("ProjectEdit limits serialization preserves explicit values without leaking policy fields into settings", () =>
        {
            var policy = new ExecutionPolicy { ProjectEditLimits = new() { MaxPaths = 0, MaxChangedLines = int.MaxValue, MaxChangedBytes = 12345 } };
            var json = ConfigurationJson.SerializePolicy(policy);
            Assert(ConfigurationJson.ParsePolicy(json) == policy && !json.Contains('\r') && json.EndsWith('\n'), "Roundtrip");
            using var document = JsonDocument.Parse(json);
            var limits = document.RootElement.GetProperty("projectEditLimits");
            Assert(limits.EnumerateObject().Select(property => property.Name).Order()
                .SequenceEqual(new[] { "maxChangedBytes", "maxChangedLines", "maxPaths" }), "Exact fields");
            using var settings = JsonDocument.Parse(ConfigurationJson.SerializeSettings(new AppSettings()));
            Assert(!settings.RootElement.TryGetProperty("projectEditLimits", out _), "Separate settings");
            Reject<JsonException>(() => ConfigurationJson.SerializePolicy(policy with { ProjectEditLimits = null! }));
            return Task.CompletedTask;
        });

        await check("local policy saves preserve patch limits and earlier runtime snapshots across updates", async () =>
        {
            using var fixture = new Fixture();
            var settings = await fixture.Store.ReadSettingsAsync(fixture.Source, fixture.DirectoryPath);
            var settingsBytes = await File.ReadAllBytesAsync(fixture.Source);
            var original = await fixture.Store.ReadPolicyAsync();
            var limits = new ProjectEditLimits { MaxPaths = 3, MaxChangedLines = 20, MaxChangedBytes = 1024 };
            var first = await fixture.Store.SavePolicyAsync(original with { Value = original.Value with { ProjectEditLimits = limits } }, settings);
            Assert((await fixture.Store.ReadPolicyAsync()) == first, "First saved policy");
            var snapshot = await fixture.Store.LoadAsync(fixture.Source, fixture.DirectoryPath);
            Assert(snapshot.Policy.ProjectEditLimits == limits, "Validated snapshot");
            var nextLimits = limits with { MaxPaths = 0, MaxChangedLines = int.MaxValue, MaxChangedBytes = 0 };
            var next = await fixture.Store.SavePolicyAsync(first with { Value = first.Value with { ProjectEditLimits = nextLimits } }, settings);
            Assert((await fixture.Store.LoadAsync(fixture.Source, fixture.DirectoryPath)).Policy.ProjectEditLimits == nextLimits, "Updated limits");
            Assert(snapshot.Policy.ProjectEditLimits == limits && first.Value.ProjectEditLimits == limits, "Prior snapshots unchanged");
            Assert((await fixture.Store.ReadPolicyAsync()) == next && next.Revision != first.Revision, "Updated revision");
            Assert((await File.ReadAllBytesAsync(fixture.Source)).SequenceEqual(settingsBytes), "Settings unchanged");
            Assert(!Directory.Exists(fixture.Paths.RecoveryDirectory), "No recovery directory");
            // Match the returned long filename explicitly; Windows wildcard searches can include 8.3 aliases.
            var temporaryFiles = Directory.EnumerateFiles(fixture.DirectoryPath, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path).Equals(".tmp", StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert(temporaryFiles.Length == 0, "No staging remnants: " + string.Join(", ", temporaryFiles));
        });

        await check("invalid patch limit saves leave existing policy intact and create no missing policy storage", async () =>
        {
            using var fixture = new Fixture();
            var settings = await fixture.Store.ReadSettingsAsync(fixture.Source, fixture.DirectoryPath);
            foreach (var exists in new[] { false, true })
            {
                if (exists) fixture.WritePolicy(new ExecutionPolicy());
                var original = await fixture.Store.ReadPolicyAsync();
                var before = fixture.Snapshot();
                foreach (var (field, limits) in InvalidLimits())
                {
                    var error = await RejectAsync<ConfigurationValidationException>(() => fixture.Store.SavePolicyAsync(original with
                    { Value = original.Value with { ProjectEditLimits = limits } }, settings));
                    Assert(error.Field == field && before.SequenceEqual(fixture.Snapshot()), "Invalid save effects");
                }
                var missing = await RejectAsync<ConfigurationValidationException>(() => fixture.Store.SavePolicyAsync(original with
                { Value = original.Value with { ProjectEditLimits = null! } }, settings));
                Assert(missing.Field == "projectEditLimits" && before.SequenceEqual(fixture.Snapshot()), "Null save effects");
            }
        });

        await check("invalid persisted patch limits cannot be bypassed by mode selection or settings saves", async () =>
        {
            using var fixture = new Fixture();
            var settings = await fixture.Store.ReadSettingsAsync(fixture.Source, fixture.DirectoryPath);
            foreach (var (field, limits) in InvalidLimits())
            {
                // Formatting is separate from business validation; this represents an invalid manual edit.
                fixture.WritePolicy(new ExecutionPolicy { ProjectEditLimits = limits });
                var before = fixture.Snapshot();
                foreach (var mode in Enum.GetValues<ExecutionMode>())
                {
                    var error = await RejectAsync<ConfigurationValidationException>(() => fixture.Store.LoadAsync(fixture.Source, fixture.DirectoryPath, mode));
                    Assert(error.Field == field, "Mode override must validate limits");
                }
                var save = await RejectAsync<ConfigurationValidationException>(() => fixture.Store.SaveSettingsAsync(settings with
                { Value = settings.Value with { Port = 9000 } }));
                Assert(save.Field == field && before.SequenceEqual(fixture.Snapshot()), "Settings save cannot bypass policy");
            }
            File.WriteAllText(fixture.Paths.PolicyFile, """{"projectEditLimits":null}""");
            var malformed = await RejectAsync<ConfigurationLoadException>(() => fixture.Store.LoadAsync(fixture.Source, fixture.DirectoryPath, ExecutionMode.ReadOnly));
            Assert(malformed.FilePath == fixture.Paths.PolicyFile && malformed.InnerException is JsonException, "Malformed local source");
        });

        await check("external patch limit edits conflict with stale policy saves and remain authoritative on reload", async () =>
        {
            using var fixture = new Fixture();
            fixture.WritePolicy(new ExecutionPolicy());
            var settings = await fixture.Store.ReadSettingsAsync(fixture.Source, fixture.DirectoryPath);
            var original = await fixture.Store.ReadPolicyAsync();
            var external = new ProjectEditLimits { MaxPaths = 0, MaxChangedLines = 0, MaxChangedBytes = 0 };
            fixture.WritePolicy(original.Value with { ProjectEditLimits = external });
            var before = fixture.Snapshot();
            var error = await RejectAsync<ConfigurationConflictException>(() => fixture.Store.SavePolicyAsync(original, settings));
            Assert(error.FilePath == fixture.Paths.PolicyFile && before.SequenceEqual(fixture.Snapshot()), "Conflict leaves external values");
            Assert((await fixture.Store.LoadAsync(fixture.Source, fixture.DirectoryPath)).Policy.ProjectEditLimits == external, "Reload external limits");
        });
    }

    private static IEnumerable<(string Field, ProjectEditLimits Limits)> InvalidLimits()
    {
        foreach (var value in new[] { -1, int.MinValue })
        {
            yield return ("projectEditLimits.maxPaths", new() { MaxPaths = value });
            yield return ("projectEditLimits.maxChangedLines", new() { MaxChangedLines = value });
            yield return ("projectEditLimits.maxChangedBytes", new() { MaxChangedBytes = value });
        }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception("ProjectEdit limits configuration: " + message); }
    private static T Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T exception) { return exception; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
    private static async Task<T> RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T exception) { return exception; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"minimcp-limit-config-{Guid.NewGuid():N}");
        public string Source { get; }
        public ConfigurationPaths Paths { get; }
        public ConfigurationStore Store { get; }
        public Fixture()
        {
            var root = Path.Combine(DirectoryPath, "project");
            Directory.CreateDirectory(root);
            Source = Path.Combine(DirectoryPath, "settings.json");
            Paths = new ConfigurationPaths(Path.Combine(DirectoryPath, "user"), Path.Combine(DirectoryPath, "state"));
            Store = new ConfigurationStore(Paths);
            File.WriteAllText(Source, ConfigurationJson.SerializeSettings(new AppSettings
            { ProjectRoot = root, Authentication = new AuthenticationSettings { Mode = AuthenticationMode.Local } }));
        }
        public void WritePolicy(ExecutionPolicy policy)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Paths.PolicyFile)!);
            File.WriteAllText(Paths.PolicyFile, ConfigurationJson.SerializePolicy(policy));
        }
        public string[] Snapshot() => Directory.EnumerateFileSystemEntries(DirectoryPath, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(DirectoryPath, path) + (Directory.Exists(path) ? "/" : ":" + Convert.ToBase64String(File.ReadAllBytes(path)))).ToArray();
        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
