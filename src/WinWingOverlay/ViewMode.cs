namespace WinWingOverlay;

/// <summary>The three overlay layouts, cycled with the mode hotkey or the radial dials.</summary>
internal enum ViewMode
{
    /// <summary>Everything: gauges, yaw bar and the button grid.</summary>
    Full,

    /// <summary>The gauges that survive <c>minimalHides</c>, cropped to fit.</summary>
    Minimal,

    /// <summary>A single axis as a large number, for reading collective at a glance.</summary>
    Collective
}

/// <summary>What a clickable region in the overlay does.</summary>
internal enum HitKind
{
    Button,
    Slider
}

/// <summary>
/// A clickable region produced while drawing. The renderer owns layout, so it is also the
/// thing that knows where the controls ended up; the form just looks them up on a click.
/// </summary>
internal readonly record struct HitRegion(string Id, RectangleF Rect, HitKind Kind);
