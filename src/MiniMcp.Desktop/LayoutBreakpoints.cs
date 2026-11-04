namespace MiniMcp.Desktop;

/// <summary>
/// Width thresholds (DIPs) at which pages switch layouts, listed from narrowest to widest so the
/// whole responsive ladder is visible in one place. Each page reacts to its own content width.
/// </summary>
internal static class LayoutBreakpoints
{
    /// <summary>Overview: tunnel start/stop buttons sit side by side.</summary>
    public const double TunnelActionsInline = 480;

    /// <summary>Settings: configuration-source buttons sit side by side.</summary>
    public const double SourceActionsInline = 560;

    /// <summary>Overview: service start/stop/restart buttons sit side by side.</summary>
    public const double ServiceActionsInline = 640;

    /// <summary>Logs: search and level filters share one row; workspace gutter grows to medium.</summary>
    public const double FiltersInline = 720;

    /// <summary>Recovery: record list and detail pane become two columns.</summary>
    public const double RecoveryTwoColumns = 860;

    /// <summary>Workspace gutter grows to its widest.</summary>
    public const double WideWorkspace = 1000;

    /// <summary>Preferred initial window size before clamping to the work area.</summary>
    public const double InitialWindowWidth = 1040;
    public const double InitialWindowHeight = 780;
}
