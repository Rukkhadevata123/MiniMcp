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
/// A coherent view of the current or most recent startup attempt. Configuration is absent
/// before the first start and is retained with the last run ID after stopping.
/// A fault may retain host ownership and its last bound address until cleanup succeeds.
/// </summary>
public sealed record RuntimeSnapshot(RuntimeState State, Guid? RunId = null,
    RuntimeConfiguration? Configuration = null, Uri? Address = null, string? Error = null);
