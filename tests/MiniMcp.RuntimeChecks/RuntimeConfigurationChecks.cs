using MiniMcp.Configuration;
using MiniMcp.Services;

internal static class RuntimeConfigurationChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"minimcp-runtime-config-{Guid.NewGuid():N}");
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
        Directory.CreateDirectory(Path.GetDirectoryName(paths.PolicyFile)!);
        var bash = Path.Combine(temporary, "tools", "bash");
        File.WriteAllText(bash, "Configuration fixture; do not execute.");
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(bash, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        void Assert(bool condition)
        {
            if (!condition) throw new Exception("Runtime configuration assertion failed.");
        }
        async Task<TException> Reject<TException>(Func<Task> action) where TException : Exception
        {
            try { await action(); }
            catch (TException exception) { return exception; }
            throw new Exception($"Expected {typeof(TException).Name} while loading a runtime configuration.");
        }
        async Task Write(AppSettings settings, ExecutionPolicy policy)
        {
            await File.WriteAllTextAsync(source, ConfigurationJson.SerializeSettings(settings));
            await File.WriteAllTextAsync(paths.PolicyFile, ConfigurationJson.SerializePolicy(policy));
        }
        try
        {
            await check("validated runtime snapshots retain their settings after files and drafts change", async () =>
            {
                var policy = new ExecutionPolicy();
                await Write(draft, policy);
                RuntimeConfiguration first = await store.LoadAsync(source, temporary);
                var edited = draft with { Port = 8123 };
                await Write(edited, policy with { Mode = ExecutionMode.ProjectEdit });
                RuntimeConfiguration second = await store.LoadAsync(source, temporary);
                Assert(first.Settings.Port == 8000 && first.Policy.Mode == ExecutionMode.ReadOnly);
                Assert(second.Settings.Port == 8123 && second.Policy.Mode == ExecutionMode.ProjectEdit);
                Assert(first.Settings.ProjectRoot == root && second.Settings.ProjectRoot == root);
                Assert(first.Settings.BashPath == bash && draft.BashPath == "./tools/bash");
                Assert(policy.RecoveryDirectory == "" && first.Policy.RecoveryDirectory == paths.RecoveryDirectory);
            });

            await check("local mode overrides apply to snapshots without rewriting either configuration file", async () =>
            {
                await Write(draft, new ExecutionPolicy { RecoveryDirectory = "../history" });
                var settingsBytes = await File.ReadAllBytesAsync(source);
                var policyBytes = await File.ReadAllBytesAsync(paths.PolicyFile);
                foreach (var mode in Enum.GetValues<ExecutionMode>())
                {
                    var result = await store.LoadAsync(source, temporary, mode: mode);
                    Assert(result.Policy.Mode == mode && result.Settings.Authentication.Mode == AuthenticationMode.Local);
                    Assert(result.Policy.RecoveryDirectory == Path.Combine(temporary, "user", "history"));
                }
                Assert((await File.ReadAllBytesAsync(source)).SequenceEqual(settingsBytes));
                Assert((await File.ReadAllBytesAsync(paths.PolicyFile)).SequenceEqual(policyBytes));
                Assert((await store.ReadPolicyAsync()).Value.Mode == ExecutionMode.ReadOnly);
            });

            await check("loaded runtime configurations retain the normalized selected source for critical-file classification", async () =>
            {
                await Write(draft, new ExecutionPolicy());
                var selected = Path.Combine(root, "custom.md");
                var customDraft = draft with { ProjectRoot = ".", BashPath = "../tools/bash" };
                await File.WriteAllTextAsync(selected, ConfigurationJson.SerializeSettings(customDraft));
                var before = await File.ReadAllBytesAsync(selected);
                var workspace = new ProjectWorkspace(root);
                try
                {
                    var plan = await new PatchService(workspace).PrepareAsync(
                        "*** Begin Patch\n*** Update File: custom.md\n@@\n-  \"port\": 8000,\n+  \"port\": 8123,\n*** End Patch");
                    var preview = PatchDiffBuilder.Create(plan);
                    foreach (var mode in Enum.GetValues<ExecutionMode>())
                    {
                        var configuration = await store.LoadAsync("project/nested/../custom.md", temporary, mode);
                        Assert(configuration.SettingsFilePath == selected && Path.IsPathFullyQualified(configuration.SettingsFilePath));
                        Assert(configuration.Settings.ProjectRoot == root && configuration.Settings.BashPath == bash && configuration.Policy.Mode == mode);
                        var risk = PatchRiskClassifier.Classify(preview, configuration.SettingsFilePath, configuration.Policy.PatchApprovalLimits);
                        Assert(risk.Changes[0].Kind == PatchFileKind.Critical && risk.Reasons == PatchRiskReason.CriticalFile);
                    }
                    Assert((await File.ReadAllBytesAsync(selected)).SequenceEqual(before) && !Directory.Exists(Path.Combine(root, "nested")));
                }
                finally { workspace.Mutations.Dispose(); }
            });

            await check("reselecting settings keeps earlier configuration sources isolated after external edits and deletion", async () =>
            {
                await Write(draft, new ExecutionPolicy());
                var firstSource = Path.Combine(root, "first.md");
                var secondSource = Path.Combine(root, "second.cs");
                var value = draft with { ProjectRoot = ".", BashPath = "" };
                await File.WriteAllTextAsync(firstSource, ConfigurationJson.SerializeSettings(value));
                await File.WriteAllTextAsync(secondSource, ConfigurationJson.SerializeSettings(value with { Port = 8123 }));
                var first = await store.LoadAsync(firstSource, temporary);
                var second = await store.LoadAsync(secondSource, temporary);
                Assert(first.SettingsFilePath == firstSource && second.SettingsFilePath == secondSource);
                var workspace = new ProjectWorkspace(root);
                try
                {
                    var plan = await new PatchService(workspace).PrepareAsync(
                        "*** Begin Patch\n*** Update File: first.md\n@@\n-  \"port\": 8000,\n+  \"port\": 9000,\n*** End Patch");
                    var preview = PatchDiffBuilder.Create(plan);
                    await File.WriteAllTextAsync(firstSource, "external edit");
                    File.Delete(secondSource);
                    Assert(first.SettingsFilePath == firstSource && second.SettingsFilePath == secondSource);
                    Assert(first.Settings.Port == 8000 && second.Settings.Port == 8123);
                    Assert(PatchRiskClassifier.Classify(preview, first.SettingsFilePath, first.Policy.PatchApprovalLimits).Reasons == PatchRiskReason.CriticalFile);
                    Assert(!PatchRiskClassifier.Classify(preview, second.SettingsFilePath, second.Policy.PatchApprovalLimits).RequiresApproval);
                    Assert(await File.ReadAllTextAsync(firstSource) == "external edit" && !File.Exists(secondSource));
                }
                finally { workspace.Mutations.Dispose(); }
            });

            await check("validated in-memory drafts retain their intended source without creating configuration or project directories", async () =>
            {
                var intended = Path.Combine(root, "pending", "draft.md");
                var candidate = Path.Combine(root, "pending", "nested", "..", "draft.md");
                var configuration = new ConfigurationValidator(paths).Validate(draft with { ProjectRoot = root, BashPath = "" },
                    candidate, new ExecutionPolicy());
                Assert(configuration.SettingsFilePath == intended && !Directory.Exists(Path.Combine(root, "pending")));
                var workspace = new ProjectWorkspace(root);
                try
                {
                    var plan = await new PatchService(workspace).PrepareAsync("*** Begin Patch\n*** Add File: pending/draft.md\n+{}\n*** End Patch");
                    var risk = PatchRiskClassifier.Classify(PatchDiffBuilder.Create(plan), configuration.SettingsFilePath, configuration.Policy.PatchApprovalLimits);
                    Assert(risk.Changes[0].Kind == PatchFileKind.Critical && risk.Reasons == PatchRiskReason.CriticalFile);
                    Assert(!File.Exists(intended) && !Directory.Exists(Path.Combine(root, "pending")) && !Directory.Exists(paths.RecoveryDirectory));
                }
                finally { workspace.Mutations.Dispose(); }
            });

            await check("a read-only session override skips Bash required by the persisted execution mode", async () =>
            {
                await Write(draft with { BashPath = "missing-bash" }, new ExecutionPolicy { Mode = ExecutionMode.FullAccess });
                var required = await Reject<ConfigurationValidationException>(() => store.LoadAsync(source, temporary));
                Assert(required.Field == "bashPath");
                var result = await store.LoadAsync(source, temporary, mode: ExecutionMode.ReadOnly);
                Assert(result.Policy.Mode == ExecutionMode.ReadOnly && result.Settings.BashPath == Path.Combine(temporary, "missing-bash"));
                Assert((await store.ReadPolicyAsync()).Value.Mode == ExecutionMode.FullAccess);
            });

            await check("editing mode overrides still require dependencies and reject undefined modes", async () =>
            {
                await Write(draft with { BashPath = "missing-bash" }, new ExecutionPolicy());
                foreach (var mode in new[] { ExecutionMode.ProjectEdit, ExecutionMode.FullAccess })
                {
                    var required = await Reject<ConfigurationValidationException>(() => store.LoadAsync(source, temporary, mode: mode));
                    Assert(required.Field == "bashPath");
                }
                var unknown = await Reject<ConfigurationValidationException>(() => store.LoadAsync(source, temporary, mode: (ExecutionMode)999));
                Assert(unknown.Field == "mode" && (await store.ReadPolicyAsync()).Value.Mode == ExecutionMode.ReadOnly);
            });

            await check("session mode selection cannot bypass malformed policy, authentication or tunnel checks", async () =>
            {
                await Write(draft, new ExecutionPolicy());
                foreach (var json in new[] { "{", """{"mode":"Unknown"}""", """{"schemaVersion":99}""" })
                {
                    await File.WriteAllTextAsync(paths.PolicyFile, json);
                    var error = await Reject<ConfigurationLoadException>(() => store.LoadAsync(source, temporary, mode: ExecutionMode.ReadOnly));
                    Assert(error.FilePath == paths.PolicyFile);
                }
                await Write(draft with { Authentication = new AuthenticationSettings() }, new ExecutionPolicy());
                var authentication = await Reject<ConfigurationValidationException>(() => store.LoadAsync(source, temporary, mode: ExecutionMode.ReadOnly));
                Assert(authentication.Field == "authentication.publicOrigin");
                await Write(draft with { Tunnel = new TunnelSettings { Enabled = true, TokenFilePath = "token" } }, new ExecutionPolicy());
                var tunnel = await Reject<ConfigurationValidationException>(() => store.LoadAsync(source, temporary, mode: ExecutionMode.ReadOnly));
                Assert(tunnel.Field == "tunnel.enabled");
            });

            await check("mode selection preserves cancellation and does not create runtime state storage", async () =>
            {
                await Write(draft, new ExecutionPolicy());
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                await Reject<OperationCanceledException>(() => store.LoadAsync(source, temporary,
                    mode: ExecutionMode.FullAccess, cancellationToken: cancelled.Token));
                Assert(!Directory.Exists(Path.GetDirectoryName(paths.RecoveryDirectory)));
            });
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }
}
