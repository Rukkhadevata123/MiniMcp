using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MiniMcp.Runtime;
using ModelContextProtocol.Client;

internal static class RuntimeDiagnosticsChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("diagnostics: count bounds, immutable initial views, clear and monotonic sequence", () =>
        {
            var (diagnostics, _, logger) = Create();
            logger.LogInformation("initial");
            using var subscription = diagnostics.SubscribeLogs(default);
            var before = diagnostics.GetLogs();
            for (var i = 0; i < 1100; i++) logger.LogInformation("entry {Index}", i);
            var after = diagnostics.GetLogs();
            Assert(before.Entries.Length == 1 && subscription.Initial.SequenceEqual(before.Entries), "Initial remains immutable");
            Assert(after.Entries.Length == RuntimeDiagnostics.MaxEntries && after.Entries[0].Sequence == 102, "Count bound");
            Assert(after.CharacterCount == after.Entries.Sum(Size), "Exact retained size");
            diagnostics.ClearLogs();
            Assert(diagnostics.GetLogs() is { Entries.Length: 0, CharacterCount: 0 }, "Clear buffer");
            logger.LogInformation("after clear");
            Assert(diagnostics.GetLogs().Entries.Single().Sequence == 1102, "Sequence never resets");
            return Task.CompletedTask;
        });

        await check("diagnostics: aggregate characters, entry/category limits and Unicode clipping", () =>
        {
            var (diagnostics, _, logger) = Create(new string('c', 500));
            var message = new string('x', RuntimeDiagnostics.MaxEntryCharacters - 2) + "👋tail";
            for (var i = 0; i < 200; i++) logger.LogInformation("{Message}", message);
            var snapshot = diagnostics.GetLogs();
            Assert(snapshot.Entries.Length < 200 && snapshot.CharacterCount <= RuntimeDiagnostics.MaxCharacters, "Character bound evicts");
            Assert(snapshot.CharacterCount == snapshot.Entries.Sum(Size), "Category contributes to bound");
            foreach (var entry in snapshot.Entries)
            {
                Assert(entry.Category.Length <= RuntimeDiagnostics.MaxCategoryCharacters && entry.Message.Length <= RuntimeDiagnostics.MaxEntryCharacters && entry.Truncated, "Per entry bounds");
                Assert(WellFormed(entry.Message), "No dangling surrogate");
            }
            return Task.CompletedTask;
        });

        await check("diagnostics: ILogger levels, event identity, exception capture and formatting failures", () =>
        {
            var (diagnostics, run, logger) = Create();
            logger.LogTrace("trace");
            logger.LogDebug("debug");
            logger.Log(LogLevel.None, "none");
            logger.LogWarning(new EventId(42, "unused"), new IOException("disk unavailable"), "Operation {Operation}", "read");
            logger.Log(LogLevel.Information, default, "state", null, (_, _) => throw new Exception("secret formatter failure"));
            var entries = diagnostics.GetLogs().Entries;
            Assert(entries.Length == 2, "Information and above");
            Assert(entries[0] is { Level: LogLevel.Warning, EventId: 42, Category: "checks" } &&
                entries[0].RunId == run && entries[0].Timestamp.Offset == TimeSpan.Zero &&
                entries[0].Message.Contains("disk unavailable") && entries[0].Message.Contains("read"), "Structured metadata and exception");
            Assert(entries[1].Message == "[log entry omitted: formatting failed]" && entries[1].Truncated, "Formatter failure withheld");
            return Task.CompletedTask;
        });

        await check("diagnostics: credential fields, JSON escapes, authentication, JWT and URL redaction", () =>
        {
            var (diagnostics, _, logger) = Create("category?api_key=category-private");
            string[] messages =
            [
                "Authorization: Bearer private-value",
                "Proxy-Authorization=Basic private-value",
                "Cookie: session=private-value; other=value",
                "Set-Cookie: session=private-value\r\n folded-private-value",
                "Cf-Access-Jwt-Assertion: private-value",
                "CF-Access-Client-Secret=private-value",
                """{"access_token":"private-value","refreshToken":"private-value","safe":"retained"}""",
                """{"password":"private-\"quoted-value","safe":"retained"}""",
                "clientSecret='private-value'\nstatus=ready",
                "api-key: private-value", "api key: private-value",
                "token=\"private-value\nprivate-continuation\"",
                "token=\"private-value\nprivate-continuation",
                "using Bearer private-value with request",
                "using Basic private-value with request",
                "eyJhbGciOiJSUzI1NiJ9.cHJpdmF0ZS12YWx1ZQ.c2lnbmF0dXJl",
                "https://user:private-value@example.invalid/path?access_token=private-value",
                "-----BEGIN RSA PRIVATE KEY-----\nprivate-value"
            ];
            foreach (var message in messages) logger.LogInformation("{Text}", message);
            var entries = diagnostics.GetLogs().Entries;
            Assert(entries.Length == messages.Length, "Each input produces a safe diagnostic");
            foreach (var entry in entries)
                Assert(!entry.Message.Contains("private-", StringComparison.Ordinal) &&
                    !entry.Message.Contains("cHJpdmF0ZS12YWx1ZQ", StringComparison.Ordinal) &&
                    !entry.Category.Contains("category-private"), "Credential absent: " + entry.Message);
            Assert(entries[6].Message.Contains("retained") && entries[8].Message.Contains("ready"), "Safe context remains where delimited");
            return Task.CompletedTask;
        });

        await check("diagnostics: sensitive structured values cannot escape through custom formatting", () =>
        {
            var (diagnostics, _, logger) = Create();
            logger.LogInformation("Login {Password}", new[] { "private-a", "private-b" });
            logger.Log(LogLevel.Warning, default,
                new[] { new KeyValuePair<string, object?>("clientSecret", "private-value") },
                new Exception("private-exception"), (_, _) => "encoded-private-formatter");
            logger.LogInformation("safe {Operation}", "read");
            Assert(diagnostics.GetLogs().Entries.Take(2).All(entry => entry.Message == "[log entry redacted: credential field]"), "Entire credential entry withheld");
            Assert(diagnostics.GetLogs().Entries[2].Message == "safe read", "Ordinary structured logs preserved");
            return Task.CompletedTask;
        });

        await check("diagnostics: oversized messages and exception/category input are withheld before clipping", () =>
        {
            var (diagnostics, _, logger) = Create(new string('c', 70_000));
            logger.LogInformation("{Text}", "private-prefix" + new string('x', 70_000) + " token=private-value");
            logger.LogError(new Exception(new string('e', 70_000)), "operation");
            foreach (var entry in diagnostics.GetLogs().Entries)
                Assert(entry.Message == LogSanitizer.Omitted && entry.Category == LogSanitizer.Omitted && entry.Truncated, "Oversized input omitted");
            return Task.CompletedTask;
        });

        await check("diagnostics: multicast readers do not compete and slow observers stay bounded", () =>
        {
            var (diagnostics, _, logger) = Create();
            using var fast = diagnostics.SubscribeLogs(default);
            using var slow = diagnostics.SubscribeLogs(default);
            var fastEntries = new List<RuntimeLogEntry>();
            for (var i = 0; i < 4000; i++)
            {
                logger.LogInformation("entry {Index}", i);
                Assert(fast.Reader.TryRead(out var entry), "Fast reader gets each publication");
                fastEntries.Add(entry!);
            }
            var slowEntries = Drain(slow.Reader);
            Assert(fastEntries.Count == 4000 && slowEntries.Count == RuntimeDiagnostics.LogSubscriptionCapacity, "Slow queue bounded");
            Assert(slowEntries.SequenceEqual(fastEntries.TakeLast(slowEntries.Count)), "Independent reader keeps newest");
            return Task.CompletedTask;
        });

        await check("diagnostics: typed states coalesce independently of dropped log entries", () =>
        {
            var (diagnostics, run, logger) = Create();
            using var first = diagnostics.SubscribeState(default);
            using var second = diagnostics.SubscribeState(default);
            using var logs = diagnostics.SubscribeLogs(default);
            Assert(first.Initial.Single().State == RuntimeState.Starting, "Atomic initial state");
            for (var i = 0; i < 2000; i++) logger.LogInformation("noise");
            diagnostics.Publish(new(RuntimeState.Running, run));
            var final = new RuntimeSnapshot(RuntimeState.Stopped, run);
            diagnostics.Publish(final);
            Assert(ReferenceEquals(first.Reader.ReadAsync().Result, final) && ReferenceEquals(second.Reader.ReadAsync().Result, final), "Multicast final state");
            Assert(!first.Reader.TryRead(out _) && ReferenceEquals(diagnostics.Snapshot, final), "Single latest snapshot");
            return Task.CompletedTask;
        });

        await check("diagnostics: observer continuations cannot block publishers", async () =>
        {
            var (diagnostics, _, logger) = Create();
            using var subscription = diagnostics.SubscribeLogs(default);
            using var release = new ManualResetEventSlim();
            var callback = subscription.Reader.ReadAsync().AsTask().ContinueWith(_ => release.Wait(TimeSpan.FromSeconds(5)),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try
            {
                await Task.Run(() => logger.LogInformation("publication")).WaitAsync(TimeSpan.FromSeconds(2));
                Assert(!release.IsSet, "Producer completed while observer is blocked");
            }
            finally { release.Set(); }
            await callback.WaitAsync(TimeSpan.FromSeconds(2));
        });

        await check("diagnostics: cancellation, disposal and subscription races release registrations", async () =>
        {
            var (diagnostics, _, logger) = Create();
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            using (var preCancelled = diagnostics.SubscribeLogs(cancelled.Token))
                await preCancelled.Reader.Completion;
            using var cancellation = new CancellationTokenSource();
            using var logs = diagnostics.SubscribeLogs(cancellation.Token);
            using var states = diagnostics.SubscribeState(cancellation.Token);
            var waiting = logs.Reader.WaitToReadAsync().AsTask();
            cancellation.Cancel();
            Assert(!await waiting, "Pending read completes");
            await states.Reader.Completion;
            await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() =>
            {
                using var token = new CancellationTokenSource();
                var cancel = Task.Run(token.Cancel);
                using var subscription = diagnostics.SubscribeLogs(token.Token);
                cancel.GetAwaiter().GetResult();
            })));
            logger.LogInformation("after unsubscribe");
            Assert(diagnostics.SubscriberCounts == (0, 0) && !logs.Reader.TryRead(out _), "No dangling observers");
        });

        await check("diagnostics: stale run loggers and state publications cannot affect a replacement run", () =>
        {
            var (diagnostics, run, logger) = Create();
            logger.LogInformation("old history");
            diagnostics.EndRun(run);
            var formatted = false;
            logger.Log(LogLevel.Error, default, "", null, (_, _) => { formatted = true; return "old late event"; });
            var next = Guid.NewGuid();
            diagnostics.BeginRun(new(RuntimeState.Starting, next));
            logger.LogInformation("old after replacement");
            Assert(!diagnostics.Publish(new(RuntimeState.Faulted, run, Error: "old failure")), "Old state rejected");
            new RuntimeLoggerProvider(diagnostics, next).CreateLogger("new").LogInformation("new event");
            Assert(!formatted && diagnostics.Snapshot.RunId == next && diagnostics.Snapshot.State == RuntimeState.Starting, "No stale formatting or state");
            var entries = diagnostics.GetLogs().Entries;
            Assert(entries.Length == 2 && entries[0].RunId == run && entries[1].RunId == next, "History keeps explicit run identity");
            return Task.CompletedTask;
        });

        await check("diagnostics: concurrent log writers preserve ordered identities and exact bounds", async () =>
        {
            var (diagnostics, _, logger) = Create();
            await Task.WhenAll(Enumerable.Range(0, 8).Select(writer => Task.Run(() =>
            {
                for (var i = 0; i < 300; i++) logger.LogInformation("writer {Writer} entry {Index}", writer, i);
            })));
            var snapshot = diagnostics.GetLogs();
            Assert(snapshot.Entries.Length == 1000 && snapshot.Entries[^1].Sequence == 2400, "Concurrent bounded writes");
            Assert(snapshot.Entries.Zip(snapshot.Entries.Skip(1)).All(pair => pair.Second.Sequence == pair.First.Sequence + 1), "Publication order");
            Assert(snapshot.CharacterCount == snapshot.Entries.Sum(Size), "Concurrent size accounting");
        });

        await check("diagnostics: subscribing during publication has no gap between initial and live entries", async () =>
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var (diagnostics, _, logger) = Create();
                var writing = Task.Run(() =>
                {
                    for (var i = 0; i < 100; i++) logger.LogInformation("entry {Index}", i);
                });
                using var subscription = diagnostics.SubscribeLogs(default);
                await writing;
                var all = subscription.Initial.Concat(Drain(subscription.Reader)).ToArray();
                Assert(all.Select(entry => entry.Sequence).SequenceEqual(Enumerable.Range(1, 100).Select(i => (long)i)), "Atomic handoff");
            }
        });

        await check("diagnostics: real Host and MCP use the run logger and retain safe shutdown diagnostics", async () =>
        {
            using var fixture = new RuntimeLifetimeFixture();
            ILogger? hostLogger = null;
            await using var runtime = new McpRuntime(0, services => services.AddSingleton<IHostedService>(provider =>
            {
                hostLogger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("host-probe");
                return new LogProbe(hostLogger);
            }));
            using var states = runtime.SubscribeState();
            using var logs = runtime.SubscribeLogs();
            Assert(states.Initial.Single().State == RuntimeState.Stopped && logs.Initial.IsEmpty, "Initial stopped Runtime");
            await runtime.StartAsync(fixture.Configuration);
            var first = runtime.Snapshot;
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            await using (var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(runtime.Address!, "mcp") }, http))
            await using (var mcp = await McpClient.CreateAsync(transport))
                Assert((await mcp.ListToolsAsync()).Count == 8, "Real MCP discovery still works");
            Assert(runtime.GetLogs().Entries.Any(entry => entry.Category == "host-probe" && entry.Message == "probe started"), "Host ILogger captured");
            Assert(runtime.GetLogs().Entries.Any(entry => entry.Category.StartsWith("Microsoft.AspNetCore.") && entry.Message.Contains("/mcp")), "HTTP logs captured");
            await runtime.StopAsync();
            Assert(runtime.GetLogs().Entries.Any(entry => entry.Message == "probe stopped") &&
                runtime.GetLogs().Entries.Any(entry => entry.Message == "Runtime state: Stopped."), "Shutdown logs survive Host disposal");
            Assert(!states.Reader.Completion.IsCompleted && !logs.Reader.Completion.IsCompleted, "Stopping a run keeps Runtime subscriptions alive");
            var old = hostLogger!;
            await runtime.StartAsync(fixture.Configuration);
            old.LogError("stale host event");
            Assert(runtime.Snapshot.RunId != first.RunId && !runtime.GetLogs().Entries.Any(entry => entry.Message == "stale host event"), "Cached old Host logger rejected");
            await runtime.DisposeAsync();
            var final = await states.Reader.ReadAsync();
            Assert(final.State == RuntimeState.Disposed && ReferenceEquals(final, runtime.Snapshot), "Actual final state retained");
            await states.Reader.Completion;
            Drain(logs.Reader);
            await logs.Reader.Completion;
            using var late = runtime.SubscribeState();
            Assert(late.Initial.Single().State == RuntimeState.Disposed, "Late observer sees final state");
            await late.Reader.Completion;
            using var lateLogs = runtime.SubscribeLogs();
            Assert(!lateLogs.Initial.IsEmpty, "Diagnostics remain readable after release");
            await lateLogs.Reader.Completion;
        });

        await check("diagnostics: fault snapshots and logs redact credentials before any local reader", async () =>
        {
            using var fixture = new RuntimeLifetimeFixture();
            await using var runtime = new McpRuntime(0, services => services.AddSingleton<IHostedService>(
                new FailingProbe("Authorization: Bearer private-failure")));
            using var states = runtime.SubscribeState();
            using var logs = runtime.SubscribeLogs();
            try { await runtime.StartAsync(fixture.Configuration); throw new Exception("Expected startup failure"); }
            catch (InvalidOperationException) { }
            Assert(runtime.Snapshot.State == RuntimeState.Faulted && !runtime.Snapshot.Error!.Contains("private-failure"), "Safe snapshot error");
            Assert(!(await states.Reader.ReadAsync()).Error!.Contains("private-failure"), "Safe typed event");
            Assert(runtime.GetLogs().Entries.Length > 0 && runtime.GetLogs().Entries.All(entry => !entry.Message.Contains("private-failure")), "Safe retained logs");
            Assert(Drain(logs.Reader).All(entry => !entry.Message.Contains("private-failure")), "Safe live logs");
        });

        await check("diagnostics: blocked state observers cannot hold Runtime lifecycle operations", async () =>
        {
            using var fixture = new RuntimeLifetimeFixture();
            await using var runtime = new McpRuntime(0);
            using var states = runtime.SubscribeState();
            using var release = new ManualResetEventSlim();
            var callback = states.Reader.ReadAsync().AsTask().ContinueWith(_ => release.Wait(TimeSpan.FromSeconds(8)),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try
            {
                await runtime.StartAsync(fixture.Configuration).WaitAsync(TimeSpan.FromSeconds(5));
                await runtime.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert(!release.IsSet, "Lifecycle finished while observer blocked");
            }
            finally { release.Set(); }
            await callback;
        });
    }

    private static (RuntimeDiagnostics Diagnostics, Guid RunId, ILogger Logger) Create(string category = "checks")
    {
        var diagnostics = new RuntimeDiagnostics();
        var run = Guid.NewGuid();
        diagnostics.BeginRun(new(RuntimeState.Starting, run));
        return (diagnostics, run, new RuntimeLoggerProvider(diagnostics, run).CreateLogger(category));
    }

    private static int Size(RuntimeLogEntry entry) => entry.Message.Length + entry.Category.Length;
    private static bool WellFormed(string value) => Encoding.UTF8.GetString(new UTF8Encoding(false, true).GetBytes(value)) == value;
    private static List<T> Drain<T>(ChannelReader<T> reader)
    {
        var values = new List<T>();
        while (reader.TryRead(out var value)) values.Add(value);
        return values;
    }
    private static void Assert(bool condition, string reason)
    {
        if (!condition) throw new Exception(reason);
    }

    private sealed class LogProbe(ILogger logger) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) { logger.LogInformation("probe started"); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) { logger.LogInformation("probe stopped"); return Task.CompletedTask; }
    }

    private sealed class FailingProbe(string message) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => throw new InvalidOperationException(message);
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
