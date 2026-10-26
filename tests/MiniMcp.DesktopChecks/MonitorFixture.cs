using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace MiniMcp.DesktopChecks;

internal sealed class MonitorFixture : IAsyncDisposable
{
    internal SettingsFixture Settings { get; } = new();
    internal McpRuntime Runtime { get; }
    private ILogger? _logger;
    internal ILogger Logger => _logger ?? throw new InvalidOperationException("Capture the running logger first.");
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false });
    internal MonitorFixture() => Runtime = new(0, configureEndpoints: Map);
    private void Map(IEndpointRouteBuilder endpoints) => endpoints.MapGet("/capture-logger", (ILoggerFactory factory) =>
    { _logger = factory.CreateLogger("Desktop.Fixture"); return Results.Ok(); });
    internal async Task Start()
    {
        await Settings.Load(); var saved = await Settings.Editor.ReadSavedForStartupAsync();
        await Runtime.StartAsync(saved.Configuration); await Capture();
    }
    internal async Task Capture() => (await _http.GetAsync(new Uri(Runtime.Address!, "capture-logger"))).EnsureSuccessStatusCode();
    public async ValueTask DisposeAsync() { await Runtime.DisposeAsync(); _http.Dispose(); await Settings.DisposeAsync(); }
}

internal sealed class ManualDispatcher
{
    private readonly ConcurrentQueue<Action> _pending = new();
    internal int Pending => _pending.Count;
    internal bool Post(Action action) { _pending.Enqueue(action); return true; }
    internal void Drain(int max = 16) { for (var count = 0; count < max && _pending.TryDequeue(out var action); count++) action(); }
    internal async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition()) { Drain(); if (DateTime.UtcNow >= deadline) throw new TimeoutException("Monitor condition did not complete"); await Task.Delay(10); }
        Drain();
    }
}
