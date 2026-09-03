using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinWingOverlay;

internal sealed class HotkeyConfig
{
    public string ToggleLock { get; set; } = "Ctrl+Alt+L";
    public string ToggleShow { get; set; } = "Ctrl+Alt+O";
    public string ToggleMinimal { get; set; } = "Ctrl+Alt+M";
}

internal sealed class OverlayConfig
{
    public int X { get; set; } = 40;
    public int Y { get; set; } = 40;
    public int Width { get; set; } = 460;
    public int Height { get; set; } = 320;

    /// <summary>Opacity of the whole overlay, 0.15 - 1.0. Scales every pixel including the gauges.</summary>
    public double Opacity { get; set; } = 0.82;

    /// <summary>
    /// Opacity of the dark background and panel fills only, 0.0 - 1.0. Outlines, grid lines,
    /// text and live values stay solid, so 0 leaves a readable wireframe over the game.
    /// </summary>
    public double BackgroundOpacity { get; set; } = 1.0;

    /// <summary>When locked the window is click-through and cannot be moved or resized.</summary>
    public bool Locked { get; set; } = true;

    /// <summary>Vendor / product id of the device to display. 0 means "first joystick found".</summary>
    public int VendorId { get; set; } = 0x4098;   // WINWING
    public int ProductId { get; set; } = 0;       // any WINWING device

    /// <summary>Buttons drawn in the grid. 0 auto-sizes to the highest button the device declares.</summary>
    public int ButtonCount { get; set; } = 0;

    public bool ShowButtons { get; set; } = true;
    public bool ShowAxisReadouts { get; set; } = true;

    /// <summary>Repaint ceiling in frames per second. The window only repaints when values change.</summary>
    public int MaxFps { get; set; } = 60;

    /// <summary>Optional friendly labels, keyed by button number as a string, e.g. "1": "Trigger".</summary>
    public Dictionary<string, string> ButtonLabels { get; set; } = new();

    /// <summary>
    /// Left-to-right order of the gauges. Tokens: XY, RXRY, HAT, and any axis name
    /// (Z, RZ, Slider, Dial, Wheel) which draws as a vertical bar. Anything the device
    /// does not report is skipped; bar axes the device has but this list omits are
    /// appended on the right so nothing disappears silently.
    /// </summary>
    public List<string> GaugeOrder { get; set; } = new() { "XY", "Z", "Slider", "RXRY", "HAT" };

    /// <summary>
    /// Axes drawn upside down, by name (X, Y, Z, RX, RY, Slider...). Use this to make the
    /// overlay match an axis you have inverted in game.
    /// </summary>
    public List<string> InvertAxes { get; set; } = new() { "Slider" };

    /// <summary>
    /// What the minimal view hides. Tokens: Title (the top row), Labels (the small gauge
    /// captions and percentages), Buttons, XY, RXRY, HAT, or an axis name.
    /// </summary>
    public List<string> MinimalHides { get; set; } = new() { "Buttons", "HAT", "RXRY", "Title" };

    /// <summary>
    /// Global hotkeys. If a combination is already owned by another program the overlay
    /// falls back to a free one and says so in the tray menu.
    /// </summary>
    public HotkeyConfig Hotkeys { get; set; } = new();

    /// <summary>Start in minimal view.</summary>
    public bool Minimal { get; set; }

    [JsonIgnore]
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WinWingOverlay", "config.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static OverlayConfig Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<OverlayConfig>(File.ReadAllText(Path), Options) ?? new OverlayConfig();
        }
        catch
        {
            // A corrupt config should never stop the overlay from starting.
        }
        return new OverlayConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // Losing window position is not worth crashing over.
        }
    }
}
