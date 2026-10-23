using MiniMcp.Configuration;

internal static class ConfigurationValidatorChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"minimcp-validation-{Guid.NewGuid():N}");
        var root = Path.Combine(temporary, "project");
        var source = Path.Combine(temporary, "config", "settings.json");
        var paths = new ConfigurationPaths(Path.Combine(temporary, "user", "config"), Path.Combine(temporary, "user", "state"));
        var validator = new ConfigurationValidator(paths);
        var settings = new AppSettings { ProjectRoot = root };
        var policy = new ExecutionPolicy();
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        void Assert(bool condition)
        {
            if (!condition) throw new Exception("Configuration path validation assertion failed.");
        }
        void Reject(string field, Action action)
        {
            try { action(); }
            catch (ConfigurationValidationException exception) { Assert(exception.Field == field); return; }
            throw new Exception($"Expected a validation error for {field}.");
        }
        try
        {
            await check("path validation normalizes every configured path without changing the draft", () =>
            {
                var draft = settings with
                {
                    ProjectRoot = "../project/.", BashPath = "./tools/bash",
                    Tunnel = new TunnelSettings { ExecutablePath = "../bin/cloudflared", TokenFilePath = "./token" }
                };
                var draftPolicy = policy with { RecoveryDirectory = "../history" };
                var normalized = validator.NormalizePaths(draft, source, draftPolicy);
                Assert(normalized.Settings.ProjectRoot == root);
                Assert(normalized.Settings.BashPath == Path.Combine(temporary, "config", "tools", "bash"));
                Assert(normalized.Settings.Tunnel.ExecutablePath == Path.Combine(temporary, "bin", "cloudflared"));
                Assert(normalized.Settings.Tunnel.TokenFilePath == Path.Combine(temporary, "config", "token"));
                Assert(normalized.Policy.RecoveryDirectory == Path.Combine(temporary, "user", "history"));
                Assert(draft.ProjectRoot == "../project/." && draft.BashPath == "./tools/bash" && draftPolicy.RecoveryDirectory == "../history");
                Assert(!Directory.Exists(Path.Combine(temporary, "user")) && !File.Exists(source));
                return Task.CompletedTask;
            });

            await check("default recovery uses the fixed state directory and automatic program paths remain empty", () =>
            {
                var normalized = validator.NormalizePaths(settings, source, policy);
                Assert(normalized.Policy.RecoveryDirectory == paths.RecoveryDirectory);
                Assert(normalized.Settings.BashPath == "" && normalized.Settings.Tunnel.ExecutablePath == "");
                Assert(!Directory.Exists(paths.RecoveryDirectory));
                return Task.CompletedTask;
            });

            await check("project validation rejects absent directories, files, home and filesystem roots", () =>
            {
                var file = Path.Combine(temporary, "plain-file");
                File.WriteAllText(file, "fixture");
                foreach (var path in new[] { "", Path.Combine(temporary, "absent"), file, Path.GetPathRoot(root)!,
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) })
                    Reject("projectRoot", () => validator.NormalizePaths(settings with { ProjectRoot = path }, source, policy));
                if (OperatingSystem.IsWindows())
                    Reject("projectRoot", () => validator.NormalizePaths(settings with
                    { ProjectRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).ToUpperInvariant() }, source, policy));
                return Task.CompletedTask;
            });

            await check("policy containment uses path boundaries and native case rules", () =>
            {
                var inside = new ConfigurationValidator(new ConfigurationPaths(Path.Combine(root, "local config"), Path.Combine(temporary, "state")));
                Reject("policyFile", () => inside.NormalizePaths(settings, source, policy));
                if (OperatingSystem.IsWindows())
                    Reject("policyFile", () => inside.NormalizePaths(settings with { ProjectRoot = root.ToUpperInvariant() }, source, policy));
                var sibling = new ConfigurationValidator(new ConfigurationPaths(root + "-config", root + "-state"));
                Assert(sibling.NormalizePaths(settings, source, policy).Settings.ProjectRoot == root);
                return Task.CompletedTask;
            });

            await check("recovery cannot equal, contain or sit inside the project", () =>
            {
                foreach (var path in new[] { root, root + Path.DirectorySeparatorChar, Path.Combine(root, "recovery"), temporary })
                    Reject("recoveryDirectory", () => validator.NormalizePaths(settings, source, policy with { RecoveryDirectory = path }));
                Assert(validator.NormalizePaths(settings, source, policy with { RecoveryDirectory = root + "-recovery" })
                    .Policy.RecoveryDirectory == root + "-recovery");
                return Task.CompletedTask;
            });

            await check("storage validation rejects files occupying directories and malformed optional paths", () =>
            {
                var blocker = Path.Combine(temporary, "blocker");
                File.WriteAllText(blocker, "fixture");
                foreach (var path in new[] { blocker, Path.Combine(blocker, "child") })
                    Reject("recoveryDirectory", () => validator.NormalizePaths(settings, source, policy with { RecoveryDirectory = path }));
                Reject("settingsFile", () => validator.NormalizePaths(settings, Path.GetDirectoryName(source)!, policy));
                Reject("settingsFile", () => validator.NormalizePaths(settings, Path.Combine(blocker, "settings.json"), policy));
                Reject("bashPath", () => validator.NormalizePaths(settings with { BashPath = " " }, source, policy));
                Reject("tunnel.tokenFilePath", () => validator.NormalizePaths(settings with
                { Tunnel = new TunnelSettings { TokenFilePath = "invalid\0path" } }, source, policy));
                return Task.CompletedTask;
            });

            var link = Path.Combine(temporary, "project-link");
            var linksAvailable = true;
            try { Directory.CreateSymbolicLink(link, root); }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
            {
                linksAvailable = false;
                Console.WriteLine("SKIP configuration link checks: Windows symbolic link creation requires Developer Mode or permission.");
            }
            if (linksAvailable)
            {
                await check("project and recovery reject both direct links and linked ancestors", () =>
                {
                    Directory.CreateDirectory(Path.Combine(root, "child"));
                    foreach (var path in new[] { link, Path.Combine(link, "child") })
                        Reject("projectRoot", () => validator.NormalizePaths(settings with { ProjectRoot = path }, source, policy));
                    foreach (var path in new[] { link, Path.Combine(link, "absent", "recovery") })
                        Reject("recoveryDirectory", () => validator.NormalizePaths(settings, source, policy with { RecoveryDirectory = path }));
                    return Task.CompletedTask;
                });
                await check("configuration validation rejects file links, dangling policy links and linked policy directories", () =>
                {
                    File.WriteAllText(source, "{}");
                    var fileLink = Path.Combine(temporary, "settings-link.json");
                    File.CreateSymbolicLink(fileLink, source);
                    Reject("settingsFile", () => validator.NormalizePaths(settings, fileLink, policy));
                    Directory.CreateDirectory(Path.GetDirectoryName(paths.PolicyFile)!);
                    File.CreateSymbolicLink(paths.PolicyFile, Path.Combine(temporary, "missing.json"));
                    try { Reject("policyFile", () => validator.NormalizePaths(settings, source, policy)); }
                    finally { File.Delete(paths.PolicyFile); }
                    var linkedPolicy = new ConfigurationValidator(new ConfigurationPaths(link, Path.Combine(temporary, "state")));
                    Reject("policyFile", () => linkedPolicy.NormalizePaths(settings, source, policy));
                    Assert(File.ReadAllText(source) == "{}");
                    return Task.CompletedTask;
                });
            }
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }
}
