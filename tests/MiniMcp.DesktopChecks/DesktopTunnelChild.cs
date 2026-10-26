using System.Text;
using System.Text.Json;

namespace MiniMcp.DesktopChecks;

internal static class DesktopTunnelChild
{
    internal static async Task<int> Run(string file)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var scenario = JsonSerializer.Deserialize<DesktopTunnelScenario>(await File.ReadAllTextAsync(file))!;
        var marker = Path.Combine(scenario.Directory, "tunnel.pid");
        var temporary = marker + "." + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.Move(temporary, marker, true);
        Console.WriteLine("Desktop tunnel fixture running");
        Console.Error.WriteLine("Authorization: Bearer desktop-tunnel-secret");
        var request = Path.Combine(scenario.Directory, "tunnel-exit.request");
        while (!File.Exists(request)) await Task.Delay(20);
        File.Delete(request);
        return 23;
    }
}
internal sealed record DesktopTunnelScenario(string Directory);
