using MiniMcp;
using MiniMcp.Cli;
using MiniMcp.Configuration;
using MiniMcp.Recovery;
using MiniMcp.Services.Workspace;
using static PatchExecutionFixture;

internal static class LocalRecoveryEntryChecks
{
    private static void Assert(bool condition, string message) => PatchExecutionFixture.Assert(condition, message);
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("local recovery catalog and read-only preview create no files and isolate projects", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            await using var empty = LocalRecoverySession.Open(fixture.ConfigurationFor(ExecutionMode.ReadOnly));
            Assert((await empty.ListAsync()).IsEmpty && !Directory.Exists(fixture.RecoveryDirectory), "Inspection creates no store");
            await using var writer = fixture.Execution();
            var source = (await writer.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"))).Commit!.ChangeId;
            var before = RecordFiles(fixture);
            var row = (await empty.ListAsync()).Single();
            Assert(row.ChangeId == source && row.State == "Committed" && row.PathCount == 1, "Real manifest catalog");
            var detail = await empty.ReadAsync(source);
            Assert(detail.ManifestJson.Contains("sha256", StringComparison.OrdinalIgnoreCase) && detail.Preview.Diff!.Contains("old") && detail.Preview.Blockers.Contains("ReadOnlyMode"), "Metadata and complete readonly inverse preview");
            await Reject<WorkspaceException>(() => empty.PrepareAsync(source));
            Assert(before.SequenceEqual(RecordFiles(fixture)), "Read-only catalog and preview preserve all record bytes");
            Directory.CreateDirectory(Path.Combine(fixture.RecoveryDirectory, Guid.NewGuid().ToString("N")));
            Assert((await empty.ListAsync()).Any(item => item.Error is not null), "Partial record is shown as unavailable");
        });
        foreach (var mode in new[] { ExecutionMode.ProjectEdit, ExecutionMode.FullAccess })
            await check($"local recovery {mode} binds one-time evidence, records restoration and reverses it", async () =>
            {
                using var fixture = new PatchExecutionFixture();
                await using var writer = fixture.Execution();
                var source = (await writer.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"))).Commit!.ChangeId;
                var originalRecord = await fixture.Load(source);
                await using var local = LocalRecoverySession.Open(fixture.ConfigurationFor(mode));
                await using var other = LocalRecoverySession.Open(fixture.ConfigurationFor(mode));
                var preparation = await local.PrepareAsync(source);
                await Reject<InvalidOperationException>(() => other.RestoreAsync(preparation));
                var result = await local.RestoreAsync(preparation);
                Assert(result.State == "Committed" && fixture.Get("sample.md") == "old\n" && result.ChangeId != source, "Actual restore and fresh record");
                await Reject<InvalidOperationException>(() => local.RestoreAsync(preparation));
                var undo = await local.PrepareAsync(result.ChangeId);
                await local.RestoreAsync(undo);
                Assert(fixture.Get("sample.md") == "new\n" && fixture.RecordIds().Length == 3, "Restoration is itself reversible");
                var after = await fixture.Load(source);
                Assert(RecoveryManifestJson.Serialize(originalRecord.Manifest).SequenceEqual(RecoveryManifestJson.Serialize(after.Manifest)), "Source metadata remains unchanged");
            });
        await check("local recovery CLI loads offline dependencies, prints a complete preview, cancels and confirms", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            var old = string.Join('\n', Enumerable.Range(0, 900).Select(index => $"line-{index:D4}-" + new string('x', 30))) + "\nAuthorization: Bearer local-preview-secret\nEND-OF-FULL-PREVIEW\n";
            fixture.Put("sample.md", old);
            await using var writer = fixture.Execution();
            var source = (await writer.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n" +
                string.Join('\n', old.Split('\n').SkipLast(1).Select(line => "-" + line)) + "\n+changed"))).Commit!.ChangeId;
            var store = Store(fixture, ExecutionMode.FullAccess);
            var config = await store.LoadRecoveryAsync(fixture.Configuration.SettingsFilePath, fixture.DirectoryPath);
            await using var host = new McpRuntime();
            await Reject<InvalidOperationException>(() => host.StartAsync(config));
            Assert(host.Snapshot.State == RuntimeState.Stopped && !host.Snapshot.HasOwnedResources, "Offline snapshot cannot bypass runtime dependency validation");
            var output = new StringWriter(); var error = new StringWriter();
            var listed = await Run(["records", "list"], output, error, store, fixture);
            Assert(listed == 0 && output.ToString().Contains(source.ToString("N")), "Actual list entry point");
            output.GetStringBuilder().Clear();
            Assert(await Run(["records", "show", source.ToString()], output, error, store, fixture) == 0, "Actual show entry point");
            Assert(output.ToString().Contains("END-OF-FULL-PREVIEW") && output.ToString().Contains("local-preview-secret") && output.ToString().Length > 30000, "Complete preview bypasses diagnostic redaction and clipping");
            var changed = fixture.Get("sample.md");
            Assert(await Run(["records", "restore", source.ToString()], output, error, store, fixture, new StringReader("no\n")) == 130 && fixture.RecordIds().Length == 1 && fixture.Get("sample.md") == changed, "Cancel preserves workspace and creates no record");
            Assert(await Run(["records", "restore", source.ToString()], output, error, store, fixture,
                new StringReader("RESTORE " + source.ToString("N") + "\n")) == 0, "Explicit local confirmation succeeds");
            Assert(fixture.Get("sample.md") == old && fixture.RecordIds().Length == 2, "CLI uses the real recorded restore engine");
        });
        await check("local recovery CLI rechecks external changes after confirmation and refuses ReadOnly", async () =>
        {
            using var fixture = new PatchExecutionFixture(); await using var writer = fixture.Execution();
            var source = (await writer.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"))).Commit!.ChangeId;
            var output = new StringWriter(); var error = new StringWriter();
            var readOnly = Store(fixture, ExecutionMode.ReadOnly);
            Assert(await Run(["records", "restore", source.ToString()], output, error, readOnly, fixture, new StringReader("RESTORE " + source.ToString("N"))) == 2, "ReadOnly cannot confirm writes");
            var writable = Store(fixture, ExecutionMode.ProjectEdit);
            var reader = new ChangingReader(() => fixture.Put("sample.md", "external\n"), "RESTORE " + source.ToString("N"));
            Assert(await Run(["records", "restore", source.ToString()], output, error, writable, fixture, reader) == 1, "Changed preimage rejects the whole restore");
            Assert(fixture.Get("sample.md") == "external\n" && fixture.RecordIds().Length == 1, "External contents preserved with no new record");
            Assert(await Run(["records", "restore", source.ToString(), "--yes"], output, error, writable, fixture) == 2, "No approval flag");
        });
        await check("local recovery runtime access shares execution ownership and rejects old session preparations", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            await using var runtime = fixture.Runtime(); await runtime.StartAsync(fixture.Configuration);
            var id = (await runtime.Execution!.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"))).Commit!.ChangeId;
            var access = runtime.OpenRecoverySession(); var preparation = await access.PrepareAsync(id);
            await access.DisposeAsync();
            Assert(runtime.Snapshot.State == RuntimeState.Running && !runtime.Execution.IsDisposed, "Closing a history view does not stop runtime execution");
            await Reject<ObjectDisposedException>(() => access.RestoreAsync(preparation));
            await using var active = runtime.OpenRecoverySession(); var old = await active.PrepareAsync(id);
            await runtime.StopAsync();
            try { await active.RestoreAsync(old); throw new Exception("Stopped session accepted restore"); }
            catch (Exception failure) when (failure is ObjectDisposedException or WorkspaceException) { }
            Assert(fixture.Get("sample.md") == "new\n" && fixture.RecordIds().Length == 1, "Stopped run cannot write through old evidence");
        });
    }
    private static ConfigurationStore Store(PatchExecutionFixture fixture, ExecutionMode mode)
    {
        var paths = new ConfigurationPaths(Path.Combine(fixture.DirectoryPath, "user"), Path.Combine(fixture.DirectoryPath, "state"));
        Directory.CreateDirectory(Path.GetDirectoryName(paths.PolicyFile)!);
        File.WriteAllText(fixture.Configuration.SettingsFilePath, ConfigurationJson.SerializeSettings(fixture.Configuration.Settings with
        { BashPath = Path.Combine(fixture.DirectoryPath, "missing-bash"), Tunnel = new() { Enabled = true, ExecutablePath = "missing-cloudflared", TokenFilePath = "missing-token" },
          Authentication = new() { Mode = AuthenticationMode.CloudflareAccess, PublicOrigin = "https://fixture.invalid", Team = "fixture", Audience = "fixture" } }));
        File.WriteAllText(paths.PolicyFile, ConfigurationJson.SerializePolicy(fixture.Configuration.Policy with { Mode = mode }));
        return new(paths);
    }
    private static Task<int> Run(string[] args, TextWriter output, TextWriter error, ConfigurationStore store, PatchExecutionFixture fixture, TextReader? input = null) =>
        CliApplication.RunAsync([.. args, "--config", fixture.Configuration.SettingsFilePath], output, error, CancellationToken.None, store, fixture.DirectoryPath,
            () => throw new Exception("Recovery CLI started a Host"), recoveryInput: input);
    private static string[] RecordFiles(PatchExecutionFixture fixture) => Directory.GetFiles(fixture.RecoveryDirectory, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => path + ":" + Convert.ToBase64String(File.ReadAllBytes(path))).ToArray();
    private sealed class ChangingReader(Action change, string answer) : StringReader("")
    {
        public override ValueTask<string?> ReadLineAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); change(); return ValueTask.FromResult<string?>(answer); }
    }
}
