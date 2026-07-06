namespace Psv.App;

/// <summary>
/// Persisted window geometry and appearance preferences (plan §3.4/§3.5). Window position/size
/// are the *pre-maximize* "restore bounds" — <see cref="WindowMaximized"/> is tracked separately
/// so un-maximizing later returns to a sensible size instead of the maximized one.
/// </summary>
public sealed class AppSettings
{
    public double WindowX { get; set; } = double.NaN;
    public double WindowY { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 900;
    public double WindowHeight { get; set; } = 600;
    public bool WindowMaximized { get; set; }

    public bool ShowLineNumbers { get; set; } = true;
    public bool ShowColumnRuler { get; set; } = true;
    public bool WordWrap { get; set; }
    public bool ZebraStriping { get; set; } = true;
    public bool FollowSystemTheme { get; set; } = true;
    public bool TailingEnabled { get; set; }

    public string FontFamily { get; set; } = "monospace";
    public double FontSize { get; set; } = 14;
    public string TextColor { get; set; } = "#FF000000";
    public string ZebraEvenColor { get; set; } = "#FFFFFFFF";
    public string ZebraOddColor { get; set; } = "#FFF0F0F0";
}
