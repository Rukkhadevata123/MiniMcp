using System.Diagnostics;
using System.Text;

// Lightweight common runner for the standalone checks executables; no test framework dependency.
internal sealed class CheckRunner
{
    private readonly string _suite;
    private readonly string? _group;
    private readonly string? _id;
    private readonly string? _filter;
    private readonly string? _tier;
    private readonly bool _list;
    private readonly HashSet<string> _registered = new(StringComparer.Ordinal);
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private static readonly AsyncLocal<List<(string Label, Exception Error)>?> CleanupFailures = new();
    private static readonly AsyncLocal<string?> ActiveScenario = new();

    public int Passed { get; private set; }
    public int Failed { get; private set; }
    public int Skipped { get; private set; }
    public int Selected { get; private set; }
    public bool Listing => _list;

    public CheckRunner(string suite, string[] args, TextWriter? output = null, TextWriter? error = null)
    {
        _suite = suite;
        _output = output ?? Console.Out;
        _error = error ?? Console.Error;
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (option == "--list" && !_list)
            { _list = true; continue; }
            if (option is not ("--filter" or "--group" or "--id" or "--tier") || index + 1 >= args.Length ||
                string.IsNullOrWhiteSpace(args[index + 1]))
                throw new ArgumentException($"Unknown or incomplete checks argument: {option}. Expected --list, --group NAME, --id ID, --filter TEXT or --tier fast|integration|slow|ui.");
            var value = args[++index];
            switch (option)
            {
                case "--filter" when _filter is null:
                    _filter = value;
                    break;
                case "--group" when _group is null:
                    _group = value;
                    break;
                case "--id" when _id is null:
                    _id = value;
                    break;
                case "--tier" when _tier is null && value is ("fast" or "integration" or "slow" or "ui"):
                    _tier = value;
                    break;
                default:
                    throw new ArgumentException($"Duplicate checks argument: {option}");
            }
        }
    }

    public Func<string, Func<Task>, Task> InGroup(string group) =>
        (name, action) => CheckAsync(group, name, action);

    public async Task CheckAsync(string group, string name, Func<Task> action)
    {
        var id = $"{_suite}/{group}/{Slug(name)}";
        var tier = TierFor(_suite, group, name);
        if (!_registered.Add(id))
            throw new InvalidOperationException($"Duplicate check ID: {id}");
        if (!IsSelected(id, group, name, tier))
            return;

        Selected++;
        if (_list)
        {
            _output.WriteLine($"{id}\t{name}\t{tier}");
            return;
        }

        var watch = Stopwatch.StartNew();
        Exception? failure = null;
        CheckSkippedException? skipped = null;
        string? failedScenario = null;
        var cleanup = new List<(string Label, Exception Error)>();
        var previous = CleanupFailures.Value;
        var previousScenario = ActiveScenario.Value;
        CleanupFailures.Value = cleanup;
        ActiveScenario.Value = null;
        try
        { await action(); }
        catch (CheckSkippedException reason) { skipped = reason; }
        catch (Exception error) { failure = error; failedScenario = ActiveScenario.Value; }
        finally { CleanupFailures.Value = previous; ActiveScenario.Value = previousScenario; }

        var elapsed = $"{watch.Elapsed.TotalMilliseconds:F0} ms";
        if (failure is not null || cleanup.Count != 0)
            ReportFailure(id, elapsed, failure, failedScenario, cleanup);
        else if (skipped is not null)
            ReportSkipped(id, elapsed, skipped.Message);
        else
            ReportPassed(id, elapsed);
    }

    // --group and --id are exact (case-insensitive); --tier matches the assigned category;
    // --filter is a substring of the name or the ID. All given selectors must match.
    private bool IsSelected(string id, string group, string name, string tier)
    {
        if (_group is not null && !group.Equals(_group, StringComparison.OrdinalIgnoreCase))
            return false;
        if (_id is not null && !id.Equals(_id, StringComparison.OrdinalIgnoreCase))
            return false;
        if (_tier is not null && !_tier.Equals(tier, StringComparison.OrdinalIgnoreCase))
            return false;
        if (_filter is not null && !name.Contains(_filter, StringComparison.OrdinalIgnoreCase) && !id.Contains(_filter, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    // A cleanup failure fails the check even when its body passed, and is always listed separately
    // so the primary failure (if any) stays readable.
    private void ReportFailure(string id, string elapsed, Exception? failure, string? scenario, List<(string Label, Exception Error)> cleanup)
    {
        Failed++;
        if (failure is not null)
            _output.WriteLine($"FAIL {id} ({elapsed}){(scenario is null ? "" : " [" + scenario + "]")}: {failure}");
        else
            _output.WriteLine($"FAIL {id} ({elapsed}): cleanup failed");
        foreach (var (label, error) in cleanup)
            _output.WriteLine($"FAIL CLEANUP {id} [{label}]: {error}");
    }

    private void ReportSkipped(string id, string elapsed, string reason)
    {
        Skipped++;
        _output.WriteLine($"SKIP {id} ({elapsed}): {reason}");
    }

    private void ReportPassed(string id, string elapsed)
    {
        Passed++;
        _output.WriteLine($"PASS {id} ({elapsed})");
    }

    // A fixture can finish cleanup without replacing an earlier assertion failure.
    // When outside a check, disposal errors still propagate normally.
    public static void Cleanup(string label, Action cleanup)
    {
        try
        { cleanup(); }
        catch (Exception error)
        {
            if (CleanupFailures.Value is not { } errors)
                throw;
            errors.Add((label, error));
        }
    }

    // Retry only Windows sharing/lock violations; all other cleanup failures remain fatal.
    // A persistent sharing violation is reported after three bounded retries.
    public static void DeleteTemporaryDirectory(string directory)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (!Directory.Exists(directory))
                return;
            try
            { Directory.Delete(directory, recursive: true); return; }
            catch (IOException error) when (OperatingSystem.IsWindows() &&
                (error.HResult == unchecked((int)0x80070020) || error.HResult == unchecked((int)0x80070021)) &&
                attempt < 3)
            {
                Console.WriteLine($"CLEANUP RETRY {directory} (sharing violation, attempt {attempt + 1}/3): {error.Message}");
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }

    public static void Skip(string reason) => throw new CheckSkippedException(reason);
    public static void Scenario(string dimensions) => ActiveScenario.Value = dimensions;

    public int Finish()
    {
        _output.WriteLine($"\n{Passed} passed, {Failed} failed, {Skipped} skipped, {Selected} selected ({_elapsed.Elapsed.TotalSeconds:F2} s).");
        if (Selected == 0)
            _error.WriteLine("No checks matched the requested selection.");
        return Failed == 0 && Selected != 0 ? 0 : 1;
    }

    private static string Slug(string name)
    {
        var result = new StringBuilder();
        var pendingDash = false;
        foreach (var character in name)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                if (pendingDash && result.Length != 0)
                    result.Append('-');
                result.Append(char.ToLowerInvariant(character));
                pendingDash = false;
            }
            else
                pendingDash = true;
        }
        return result.ToString();
    }

    // Category ownership is explicit and centralized, so --tier doesn't rely on
    // elapsed times, brittle text matching of "10 seconds", or incidental order.
    // All file/HTTP/process/storage checks default to integration.
    private static string TierFor(string suite, string group, string name)
    {
        if (suite == "protocol")
            return "fast"; // In-memory SDK transport.
        if (suite == "core" && group is "patch.plan" or "patch.diff" or "patch.risk" or "patch.write-policy"
            or "recovery.draft" or "recovery.json" or "recovery.validation")
            return "fast";
        if (suite == "runtime" && group is "infrastructure.runner" or "configuration.json"
            or "configuration.patch-limits" or "configuration.serialization" or "configuration.values")
            return "fast";
        if (suite == "runtime" && (
            group == "recovery.patch-fault" && (name.StartsWith("recorded fault matrix", StringComparison.Ordinal) ||
                name.StartsWith("final metadata fault matrix", StringComparison.Ordinal)) ||
            group == "recovery.restore-fault" && (name.StartsWith("restore representative installation failures", StringComparison.Ordinal) ||
                name.StartsWith("restore final-manifest pre-", StringComparison.Ordinal)) ||
            group == "host.calls" && name.StartsWith("shutdown deadline bounds", StringComparison.Ordinal) ||
            group == "cli.run" && (name.StartsWith("CLI run: one shutdown budget", StringComparison.Ordinal) ||
                name.StartsWith("CLI run: startup ignoring cancellation", StringComparison.Ordinal))))
            return "slow";
        return "integration";
    }

    private sealed class CheckSkippedException(string reason) : Exception(reason);
}
