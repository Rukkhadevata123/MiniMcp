using System.Runtime.InteropServices;
using System.Text;
using MiniMcp.Cli;
using MiniMcp.Configuration;

internal static class CliRecoveryTerminalChecks
{
    // Explicit interactive acceptance entry. It uses real console handles and CliEntry's real
    // signal/input path while retaining the same temporary store isolation as other checks.
    internal static async Task<int> RunAsync(string expected)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.WriteLine($"Terminal handles: input redirected={Console.IsInputRedirected}, output redirected={Console.IsOutputRedirected}");
        using var fixture = new PatchExecutionFixture(ExecutionMode.ProjectEdit);
        await using var execution = fixture.Execution();
        var id = (await execution.ApplyPatchAsync(PatchExecutionFixture.Patch("*** Update File: sample.md\n@@\n-old\n+new"))).Commit!.ChangeId;
        var paths = new ConfigurationPaths(Path.Combine(fixture.DirectoryPath, "user"), Path.Combine(fixture.DirectoryPath, "state"));
        Directory.CreateDirectory(Path.GetDirectoryName(paths.PolicyFile)!);
        File.WriteAllText(fixture.Configuration.SettingsFilePath, ConfigurationJson.SerializeSettings(fixture.Configuration.Settings));
        File.WriteAllText(paths.PolicyFile, ConfigurationJson.SerializePolicy(fixture.Configuration.Policy));
        using var watchdog = new CancellationTokenSource();
        var unblocked = false;
        var rescue = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(45), watchdog.Token);
                unblocked = true;
                var enter = new[] { new ConsoleKey { EventType = 1, Down = 1, Repeat = 1, VirtualKey = 13, Character = '\r' } };
                WriteConsoleInput(GetStdHandle(-10), enter, 1, out _);
            }
            catch (OperationCanceledException) when (watchdog.IsCancellationRequested) { }
        });
        int result;
        try { result = await CliEntry.RunAsync(["records", "restore", id.ToString("N"), "--config", fixture.Configuration.SettingsFilePath],
            Console.Out, Console.Error, new ConfigurationStore(paths), fixture.DirectoryPath); }
        finally { await watchdog.CancelAsync(); await rescue; }
        var restored = expected == "restore";
        if (unblocked || result != (restored ? 0 : 130) || fixture.Get("sample.md") != (restored ? "old\n" : "new\n") || fixture.RecordIds().Length != (restored ? 2 : 1))
            throw new Exception($"Interactive recovery {expected} failed: exit={result}, watchdog={unblocked}");
        Console.WriteLine("PASS actual terminal recovery " + expected);
        return 0;
    }
    [StructLayout(LayoutKind.Explicit, Size = 20, CharSet = CharSet.Unicode)]
    private struct ConsoleKey
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public int Down;
        [FieldOffset(8)] public ushort Repeat;
        [FieldOffset(10)] public ushort VirtualKey;
        [FieldOffset(14)] public char Character;
    }
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int kind);
    [DllImport("kernel32.dll", EntryPoint = "WriteConsoleInputW")] private static extern bool WriteConsoleInput(IntPtr handle, ConsoleKey[] events, uint count, out uint written);
}
