using MiniMcp.Configuration;

namespace MiniMcp.Runtime;

public enum RuntimeState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Faulted,
    Disposed
}

/// <summary>
/// A coherent view of the current or most recent startup attempt. Configuration is absent before
/// the first configured start or when using the temporary port-only prototype API.
/// A fault may retain host ownership and its last bound address until cleanup succeeds.
/// </summary>
public sealed record RuntimeSnapshot(RuntimeState State, Guid? RunId = null,
    RuntimeConfiguration? Configuration = null, Uri? Address = null, string? Error = null);
