internal static class CheckRunnerChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("runner lists stable IDs without executing selected actions", async () =>
        {
            using var output = new StringWriter();
            var runner = new CheckRunner("probe", ["--list", "--group", "logic"], output);
            var invoked = false;
            await runner.CheckAsync("logic", "Example case!", () => { invoked = true; return Task.CompletedTask; });
            await runner.CheckAsync("other", "Another case", () => throw new Exception("Must not run"));
            Assert(!invoked && runner.Passed == 0 && runner.Failed == 0 && runner.Selected == 1 &&
                output.ToString().Contains("probe/logic/example-case\tExample case!", StringComparison.Ordinal) &&
                runner.Finish() == 0, "Listing reports stable IDs and does not execute actions");
        });
        await check("runner rejects unmatched and malformed selections", async () =>
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var runner = new CheckRunner("probe", ["--id", "probe/logic/absent"], output, error);
            await runner.CheckAsync("logic", "Present case", () => throw new Exception("Must not run"));
            Assert(runner.Finish() == 1 && runner.Selected == 0 &&
                error.ToString().Contains("No checks matched", StringComparison.Ordinal), "Zero selected is not success");
            try { _ = new CheckRunner("probe", ["--group"]); }
            catch (ArgumentException) { return; }
            throw new Exception("Incomplete arguments were accepted");
        });
        await check("runner counts skips separately from passes", async () =>
        {
            using var output = new StringWriter();
            var runner = new CheckRunner("probe", [], output);
            await runner.CheckAsync("platform", "Unavailable case", () =>
            {
                CheckRunner.Skip("Missing platform facility");
                return Task.CompletedTask;
            });
            Assert(runner.Skipped == 1 && runner.Passed == 0 && runner.Failed == 0 &&
                runner.Finish() == 0 && output.ToString().Contains("SKIP probe/platform/unavailable-case", StringComparison.Ordinal),
                "Skip is visible, nonfailing, and never counted as pass");
        });
        await check("runner reports original assertion and cleanup error with separate stacks and scenario", async () =>
        {
            using var output = new StringWriter();
            var runner = new CheckRunner("probe", [], output);
            await runner.CheckAsync("cleanup", "Both failures", () =>
            {
                CheckRunner.Scenario("stage=Installed, index=2");
                CheckRunner.Cleanup("test directory", () => throw new IOException("Injected cleanup failure"));
                throw new InvalidOperationException("Injected primary failure");
            });
            var log = output.ToString();
            Assert(runner.Failed == 1 && runner.Passed == 0 && runner.Finish() == 1 &&
                log.Contains("stage=Installed, index=2", StringComparison.Ordinal) &&
                log.Contains("InvalidOperationException: Injected primary failure", StringComparison.Ordinal) &&
                log.Contains("FAIL CLEANUP", StringComparison.Ordinal) &&
                log.Contains("IOException: Injected cleanup failure", StringComparison.Ordinal),
                "Original error and cleanup error are both retained");
        });
        await check("runner tier selection lists the owned category without executing other tiers", async () =>
        {
            using var output = new StringWriter();
            var runner = new CheckRunner("runtime", ["--tier", "slow", "--list"], output);
            await runner.CheckAsync("host.calls", "shutdown deadline bounds blocking host stop and retries share its unfinished task",
                () => throw new Exception("Listing must not execute."));
            await runner.CheckAsync("configuration.json", "Strict JSON rejects duplicate keys",
                () => throw new Exception("Fast case must not execute."));
            Assert(runner.Selected == 1 && runner.Finish() == 0 &&
                output.ToString().Contains("\tslow", StringComparison.Ordinal) &&
                !output.ToString().Contains("duplicate", StringComparison.OrdinalIgnoreCase),
                "Only selected tier appears in list mode");
            try { _ = new CheckRunner("runtime", ["--tier", "everything"]); }
            catch (ArgumentException) { return; }
            throw new Exception("Unknown tier was accepted");
        });
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
