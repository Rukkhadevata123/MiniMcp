using System.Text;
using Microsoft.Extensions.Logging;

namespace MiniMcp.Diagnostics;

/// <summary>
/// Drains arbitrary-length output using fixed reads and a bounded logical line. Overlong lines
/// produce one omission marker, then drain to the delimiter without publishing secret fragments.
/// The caller owns the reader, cancellation and the task, and drains stdout/stderr independently.
/// </summary>
internal static class ProcessOutputReader
{
    internal const int MaxLineCharacters = 8192;
    internal const int ReadCharacters = 4096;

    internal static async Task DrainAsync(TextReader reader, ILogger logger, LogLevel level,
        CancellationToken cancellationToken = default)
    {
        // Both streams must start even if one reader can complete endless reads synchronously.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        cancellationToken.ThrowIfCancellationRequested();
        var buffer = new char[ReadCharacters];
        var line = new StringBuilder(MaxLineCharacters);
        var discarding = false;
        var afterCarriageReturn = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var index = 0; index < count; index++)
            {
                var value = buffer[index];
                if (afterCarriageReturn && value == '\n') { afterCarriageReturn = false; continue; }
                afterCarriageReturn = value == '\r';
                if (value is '\r' or '\n')
                {
                    if (!discarding) logger.Log(level, new EventId(0), "{Output}", line.ToString());
                    line.Clear();
                    discarding = false;
                }
                else if (!discarding)
                {
                    if (line.Length == MaxLineCharacters)
                    {
                        logger.Log(level, new EventId(1), "[external output line omitted: length limit exceeded]");
                        line.Clear();
                        discarding = true;
                    }
                    else line.Append(value);
                }
            }
        }
        if (!discarding && line.Length != 0) logger.Log(level, new EventId(0), "{Output}", line.ToString());
    }
}
