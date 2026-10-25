using System.Net;
using System.Net.Sockets;
using System.Text;
using MiniMcp.Cli;
using MiniMcp.Configuration;
using MiniMcp.Runtime;

internal static class CliCheckCommandChecks
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("CLI check: command help bypasses configuration loading and describes the actual contract", async () =>
        {
            using var fixture = new Fixture();
            foreach (var args in new[]
            {
                Array.Empty<string>(), ["--help"], ["-h"], ["run", "--help"], ["run", "-h"],
                ["check", "--help"], ["check", "-h"], ["check", "--config", "missing.json", "--help"],
                ["run", "--help", "--config", "missing.json"]
            })
            {
                var result = await fixture.Invoke(args, runtimeFactory: () => throw new Exception("Help must not create a Runtime."));
                Assert(result.Code == 0 && result.Error == "" && result.Output.Contains("130") &&
                    !result.Output.Contains("补丁写入关闭"), "Help without configuration");
                if (args.FirstOrDefault() == "check")
                    Assert(result.Output.Contains("minimcp check") && !result.Output.Contains("--mode"), "Check has no session override");
                else Assert(result.Output.Contains("ProjectEdit 允许普通补丁") && result.Output.Contains("cloudflared"), "Actual run capabilities");
            }
            Assert(!Directory.Exists(Path.GetDirectoryName(fixture.Paths.SettingsFile)) && !Directory.Exists(Path.GetDirectoryName(fixture.Paths.RecoveryDirectory)), "Help creates no configuration/state");
        });

        await check("CLI check: shared parsing rejects invalid commands, values, duplicates and run-only options", async () =>
        {
            using var fixture = new Fixture();
            foreach (var args in new[]
            {
                new[] { "CHECK" }, ["--help", "check"], ["check", "--mode", "ReadOnly"], ["check", "--mode", "ReadOnly", "--help"],
                ["check", "--config"], ["check", "--config", ""], ["check", "--config", "--help"], ["check", "--config", "-h"],
                ["check", "--config", "a", "--config", "b"], ["check", "--help", "-h"], ["check", "--help", "--unknown"],
                ["check", "--port", "8000"], ["check", "settings.json"], ["check", "--config=x"], ["check", "run"],
                ["run", "--mode", "readonly", "--help"], ["run", "--mode", "2"], ["run", "--help", "--config"]
            })
            {
                var result = await fixture.Invoke(args);
                Assert(result.Code == 2 && result.Output == "" && result.Error.Contains("参数错误"), "Invalid syntax is not masked by help");
            }
            Assert(!Directory.Exists(Path.GetDirectoryName(fixture.Paths.SettingsFile)) && !Directory.Exists(Path.GetDirectoryName(fixture.Paths.RecoveryDirectory)), "Parsing does not read/create state");
        });

        await check("CLI check: default read-only policy, normalized report and released port without filesystem writes", async () =>
        {
            using var fixture = new Fixture();
            fixture.WriteSettings(fixture.Settings with { BashPath = "missing-bash" });
            var before = fixture.Snapshot();
            var result = await fixture.Invoke(["check"], runtimeFactory: () => throw new Exception("Check must not create a Runtime."));
            Assert(result.Code == 0 && result.Error == "" && result.Output.Contains("校验通过") &&
                result.Output.Contains("ReadOnly") && result.Output.Contains("当前可绑定") &&
                result.Output.Contains(fixture.Root) && result.Output.Contains(fixture.Paths.SettingsFile), "Saved configuration report");
            var diagnostic = await fixture.Store.CheckAsync(null, fixture.Working);
            Assert(diagnostic.PortStatus == PortCheckStatus.Bindable && diagnostic.PortError is null &&
                diagnostic.Configuration.Policy.Mode == ExecutionMode.ReadOnly, "Typed local observation");
            fixture.AssertPortReleased();
            Assert(before.SequenceEqual(fixture.Snapshot()) && !File.Exists(fixture.Paths.PolicyFile) &&
                !Directory.Exists(Path.GetDirectoryName(fixture.Paths.RecoveryDirectory)), "No settings, policy or recovery writes");
        });

        await check("CLI check: selected relative settings use fixed user policy and check saved writable modes without executing Bash", async () =>
        {
            using var fixture = new Fixture();
            var selected = Path.Combine(fixture.Working, "configs", "custom.json");
            Directory.CreateDirectory(Path.GetDirectoryName(selected)!);
            var program = fixture.ProgramFile(Path.Combine(Path.GetDirectoryName(selected)!, "not-a-bash"));
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(selected)!, "policy.json"), "{bad adjacent policy");
            foreach (var mode in new[] { ExecutionMode.ProjectEdit, ExecutionMode.FullAccess })
            {
                fixture.WritePolicy(new ExecutionPolicy { Mode = mode });
                fixture.WriteSettings(fixture.Settings with
                {
                    ProjectRoot = Path.GetRelativePath(Path.GetDirectoryName(selected)!, fixture.Root),
                    BashPath = "./not-a-bash"
                }, selected);
                var before = fixture.Snapshot();
                var result = await fixture.Invoke(["check", "--config", "configs/custom.json"]);
                Assert(result.Code == 0 && result.Error == "" && result.Output.Contains(mode.ToString()) &&
                    result.Output.Contains(program) && result.Output.Contains(fixture.Root), "Fixed user policy and settings-relative paths");
                Assert(before.SequenceEqual(fixture.Snapshot()), "No policy override or program execution");
            }
            Assert(!Directory.Exists(Path.GetDirectoryName(fixture.Paths.RecoveryDirectory)), "Checking writable modes creates no recovery store");
        });

        await check("CLI check: saved execution policy determines required dependencies", async () =>
        {
            using var fixture = new Fixture();
            fixture.WriteSettings(fixture.Settings with { BashPath = "missing-bash" });
            foreach (var mode in Enum.GetValues<ExecutionMode>())
            {
                fixture.WritePolicy(new ExecutionPolicy { Mode = mode });
                var before = fixture.Snapshot();
                var result = await fixture.Invoke(["check"]);
                Assert(result.Code == (mode == ExecutionMode.ReadOnly ? 0 : 2), "Saved dependency requirement");
                if (mode != ExecutionMode.ReadOnly) Assert(result.Error.Contains("bashPath"), "Dependency diagnostic");
                Assert(before.SequenceEqual(fixture.Snapshot()), "Failure does not write configuration");
            }
        });

        await check("CLI check: enabled tunnel checks program and token metadata without executing or exposing contents", async () =>
        {
            using var fixture = new Fixture();
            var program = fixture.ProgramFile();
            var token = Path.Combine(fixture.DirectoryPath, "opaque token.bin");
            File.WriteAllBytes(token, [0xff, .. Encoding.UTF8.GetBytes("private-token-body")]);
            fixture.WriteSettings(fixture.Settings with
            {
                BashPath = "missing-bash",
                Authentication = Cloudflare(),
                Tunnel = new TunnelSettings { Enabled = true, ExecutablePath = program, TokenFilePath = token }
            });
            var before = fixture.Snapshot();
            var result = await fixture.Invoke(["check"]);
            Assert(result.Code == 0 && result.Error == "" && result.Output.Contains("CloudflareAccess") &&
                result.Output.Contains("未启动") && result.Output.Contains("OAuth 未验证") &&
                !result.Output.Contains("private-token-body") && !result.Error.Contains("private-token-body"), "Local checks without public connectivity claims");
            Assert(before.SequenceEqual(fixture.Snapshot()) && !Directory.Exists(Path.GetDirectoryName(fixture.Paths.RecoveryDirectory)), "Invalid executable/token contents were never executed or interpreted");
            fixture.AssertPortReleased();
        });

        await check("CLI check: disabled external features do not require their absent files", async () =>
        {
            using var fixture = new Fixture();
            fixture.WriteSettings(fixture.Settings with
            {
                Authentication = Cloudflare(), BashPath = "missing-bash",
                Tunnel = new TunnelSettings { Enabled = false, ExecutablePath = "missing-cloudflared", TokenFilePath = "missing-token" }
            });
            var before = fixture.Snapshot();
            var result = await fixture.Invoke(["check"]);
            Assert(result.Code == 0 && result.Error == "" && result.Output.Contains("受管隧道：关闭"), "Inactive dependencies skipped");
            Assert(before.SequenceEqual(fixture.Snapshot()), "No fallback files created");
        });

        await check("CLI check: enabled tunnel rejects missing programs and missing, empty or directory token files", async () =>
        {
            using var fixture = new Fixture();
            var program = fixture.ProgramFile();
            var token = Path.Combine(fixture.DirectoryPath, "empty-token");
            File.WriteAllBytes(token, []);
            foreach (var settings in new[]
            {
                new TunnelSettings { Enabled = true, ExecutablePath = "missing-cloudflared", TokenFilePath = token },
                new TunnelSettings { Enabled = true, ExecutablePath = program, TokenFilePath = "missing-token" },
                new TunnelSettings { Enabled = true, ExecutablePath = program, TokenFilePath = token },
                new TunnelSettings { Enabled = true, ExecutablePath = program, TokenFilePath = fixture.Working }
            })
            {
                fixture.WriteSettings(fixture.Settings with { Authentication = Cloudflare(), Tunnel = settings });
                var before = fixture.Snapshot();
                var result = await fixture.Invoke(["check"]);
                Assert(result.Code == 2 && result.Output == "" && result.Error.Contains("tunnel."), "Enabled dependency fails validation");
                Assert(before.SequenceEqual(fixture.Snapshot()), "Invalid dependency leaves no artifacts");
            }
        });

        await check("CLI check: malformed settings, authentication, roots and fixed policy return configuration errors without writes", async () =>
        {
            using var fixture = new Fixture();
            Assert((await fixture.Invoke(["check"])).Code == 2, "Missing selected settings");
            fixture.WriteSettings(fixture.Settings);
            foreach (var json in new[] { "{", """{"port":8000,"port":9000}""", """{"schemaVersion":99}""", """{"unknown":true}""" })
            {
                File.WriteAllText(fixture.Paths.SettingsFile, json);
                var before = fixture.Snapshot();
                var result = await fixture.Invoke(["check"]);
                Assert(result.Code == 2 && result.Output == "" && result.Error.Contains("配置错误"), "Strict JSON");
                Assert(before.SequenceEqual(fixture.Snapshot()), "Invalid JSON preserved");
            }
            File.WriteAllBytes(fixture.Paths.SettingsFile, [0xff]);
            Assert((await fixture.Invoke(["check"])).Code == 2, "Strict UTF-8");
            foreach (var settings in new[]
            {
                fixture.Settings with { Port = 0 }, fixture.Settings with { ProjectRoot = "absent-project" },
                fixture.Settings with { Authentication = Cloudflare() with { Audience = null } },
                fixture.Settings with { Authentication = Cloudflare() with { PublicOrigin = "http://invalid.example" } },
                fixture.Settings with { Authentication = Cloudflare() with { Team = "bad/team" } }
            })
            {
                fixture.WriteSettings(settings);
                var before = fixture.Snapshot();
                Assert((await fixture.Invoke(["check"])).Code == 2 && before.SequenceEqual(fixture.Snapshot()), "Business validation");
            }
            fixture.WriteSettings(fixture.Settings);
            File.WriteAllText(fixture.Paths.PolicyFile, """{"mode":"Unknown"}""");
            var policyBefore = fixture.Snapshot();
            var policy = await fixture.Invoke(["check"]);
            Assert(policy.Code == 2 && policy.Error.Contains(fixture.Paths.PolicyFile) && policyBefore.SequenceEqual(fixture.Snapshot()), "Fixed policy cannot be bypassed");
        });

        await check("CLI check: busy live MCP port is a hint and leaves the current server available", async () =>
        {
            using var fixture = new Fixture();
            fixture.WriteSettings(fixture.Settings);
            await using var owner = new McpRuntime(0);
            await owner.StartAsync(await fixture.Store.LoadAsync(null, fixture.Working));
            fixture.WriteSettings(fixture.Settings with { Port = owner.Address!.Port });
            var before = fixture.Snapshot();
            var result = await fixture.Invoke(["check"]);
            Assert(result.Code == 0 && result.Output.Contains("校验通过") && result.Error.Contains("端口提示"), "Busy port does not invalidate configuration");
            var observation = await fixture.Store.CheckAsync(null, fixture.Working);
            Assert(observation.PortStatus != PortCheckStatus.Bindable && observation.PortError is not null, "Typed unavailable bind result");
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            using var response = await http.GetAsync(new Uri(owner.Address!, "healthz"));
            Assert(response.StatusCode == HttpStatusCode.OK && before.SequenceEqual(fixture.Snapshot()), "Probe did not disturb its owner or files");
            var port = owner.Address!.Port;
            await owner.StopAsync();
            fixture.AssertPortReleased(port);
        });

        await check("CLI check: cancellation returns 130 without state writes and help remains configuration independent", async () =>
        {
            using var fixture = new Fixture();
            fixture.WriteSettings(fixture.Settings);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var before = fixture.Snapshot();
            var result = await fixture.Invoke(["check"], cancelled.Token);
            Assert(result.Code == 130 && result.Output == "" && result.Error.Contains("检查已取消"), "Cancelled load");
            Assert((await fixture.Invoke(["check", "--help"], cancelled.Token)).Code == 0, "Help requires no load");
            Assert(before.SequenceEqual(fixture.Snapshot()), "Cancellation is read-only");
            fixture.AssertPortReleased();
        });

        await check("CLI check: diagnostic credentials are redacted and terminal control characters are visible", async () =>
        {
            using var fixture = new Fixture();
            var sensitive = await fixture.Invoke(["check", "--password=private-argument-value"]);
            Assert(sensitive.Code == 2 && !sensitive.Error.Contains("private-argument-value") && sensitive.Error.Contains("[REDACTED]"), "Argument diagnostic redaction");
            var control = await fixture.Invoke(["check", "--unknown\u001b[2J\nnext"]);
            Assert(control.Code == 2 && !control.Error.Contains('\u001b') && control.Error.Contains("\\u001b[2J\\u000anext"), "Terminal sequences cannot execute");
            var secretPath = await fixture.Invoke(["check", "--config", "password=private-path-value.json"]);
            Assert(secretPath.Code == 2 && !secretPath.Error.Contains("private-path-value"), "Configuration failure redaction");
            var oversized = await fixture.Invoke(["check", "--" + new string('x', 70_000)]);
            Assert(oversized.Code == 2 && oversized.Error.Length < 1000, "Bounded diagnostic source");
        });

        await check("CLI check: broken report and error outputs return failure after port release", async () =>
        {
            using var fixture = new Fixture();
            fixture.WriteSettings(fixture.Settings);
            var before = fixture.Snapshot();
            using var failed = new BrokenWriter();
            using var error = new StringWriter();
            var result = await CliApplication.RunAsync(["check"], failed, error, default, fixture.Store, fixture.Working);
            Assert(result == 1 && !error.ToString().Contains("private-output-value") && error.ToString().Contains("[REDACTED]"), "Report failure is not a configuration error");
            Assert(await CliApplication.RunAsync(["--help"], failed, failed, default, fixture.Store, fixture.Working) == 1, "Closed error channel still preserves failure code");
            Assert(await CliApplication.RunAsync(["check", "--bad"], failed, failed, default, fixture.Store, fixture.Working) == 1, "Error reporting failure cannot escape");
            Assert(before.SequenceEqual(fixture.Snapshot()), "Output failure does not change files");
            fixture.AssertPortReleased();
        });

        await check("CLI check: shared exit contract returns 130 for actual startup cancellation only after successful cleanup", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var checkpoints = new TunnelCheckpoints
            {
                AfterStart = () => { cancellation.Cancel(); return Task.CompletedTask; }
            };
            await using var fixture = new TunnelFixture(checks: checkpoints);
            var directory = fixture.DirectoryPath;
            File.WriteAllText(fixture.Configuration.SettingsFilePath, ConfigurationJson.SerializeSettings(fixture.Configuration.Settings));
            var store = new ConfigurationStore(new ConfigurationPaths(Path.Combine(directory, "user"), Path.Combine(directory, "state")));
            using var output = new StringWriter();
            using var error = new StringWriter();
            var code = await CliApplication.RunAsync(["run", "--config", fixture.Configuration.SettingsFilePath], output, error,
                cancellation.Token, store, directory, () => fixture.Runtime);
            Assert(code == 130 && fixture.Runtime.Snapshot.State == RuntimeState.Disposed &&
                error.ToString().Contains("启动已取消") && !output.ToString().Contains("已停止"), "Actual cancelled startup fully released");
        });

        await check("CLI check: shared exit contract keeps cancelled cleanup as failure and allows a later explicit retry", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var releases = 0;
            var checkpoints = new TunnelCheckpoints
            {
                AfterStart = () => { cancellation.Cancel(); return Task.CompletedTask; },
                BeforeRelease = () => ++releases <= 2
                    ? Task.FromException(new OperationCanceledException("fixture cleanup cancelled")) : Task.CompletedTask
            };
            await using var fixture = new TunnelFixture(checks: checkpoints);
            var directory = fixture.DirectoryPath;
            File.WriteAllText(fixture.Configuration.SettingsFilePath, ConfigurationJson.SerializeSettings(fixture.Configuration.Settings));
            var store = new ConfigurationStore(new ConfigurationPaths(Path.Combine(directory, "user"), Path.Combine(directory, "state")));
            using var output = new StringWriter();
            using var error = new StringWriter();
            try
            {
                var code = await CliApplication.RunAsync(["run", "--config", fixture.Configuration.SettingsFilePath], output, error,
                    cancellation.Token, store, directory, () => fixture.Runtime);
                Assert(code == 1 && fixture.Runtime.Snapshot.State == RuntimeState.Faulted && releases == 2 &&
                    error.ToString().Contains("启动或停止失败") && !error.ToString().Contains("启动已取消") &&
                    !output.ToString().Contains("已停止"), "Cleanup cancellation must not be mislabeled 130");
            }
            finally { await fixture.Runtime.StopAsync(); }
            Assert(releases == 3 && fixture.Runtime.Snapshot.State == RuntimeState.Stopped, "Explicit retry releases the actual cancelled cleanup");
        });
    }

    private static AuthenticationSettings Cloudflare() => new()
    {
        Mode = AuthenticationMode.CloudflareAccess, Team = "fixture", Audience = "fixture-aud", PublicOrigin = "https://fixture.invalid"
    };

    private static void Assert(bool condition, string reason)
    {
        if (!condition) throw new Exception(reason);
    }

    private sealed class BrokenWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void WriteLine(string? value) => throw new IOException("Authorization: Bearer private-output-value");
    }

    private sealed class Fixture : IDisposable
    {
        internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"minimcp-cli-check-{Guid.NewGuid():N}");
        internal string Root { get; }
        internal string Working { get; }
        internal ConfigurationPaths Paths { get; }
        internal ConfigurationStore Store { get; }
        internal AppSettings Settings { get; }

        internal Fixture()
        {
            Root = Directory.CreateDirectory(Path.Combine(DirectoryPath, "project 中 with spaces")).FullName;
            Working = Directory.CreateDirectory(Path.Combine(DirectoryPath, "work")).FullName;
            Paths = new(Path.Combine(DirectoryPath, "user"), Path.Combine(DirectoryPath, "state"));
            Store = new(Paths);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Settings = new AppSettings
            {
                ProjectRoot = Root, Port = ((IPEndPoint)listener.LocalEndpoint).Port,
                Authentication = new AuthenticationSettings { Mode = AuthenticationMode.Local }
            };
        }

        internal void WriteSettings(AppSettings settings, string? path = null)
        {
            path ??= Paths.SettingsFile;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, ConfigurationJson.SerializeSettings(settings));
        }

        internal void WritePolicy(ExecutionPolicy policy)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Paths.PolicyFile)!);
            File.WriteAllText(Paths.PolicyFile, ConfigurationJson.SerializePolicy(policy));
        }

        internal string ProgramFile(string? path = null)
        {
            path ??= Path.Combine(DirectoryPath, "not-an-executable");
            File.WriteAllText(path, "Not an executable image. Checks must never execute this file.");
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }

        internal async Task<(int Code, string Output, string Error)> Invoke(string[] args, CancellationToken cancellationToken = default,
            Func<McpRuntime>? runtimeFactory = null)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var code = await CliApplication.RunAsync(args, output, error, deadline.Token, Store, Working, runtimeFactory);
            return (code, output.ToString(), error.ToString());
        }

        internal string[] Snapshot() => Directory.EnumerateFileSystemEntries(DirectoryPath, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(DirectoryPath, path) + ":" +
                (Directory.Exists(path) ? "directory" : Convert.ToBase64String(File.ReadAllBytes(path)) +
                    (OperatingSystem.IsLinux() ? ":" + File.GetUnixFileMode(path) : ""))).ToArray();

        internal void AssertPortReleased(int? port = null)
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port ?? Settings.Port));
        }

        public void Dispose()
        {
            var target = Path.GetFullPath(DirectoryPath);
            var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(temporary, StringComparison.Ordinal) ||
                !Path.GetFileName(target).StartsWith("minimcp-cli-check-", StringComparison.Ordinal))
                throw new Exception("Unsafe CLI fixture cleanup target.");
            Directory.Delete(target, recursive: true);
        }
    }
}
