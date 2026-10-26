using MiniMcp;
using MiniMcp.Diagnostics;
using Microsoft.Extensions.Logging;

internal static class ProcessOutputReaderChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("output diagnostics: fixed reads handle delimiters, partial reads and unterminated tails", async () =>
        {
            foreach (var chunkSize in new[] { 1, 7, ProcessOutputReader.ReadCharacters })
            {
                var (diagnostics, logger) = Create("stdout");
                using var reader = new FragmentedReader("first\r\n\rsecond\nthird\rfinal👋", chunkSize);
                await ProcessOutputReader.DrainAsync(reader, logger, LogLevel.Information);
                Assert(diagnostics.GetLogs().Entries.Select(entry => entry.Message).SequenceEqual(
                    ["first", "", "second", "third", "final👋"]), "Delimiters/tail at chunk size " + chunkSize);
                Assert(reader.MaxRequested <= ProcessOutputReader.ReadCharacters && !reader.WasDisposed, "Reader ownership and fixed requests");
            }
        });

        await check("output diagnostics: credentials spanning read boundaries are redacted before clipping", async () =>
        {
            foreach (var chunkSize in new[] { 1, 19, ProcessOutputReader.ReadCharacters })
            {
                var (diagnostics, logger) = Create("stdout");
                var input = new string('a', ProcessOutputReader.ReadCharacters - 3) +
                    " token=\"private-" + new string('s', 3000) + "\"\n" +
                    "Cf-Access-Jwt-Assertion: private-assertion\n" +
                    "safe👋\n";
                using var reader = new FragmentedReader(input, chunkSize);
                await ProcessOutputReader.DrainAsync(reader, logger, LogLevel.Information);
                var entries = diagnostics.GetLogs().Entries;
                Assert(entries.Length == 3 && entries.All(entry => !entry.Message.Contains("private-")), "No split credential fragment");
                Assert(entries[0].Truncated && entries[^1].Message == "safe👋", "Safe clipping and subsequent output");
            }
        });

        await check("output diagnostics: huge unbroken lines drain with one marker and recover at newline", async () =>
        {
            foreach (var tail in new[] { "", "\r\nready\n" })
            {
                var (diagnostics, logger) = Create("stdout");
                using var reader = new GeneratedReader(16 * 1024 * 1024, tail);
                await ProcessOutputReader.DrainAsync(reader, logger, LogLevel.Information).WaitAsync(TimeSpan.FromSeconds(10));
                var entries = diagnostics.GetLogs().Entries;
                Assert(reader.ReadCount == 16 * 1024 * 1024 + tail.Length && reader.MaxRequested <= 4096, "Entire source drained in bounded reads");
                Assert(entries[0].Message == "[external output line omitted: length limit exceeded]" && entries[0].EventId == 1, "One marker without fragments");
                Assert(entries.Length == (tail.Length == 0 ? 1 : 2) && (tail.Length == 0 || entries[1].Message == "ready"), "Delimiter recovers next line");
            }
        });

        await check("output diagnostics: exact line boundary is retained and longer lines are withheld", async () =>
        {
            var (diagnostics, logger) = Create("stdout");
            using var exact = new GeneratedReader(ProcessOutputReader.MaxLineCharacters, "\n");
            using var over = new GeneratedReader(ProcessOutputReader.MaxLineCharacters + 1, "\n");
            await ProcessOutputReader.DrainAsync(exact, logger, LogLevel.Information);
            await ProcessOutputReader.DrainAsync(over, logger, LogLevel.Information);
            var entries = diagnostics.GetLogs().Entries;
            Assert(entries.Length == 2 && entries[0].Message.StartsWith("ssss") && entries[0].Truncated, "Exact limit uses sanitized log clipping");
            Assert(entries[1].EventId == 1 && !entries[1].Message.Contains("ssss"), "Over limit omits entire logical line");
        });

        await check("output diagnostics: simultaneous stdout and stderr drain despite idle observers", async () =>
        {
            var (diagnostics, output) = Create("stdout");
            var error = new RuntimeLoggerProvider(diagnostics, diagnostics.Snapshot.RunId!.Value).CreateLogger("stderr");
            using var slow = diagnostics.SubscribeLogs(default);
            using var stdout = new FragmentedReader(string.Concat(Enumerable.Repeat("output\n", 4000)), 29);
            using var stderr = new FragmentedReader(string.Concat(Enumerable.Repeat("error\n", 4000)), 31);
            await Task.WhenAll(
                ProcessOutputReader.DrainAsync(stdout, output, LogLevel.Information),
                ProcessOutputReader.DrainAsync(stderr, error, LogLevel.Warning)).WaitAsync(TimeSpan.FromSeconds(10));
            var entries = diagnostics.GetLogs().Entries;
            Assert(entries[^1].Sequence == 8000 && stdout.EndReached && stderr.EndReached, "Both streams completely drained");
            var pending = new List<RuntimeLogEntry>();
            while (slow.Reader.TryRead(out var entry)) pending.Add(entry);
            Assert(pending.Count == RuntimeDiagnostics.LogSubscriptionCapacity, "Observer stays bounded");
            Assert(pending.All(entry => entry.Category == "stdout" ? entry.Level == LogLevel.Information : entry.Level == LogLevel.Warning), "Independent source and level identity");
        });

        await check("output diagnostics: cancellation and IO failures remain owned by the draining task", async () =>
        {
            var (_, logger) = Create("stdout");
            using var cancellation = new CancellationTokenSource();
            using var blocked = new BlockingReader();
            var draining = ProcessOutputReader.DrainAsync(blocked, logger, LogLevel.Information, cancellation.Token);
            await blocked.Entered.Task;
            cancellation.Cancel();
            await Reject<OperationCanceledException>(draining);
            Assert(!blocked.WasDisposed, "Caller still owns stream after cancellation");
            using var broken = new BrokenReader();
            await Reject<IOException>(ProcessOutputReader.DrainAsync(broken, logger, LogLevel.Warning));
            using var preCancelled = new CancellationTokenSource();
            preCancelled.Cancel();
            using var neverRead = new BlockingReader();
            await Reject<OperationCanceledException>(ProcessOutputReader.DrainAsync(neverRead, logger, LogLevel.Information, preCancelled.Token));
            Assert(!neverRead.Entered.Task.IsCompleted, "Pre-cancelled drain never reads");
        });
    }

    private static (RuntimeDiagnostics Diagnostics, ILogger Logger) Create(string category)
    {
        var diagnostics = new RuntimeDiagnostics();
        var run = Guid.NewGuid();
        diagnostics.BeginRun(new(RuntimeState.Running, run));
        return (diagnostics, new RuntimeLoggerProvider(diagnostics, run).CreateLogger(category));
    }

    private static void Assert(bool condition, string reason)
    {
        if (!condition) throw new Exception(reason);
    }

    private static async Task Reject<T>(Task task) where T : Exception
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private sealed class FragmentedReader(string text, int chunkSize) : TextReader
    {
        private int _position;
        public int MaxRequested { get; private set; }
        public bool WasDisposed { get; private set; }
        public bool EndReached => _position == text.Length;
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MaxRequested = Math.Max(MaxRequested, buffer.Length);
            var length = Math.Min(Math.Min(buffer.Length, chunkSize), text.Length - _position);
            text.AsMemory(_position, length).CopyTo(buffer);
            _position += length;
            return ValueTask.FromResult(length);
        }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }

    // Produces large output without first allocating that output in the fixture.
    private sealed class GeneratedReader(int characters, string tail) : TextReader
    {
        private int _remaining = characters;
        private int _tailPosition;
        public int ReadCount { get; private set; }
        public int MaxRequested { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MaxRequested = Math.Max(MaxRequested, buffer.Length);
            var length = Math.Min(buffer.Length, _remaining);
            if (length != 0) { buffer.Span[..length].Fill('s'); _remaining -= length; }
            else
            {
                length = Math.Min(buffer.Length, tail.Length - _tailPosition);
                tail.AsMemory(_tailPosition, length).CopyTo(buffer);
                _tailPosition += length;
            }
            ReadCount += length;
            return ValueTask.FromResult(length);
        }
    }

    private sealed class BlockingReader : TextReader
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasDisposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }

    private sealed class BrokenReader : TextReader
    {
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("Reader failed"));
    }
}
