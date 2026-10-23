using System.Net;
using System.Text.Json;
using MiniMcp.Runtime;

using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
{
    Timeout = TimeSpan.FromSeconds(3)
};
var passed = 0;
var failed = 0;

async Task Check(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception exception) { failed++; Console.WriteLine($"FAIL {name}: {exception}"); }
}

async Task Healthy(McpRuntime runtime)
{
    using var response = await client.GetAsync(new Uri(runtime.Address!, "healthz"));
    Assert(response.StatusCode == HttpStatusCode.OK);
    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    Assert(body.RootElement.GetProperty("status").GetString() == "ok");
}

await Check("real loopback health endpoint ignores ambient URLs and exposes no MCP tools", async () =>
{
    var previous = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
    try
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://0.0.0.0:1");
        await using var runtime = new McpRuntime(0);
        await runtime.StartAsync();
        Assert(runtime.Address!.Host == "127.0.0.1" && runtime.Address.Port > 1);
        await Healthy(runtime);
        using var unavailable = await client.GetAsync(new Uri(runtime.Address, "mcp"));
        Assert(unavailable.StatusCode == HttpStatusCode.NotFound);
    }
    finally { Environment.SetEnvironmentVariable("ASPNETCORE_URLS", previous); }
});

await Check("stop is idempotent, releases its port and permits restarting", async () =>
{
    await using var runtime = new McpRuntime(0);
    await runtime.StartAsync();
    var port = runtime.Address!.Port;
    await runtime.StopAsync();
    await runtime.StopAsync();
    Assert(runtime.Address is null);
    await using (var replacement = new McpRuntime(port))
    {
        await replacement.StartAsync();
        await Healthy(replacement);
    }
    await runtime.StartAsync();
    await Healthy(runtime);
});

await Check("concurrent starts create only one host", async () =>
{
    await using var runtime = new McpRuntime(0);
    async Task<bool> Attempt()
    {
        try { await runtime.StartAsync(); return true; }
        catch (InvalidOperationException) { return false; }
    }
    var attempts = await Task.WhenAll(Attempt(), Attempt());
    Assert(attempts.Count(success => success) == 1);
    await Healthy(runtime);
});

await Check("port conflict cleans failed startup without stopping its existing owner", async () =>
{
    await using var owner = new McpRuntime(0);
    await owner.StartAsync();
    await using var contender = new McpRuntime(owner.Address!.Port);
    await RejectAsync<IOException>(() => contender.StartAsync());
    Assert(contender.Address is null);
    await Healthy(owner);
    await owner.StopAsync();
    await contender.StartAsync();
    await Healthy(contender);
});

await Check("cancelled start and stop retain consistent ownership", async () =>
{
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    await using var runtime = new McpRuntime(0);
    await RejectAsync<OperationCanceledException>(() => runtime.StartAsync(cancelled.Token));
    Assert(runtime.Address is null);
    await runtime.StartAsync();
    await RejectAsync<OperationCanceledException>(() => runtime.StopAsync(cancelled.Token));
    await Healthy(runtime);
    await runtime.StopAsync();
    Assert(runtime.Address is null);
});

await Check("disposal releases the port and prevents subsequent startup", async () =>
{
    var runtime = new McpRuntime(0);
    try
    {
        await runtime.StartAsync();
        var port = runtime.Address!.Port;
        await runtime.DisposeAsync();
        await runtime.DisposeAsync();
        await RejectAsync<ObjectDisposedException>(() => runtime.StartAsync());
        await using var replacement = new McpRuntime(port);
        await replacement.StartAsync();
        await Healthy(replacement);
    }
    finally { await runtime.DisposeAsync(); }
});

await Check("invalid ports are rejected", () =>
{
    foreach (var port in new[] { -1, 65536 })
    {
        try { _ = new McpRuntime(port); }
        catch (ArgumentOutOfRangeException) { continue; }
        throw new Exception("Invalid port was accepted.");
    }
    return Task.CompletedTask;
});

Console.WriteLine($"\n{passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;

static void Assert(bool condition)
{
    if (!condition) throw new Exception("Assertion failed.");
}

static async Task RejectAsync<TException>(Func<Task> action) where TException : Exception
{
    try { await action(); }
    catch (TException) { return; }
    throw new Exception($"Expected {typeof(TException).Name}.");
}
