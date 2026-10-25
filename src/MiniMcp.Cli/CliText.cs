using System.Text;
using MiniMcp.Runtime;

namespace MiniMcp.Cli;

internal static class CliText
{
    internal static string Visible(string value)
    {
        value = RuntimeDiagnosticText.Sanitize(value);
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
            if (char.IsControl(character)) result.Append($"\\u{(int)character:x4}");
            else result.Append(character);
        return result.ToString();
    }
}
