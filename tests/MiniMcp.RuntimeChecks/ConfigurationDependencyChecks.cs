using MiniMcp.Configuration;

internal static class ConfigurationDependencyChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"minimcp-dependencies-{Guid.NewGuid():N}");
        var source = Path.Combine(temporary, "settings.json");
        var root = Path.Combine(temporary, "project");
        var paths = new ConfigurationPaths(Path.Combine(temporary, "user"), Path.Combine(temporary, "state"));
        var local = new AppSettings
        {
            ProjectRoot = "project", Authentication = new AuthenticationSettings { Mode = AuthenticationMode.Local }
        };
        var cloud = local with
        {
            Authentication = new AuthenticationSettings
            { PublicOrigin = "https://mcp.example.com", Team = "test-team", Audience = "test-aud" },
            Tunnel = new TunnelSettings { Enabled = true, ExecutablePath = "tools/cloudflared", TokenFilePath = "token" }
        };
        var readOnly = new ExecutionPolicy();
        var edit = readOnly with { Mode = ExecutionMode.ProjectEdit };
        Directory.CreateDirectory(root);
        string Put(string relative, string text = "Discovery fixture; this file must never be executed.")
        {
            var path = Path.GetFullPath(Path.Combine(temporary, relative));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }
        var bash = Put("tools/bash");
        var cloudflared = Put("tools/cloudflared");
        var token = Put("token", "private-fixture-token");
        ConfigurationValidator Validator(Func<string, string, string?>? findBash = null,
            Func<string, string, string?>? findCloudflared = null) => new(paths,
                findBash ?? ((path, file) => ExternalProgramLocator.FindBash(path, file, [], null)),
                findCloudflared ?? ((path, file) => ExternalProgramLocator.FindCloudflared(path, file, [], null)));
        void Assert(bool condition)
        {
            if (!condition) throw new Exception("Configuration dependency assertion failed.");
        }
        void Reject(string field, Action action)
        {
            try { action(); }
            catch (ConfigurationValidationException exception)
            {
                Assert(exception.Field == field && !exception.ToString().Contains("private-fixture-token", StringComparison.Ordinal));
                return;
            }
            throw new Exception($"Expected a dependency error for {field}.");
        }
        try
        {
            await check("read-only configuration skips Bash and disabled tunnel dependencies", () =>
            {
                var validator = Validator((_, _) => throw new Exception("Bash discovery must not run."),
                    (_, _) => throw new Exception("Tunnel discovery must not run."));
                var settings = local with
                {
                    BashPath = "missing-bash",
                    Tunnel = new TunnelSettings { ExecutablePath = "missing-cloudflared", TokenFilePath = "missing-token" }
                };
                var result = validator.Validate(settings, source, readOnly);
                Assert(result.Settings.ProjectRoot == root && result.Policy.Mode == ExecutionMode.ReadOnly);
                Assert(result.Settings.BashPath == Path.Combine(temporary, "missing-bash"));
                Assert(!Directory.Exists(Path.GetDirectoryName(paths.PolicyFile)) && !Directory.Exists(paths.RecoveryDirectory));
                return Task.CompletedTask;
            });

            await check("editing modes require Bash and retain explicit program paths without execution", () =>
            {
                var validator = Validator();
                foreach (var mode in new[] { ExecutionMode.ProjectEdit, ExecutionMode.FullAccess })
                {
                    var policy = readOnly with { Mode = mode };
                    Reject("bashPath", () => validator.Validate(local, source, policy));
                    var result = validator.Validate(local with { BashPath = "tools/bash" }, source, policy);
                    Assert(result.Settings.BashPath == bash && result.Policy.Mode == mode);
                    Reject("bashPath", () => validator.Validate(local with { BashPath = "missing-bash" }, source, policy));
                }
                Assert(local.BashPath == "" && readOnly.RecoveryDirectory == "");
                return Task.CompletedTask;
            });

            await check("automatic program candidates are resolved into the validated settings", () =>
            {
                var validator = Validator((_, _) => bash, (_, _) => cloudflared);
                var settings = cloud with { BashPath = "", Tunnel = cloud.Tunnel with { ExecutablePath = "" } };
                var result = validator.Validate(settings, source, edit);
                Assert(result.Settings.BashPath == bash && result.Settings.Tunnel.ExecutablePath == cloudflared);
                Assert(settings.BashPath == "" && settings.Tunnel.ExecutablePath == "");
                return Task.CompletedTask;
            });

            await check("required program candidates must be absolute files rather than directories", () =>
            {
                Reject("bashPath", () => Validator((_, _) => root).Validate(local, source, edit));
                Reject("bashPath", () => Validator((_, _) => "relative-program").Validate(local, source, edit));
                Reject("tunnel.executablePath", () => Validator(findCloudflared: (_, _) => root).Validate(cloud, source, readOnly));
                return Task.CompletedTask;
            });

            await check("enabled tunnels require cloudflared but do not require Bash in read-only mode", () =>
            {
                var validator = Validator((_, _) => throw new Exception("Read-only mode must not discover Bash."));
                var result = validator.Validate(cloud, source, readOnly);
                Assert(result.Settings.Tunnel.ExecutablePath == cloudflared && result.Settings.Tunnel.TokenFilePath == token);
                Reject("tunnel.executablePath", () => validator.Validate(cloud with
                { Tunnel = cloud.Tunnel with { ExecutablePath = "" } }, source, readOnly));
                Reject("tunnel.executablePath", () => validator.Validate(cloud with
                { Tunnel = cloud.Tunnel with { ExecutablePath = "missing-cloudflared" } }, source, readOnly));
                Assert(File.ReadAllText(token) == "private-fixture-token");
                return Task.CompletedTask;
            });

            await check("enabled tunnel token files must exist, be nonempty and open for reading", () =>
            {
                var validator = Validator();
                var empty = Put("empty-token", "");
                foreach (var path in new[] { Path.Combine(temporary, "missing-token"), empty, root })
                    Reject("tunnel.tokenFilePath", () => validator.Validate(cloud with
                    { Tunnel = cloud.Tunnel with { TokenFilePath = path } }, source, readOnly));
                if (OperatingSystem.IsWindows())
                {
                    using var locked = new FileStream(token, FileMode.Open, FileAccess.Read, FileShare.None);
                    Reject("tunnel.tokenFilePath", () => validator.Validate(cloud, source, readOnly));
                }
                return Task.CompletedTask;
            });

            if (OperatingSystem.IsLinux())
            {
                await check("Linux dependency checks reject missing execute permission bits", () =>
                {
                    if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
                    File.SetUnixFileMode(bash, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    try { Reject("bashPath", () => Validator().Validate(local with { BashPath = bash }, source, edit)); }
                    finally { File.SetUnixFileMode(bash, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
                    File.SetUnixFileMode(cloudflared, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    try { Reject("tunnel.executablePath", () => Validator().Validate(cloud, source, readOnly)); }
                    finally { File.SetUnixFileMode(cloudflared, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
                    return Task.CompletedTask;
                });
            }

            await check("file loading uses the shared validation pipeline without environment overrides", async () =>
            {
                var variables = new[] { "MCP_ROOT", "MCP_PORT", "MCP_AUTH_MODE" };
                var previous = variables.Select(Environment.GetEnvironmentVariable).ToArray();
                try
                {
                    Environment.SetEnvironmentVariable(variables[0], Path.Combine(temporary, "other-project"));
                    Environment.SetEnvironmentVariable(variables[1], "12345");
                    Environment.SetEnvironmentVariable(variables[2], "CloudflareAccess");
                    await File.WriteAllTextAsync(source, """{"projectRoot":"project","authentication":{"mode":"Local"}}""");
                    var loader = new ConfigurationStore(paths);
                    var result = await loader.LoadAsync(source, temporary);
                    Assert(result.Settings.ProjectRoot == root && result.Settings.Port == 8000);
                    Assert(result.Settings.Authentication.Mode == AuthenticationMode.Local && result.Policy.Mode == ExecutionMode.ReadOnly);
                    Assert(result.Policy.RecoveryDirectory == paths.RecoveryDirectory);
                    Assert((await loader.ReadSettingsAsync(source, temporary)).Value.ProjectRoot == "project");
                    Assert(!Directory.Exists(Path.GetDirectoryName(paths.PolicyFile)));
                }
                finally
                {
                    for (var index = 0; index < variables.Length; index++) Environment.SetEnvironmentVariable(variables[index], previous[index]);
                }
            });

            await check("combined file loading reports value and path failures and preserves cancellation", async () =>
            {
                var loader = new ConfigurationStore(paths);
                foreach (var (json, field) in new[]
                {
                    ("""{"projectRoot":"project","port":0,"authentication":{"mode":"Local"}}""", "port"),
                    ("""{"projectRoot":"absent-project","authentication":{"mode":"Local"}}""", "projectRoot")
                })
                {
                    await File.WriteAllTextAsync(source, json);
                    try { await loader.LoadAsync(source, temporary); throw new Exception("Invalid file configuration was accepted."); }
                    catch (ConfigurationValidationException exception) { Assert(exception.Field == field); }
                }
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                try { await loader.LoadAsync(source, temporary, cancelled.Token); throw new Exception("Cancellation was ignored."); }
                catch (OperationCanceledException) { }
            });
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }
}
