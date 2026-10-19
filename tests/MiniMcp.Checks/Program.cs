using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Configuration;
using MiniMcp.Configuration;
using MiniMcp.Services;

var temporary = Path.Combine(Path.GetTempPath(), $"minimcp-checks-{Guid.NewGuid():N}");
Directory.CreateDirectory(temporary);
var passed = 0;
var failed = 0;
try
{
    var root = Path.Combine(temporary, "project");
    Directory.CreateDirectory(root);
    var options = new MiniMcpOptions(root, "Local", null, null, null);
    var workspace = new ProjectWorkspace(options);

    async Task Check(string name, Func<Task> action)
    {
        try { await action(); passed++; Console.WriteLine($"PASS {name}"); }
        catch (Exception exception) { failed++; Console.WriteLine($"FAIL {name}: {exception}"); }
    }

    await Check("UTF-8 write/read round-trip and nested directories", async () =>
    {
        var result = await workspace.WriteFileAsync("src/示例.cs", "你好，MCP 👋\n");
        Assert(result.Bytes == Encoding.UTF8.GetByteCount("你好，MCP 👋\n"));
        Assert(await workspace.ReadFileAsync("src/示例.cs") == "你好，MCP 👋\n");
    });
    await Check("existing files require explicit overwrite", async () =>
    {
        await RejectAsync<WorkspaceException>(() => workspace.WriteFileAsync("src/示例.cs", "changed"));
        Assert((await workspace.WriteFileAsync("src/示例.cs", "changed", overwrite: true)).Overwritten);
    });
    await Check("path traversal, absolute paths, and ADS syntax rejected", () =>
    {
        foreach (var path in new[] { "../outside", "src/../../outside", temporary, "C:/secrets", "file:stream", "..\\outside" })
            Reject<WorkspaceException>(() => workspace.Resolve(path));
        return Task.CompletedTask;
    });
    await Check("credential paths are blocked and hidden from listings", async () =>
    {
        File.WriteAllText(Path.Combine(root, ".env"), "secret");
        foreach (var path in new[] { ".env", ".envrc", "src/.env.local", ".git/config", ".ssh/id_rsa", "private.key" })
        {
            Reject<WorkspaceException>(() => workspace.Resolve(path));
            await RejectAsync<WorkspaceException>(() => workspace.WriteFileAsync(path, "x"));
        }
        Assert(workspace.ListFiles().Entries.All(e => e.Name != ".env"));
    });
    await Check("limit is UTF-8 bytes, not string length", async () =>
    {
        await RejectAsync<WorkspaceException>(() => workspace.WriteFileAsync("large.txt", new string('中', 100_000)));
        Assert(!File.Exists(Path.Combine(root, "large.txt")));
    });
    await Check("exactly 256 KiB accepted; larger files rejected", async () =>
    {
        var text = new string('a', ProjectWorkspace.MaxFileBytes);
        await workspace.WriteFileAsync("limit.txt", text);
        Assert((await workspace.ReadFileAsync("limit.txt")).Length == text.Length);
        File.WriteAllText(Path.Combine(root, "too-large.txt"), text + "a");
        await RejectAsync<WorkspaceException>(() => workspace.ReadFileAsync("too-large.txt"));
    });
    await Check("binary and invalid UTF-8 rejected; UTF-8 BOM accepted", async () =>
    {
        File.WriteAllBytes(Path.Combine(root, "binary.dat"), [0, 1, 2]);
        File.WriteAllBytes(Path.Combine(root, "invalid.dat"), [0xff, 0xfe]);
        File.WriteAllBytes(Path.Combine(root, "bom.txt"), [0xef, 0xbb, 0xbf, 0x61]);
        await RejectAsync<WorkspaceException>(() => workspace.ReadFileAsync("binary.dat"));
        await RejectAsync<WorkspaceException>(() => workspace.ReadFileAsync("invalid.dat"));
        await RejectAsync<WorkspaceException>(() => workspace.WriteFileAsync("null.txt", "a\0b"));
        Assert(await workspace.ReadFileAsync("bom.txt") == "a");
    });
    await Check("cancelled writes preserve existing content and leave no temporary file", async () =>
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await RejectAsync<OperationCanceledException>(() =>
            workspace.WriteFileAsync("src/示例.cs", "bad", overwrite: true, cancelled.Token));
        Assert(await workspace.ReadFileAsync("src/示例.cs") == "changed");
        Assert(!Directory.EnumerateFiles(root, ".minimcp-*.tmp", SearchOption.AllDirectories).Any());
    });
    await Check("directory listing is capped and signals truncation", () =>
    {
        var dir = Path.Combine(root, "many");
        Directory.CreateDirectory(dir);
        for (var i = 0; i < ProjectWorkspace.MaxEntries + 1; i++)
            File.WriteAllText(Path.Combine(dir, $"{i:D4}.txt"), "");
        var listing = workspace.ListFiles("many");
        Assert(listing.Entries.Length == ProjectWorkspace.MaxEntries && listing.Truncated);
        return Task.CompletedTask;
    });

    if (!OperatingSystem.IsWindows())
    {
        await Check("file, directory, and dangling symlinks cannot escape the root", async () =>
        {
            var outside = Path.Combine(temporary, "outside");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
            Directory.CreateSymbolicLink(Path.Combine(root, "linked"), outside);
            File.CreateSymbolicLink(Path.Combine(root, "linked-file"), Path.Combine(outside, "secret.txt"));
            File.CreateSymbolicLink(Path.Combine(root, "dangling"), Path.Combine(outside, "missing.txt"));
            foreach (var path in new[] { "linked/secret.txt", "linked-file", "dangling" })
            {
                Reject<WorkspaceException>(() => workspace.Resolve(path));
                await RejectAsync<WorkspaceException>(() => workspace.WriteFileAsync(path, "bad", overwrite: true));
            }
            Assert(File.ReadAllText(Path.Combine(outside, "secret.txt")) == "secret");
            Directory.CreateSymbolicLink(Path.Combine(temporary, "root-link"), root);
            Reject<WorkspaceException>(() => new ProjectWorkspace(options with { Root = Path.Combine(temporary, "root-link") }));
        });
    }

    await Check("authentication defaults to protected mode and requires complete configuration", () =>
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MCP_ROOT"] = root
        }).Build();
        Reject<InvalidOperationException>(() => MiniMcpOptions.Load(config));
        config["MCP_PUBLIC_URL"] = "https://mcp.example.com";
        config["CF_ACCESS_TEAM"] = "my-team";
        config["CF_ACCESS_AUD"] = "test-audience";
        Assert(!MiniMcpOptions.Load(config).IsLocal);
        config["MCP_PUBLIC_URL"] = "https://mcp.example.com/mcp";
        Reject<InvalidOperationException>(() => MiniMcpOptions.Load(config));
        config["MCP_PUBLIC_URL"] = "http://mcp.example.com";
        Reject<InvalidOperationException>(() => MiniMcpOptions.Load(config));
        config["MCP_AUTH_MODE"] = "Typo";
        Reject<InvalidOperationException>(() => MiniMcpOptions.Load(config));
        return Task.CompletedTask;
    });
    await Check("build disabled by default", () =>
        RejectAsync<WorkspaceException>(() => new BuildRunner(workspace, options).BuildAsync()));

    if (!OperatingSystem.IsWindows())
    {
        var bin = Path.Combine(temporary, "bin");
        Directory.CreateDirectory(bin);
        var shim = Path.Combine(bin, "dotnet");
        void Shim(string body)
        {
            File.WriteAllText(shim, "#!/bin/sh\n" + body + "\n");
            File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var runner = new BuildRunner(workspace, options with { EnableBuild = true, BuildTimeoutSeconds = 2 }, shim);
        await Check("build uses fixed arguments, reports nonzero exit, and drains stderr", async () =>
        {
            Shim("printf '%s\\n' \"$@\"\nprintf 'compiler diagnostic\\n' >&2\nexit 7");
            var result = await runner.BuildAsync();
            Assert(result.ExitCode == 7 && !result.TimedOut, System.Text.Json.JsonSerializer.Serialize(result));
            Assert(result.Output.Contains("compiler diagnostic") && result.Output.Contains("--nologo"));
            Assert(result.Output.Contains(root));
        });
        await Check("build output remains bounded without deadlocking", async () =>
        {
            Shim("i=0\nwhile [ \"$i\" -lt 3000 ]; do\n printf 'abcdefghijklmnopqrstuvwxyz\\n'\n printf 'abcdefghijklmnopqrstuvwxyz\\n' >&2\n i=$((i+1))\ndone");
            var result = await runner.BuildAsync();
            Assert(result.ExitCode == 0 && result.OutputTruncated && result.Output.Length == BuildRunner.MaxOutputCharacters,
                System.Text.Json.JsonSerializer.Serialize(result));
        });
        await Check("build timeout kills descendants", async () =>
        {
            var pidFile = Path.Combine(temporary, "child.pid");
            Shim($"sleep 30 &\nchild=$!\nprintf '%s' \"$child\" > '{pidFile}'\nwait");
            var result = await runner.BuildAsync();
            Assert(result.TimedOut && result.ExitCode is null, System.Text.Json.JsonSerializer.Serialize(result));
            var pid = int.Parse(File.ReadAllText(pidFile));
            try
            {
                using var child = Process.GetProcessById(pid);
                // A killed process can remain a zombie briefly until its parent is reaped.
                var stat = $"/proc/{pid}/stat";
                Assert(child.HasExited || (File.Exists(stat) && File.ReadAllText(stat).Contains(") Z ")));
            }
            catch (ArgumentException) { }
        });
        await Check("client cancellation ends a running build and releases its lock", async () =>
        {
            Shim("sleep 30");
            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await RejectAsync<OperationCanceledException>(() => runner.BuildAsync(cancellationToken: cancelled.Token));
            Shim("exit 0");
            Assert((await runner.BuildAsync()).ExitCode == 0);
        });
    }

    Console.WriteLine($"\n{passed} passed, {failed} failed.");
    return failed == 0 ? 0 : 1;
}
finally
{
    Directory.Delete(temporary, recursive: true);
}

static void Assert(bool condition, string message = "Assertion failed.")
{
    if (!condition) throw new Exception(message);
}

static void Reject<TException>(Action action) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new Exception($"Expected {typeof(TException).Name}.");
}

static async Task RejectAsync<TException>(Func<Task> action) where TException : Exception
{
    try { await action(); }
    catch (TException) { return; }
    throw new Exception($"Expected {typeof(TException).Name}.");
}
