using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace WinWingOverlay;

/// <summary>
/// Draws the whole overlay with GDI+.
///
/// Layout is always computed from a <em>basis</em> size — the full-view window size — rather
/// than from the current client rectangle. In full view the two are the same. In minimal view
/// the window is trimmed to just the visible gauges, and because the basis has not changed,
/// every remaining gauge and label keeps exactly the size and position it had in full view.
/// Minimal is a crop, not a rescale.
/// </summary>
internal sealed partial class OverlayRenderer : IDisposable
{
    private enum GaugeKind { StickXY, StickRXRY, Hat, Bar }

    private readonly record struct GaugeItem(GaugeKind Kind, ushort Usage, string Label, float Factor);

    private readonly record struct GaugeMetrics(float Side, float BarW, float Gap);

    /// <summary>Everything the layout depends on, derived once from the basis size.</summary>
    private readonly record struct Layout(float Pad, float TitleH, RectangleF Body, float GaugeH, bool ButtonsInFullView);

    private static readonly ushort[] BarUsages =
    {
        Native.USAGE_Z, Native.USAGE_RZ, Native.USAGE_SLIDER, Native.USAGE_DIAL, Native.USAGE_WHEEL
    };

    private static readonly Color Background = Color.FromArgb(14, 17, 22);
    private static readonly Color Panel = Color.FromArgb(24, 29, 36);
    private static readonly Color Edge = Color.FromArgb(58, 68, 80);
    private static readonly Color Text = Color.FromArgb(196, 208, 218);
    private static readonly Color TextDim = Color.FromArgb(120, 132, 144);
    private static readonly Color Accent = Color.FromArgb(64, 200, 232);
    private static readonly Color AccentSoft = Color.FromArgb(40, 64, 200, 232);

    // Only these two carry the configurable background alpha. Outlines, grid lines, text and
    // every live value stay fully opaque, so the readout is legible at any background setting.
    private SolidBrush _bg = new(Background);
    private SolidBrush _panel = new(Panel);
    private int _bgAlpha = 255;

    private readonly SolidBrush _text = new(Text);
    private readonly SolidBrush _textDim = new(TextDim);
    private readonly SolidBrush _accent = new(Accent);
    private readonly SolidBrush _accentSoft = new(AccentSoft);
    private readonly Pen _edge = new(Edge, 1f);
    private readonly Pen _accentPen = new(Accent, 1.6f);
    private readonly Pen _gridPen = new(Color.FromArgb(42, 50, 60), 1f);

    // Font sizes are in PIXELS, not points. The window is sized and laid out in pixels, so
    // point-sized fonts would change height with the monitor DPI and pull the layout — and the
    // measured minimal-view crop — out of step with it.
    private const float TinyPx = 8.7f;
    private const float SmallPx = 10f;
    private const float TitlePx = 11.3f;
    private const float MinFontPx = 6f;

    private Font _fontSmall = NewFont("Segoe UI", SmallPx);
    private Font _fontTiny = NewFont("Segoe UI", TinyPx);
    private Font _fontTitle = NewFont("Segoe UI Semibold", TitlePx);
    private float _fontScale = -1f;

    private static Font NewFont(string family, float pixels) =>
        new(family, Math.Max(MinFontPx, pixels), FontStyle.Regular, GraphicsUnit.Pixel);

    private Font? _cellFont;
    private Font? _barFont;
    private string _widestLabel = "";
    private Dictionary<string, string>? _labelSource;
    private int _labelCount = -1;

    private HashSet<string>? _invert;
    private List<string>? _invertSource;
    private HashSet<string>? _bottom;
    private List<string>? _bottomSource;
    private HashSet<string>? _centreOrigin;
    private List<string>? _centreOriginSource;

    private readonly StringFormat _centre = new()
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
        FormatFlags = StringFormatFlags.NoWrap
    };

    /// <summary>Typographic centring: no side bearings and no clipping to the layout rectangle.</summary>
    private readonly StringFormat _centreTight = new(StringFormat.GenericTypographic)
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip
    };

    private readonly StringFormat _leftTight = new(StringFormat.GenericTypographic)
    {
        Alignment = StringAlignment.Near,
        LineAlignment = StringAlignment.Center,
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip
    };

    private readonly StringFormat _rightTight = new(StringFormat.GenericTypographic)
    {
        Alignment = StringAlignment.Far,
        LineAlignment = StringAlignment.Center,
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip
    };

    // ---- Public surface -------------------------------------------------

    public void Render(Graphics g, Rectangle client, JoystickDevice? device, OverlayConfig config,
        bool locked, ViewMode mode, string? lockHotkey = null, string? modeHotkey = null,
        Size basis = default, double backgroundAlpha = 1.0, bool menuOpen = false)
    {
        if (basis.Width <= 0 || basis.Height <= 0) basis = client.Size;

        bool minimal = mode == ViewMode.Minimal;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        // Grayscale AA, not ClearType: subpixel rendering needs an opaque backdrop and would
        // fringe badly over a translucent background on a layered window.
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;

        EnsureFonts(basis.Height);
        EnsureBackgroundAlpha(backgroundAlpha);
        Hits.Clear();

        g.Clear(Color.Transparent);

        if (menuOpen)
        {
            DrawMenuPage(g, client, config, basis, locked);
            DrawDials(g, client, mode, menuOpen: true, basis);
            return;
        }

        bool dials = !locked && config.ShowDials;

        if (mode == ViewMode.Collective)
        {
            DrawCollective(g, client, device, config, basis, locked, backgroundAlpha,
                dials ? DialStripHeight(basis) : 0f);
            if (dials) DrawDials(g, client, mode, menuOpen: false, basis);
            if (!locked) DrawChromeToggle(g, client, config, basis);
            return;
        }

        g.FillRectangle(_bg, client);
        using (var border = new Pen(locked ? Edge : Accent, locked ? 1f : 2f))
            g.DrawRectangle(border, 0, 0, client.Width - 1, client.Height - 1);

        var layout = ComputeLayout(basis, device, config);

        // Dropping the title row resizes nothing: the gauges keep their measured size and
        // simply move up into the space it occupied, and the window is trimmed to match.
        float shift = HidesTitle(config, minimal) ? -layout.TitleH : 0f;

        if (shift == 0f)
        {
            // The title bar is the one thing that follows the real window width, so the
            // right-aligned hint stays visible after a minimal-view trim.
            DrawTitle(g, new RectangleF(layout.Pad, layout.Pad * 0.5f, client.Width - layout.Pad * 2, layout.TitleH),
                device, locked, mode, lockHotkey, modeHotkey);
        }

        if (device is null)
        {
            var msg = new RectangleF(layout.Pad, layout.Body.Y + shift, client.Width - layout.Pad * 2,
                Math.Max(20f, client.Height - layout.Body.Y - layout.Pad));
            g.DrawString("No joystick detected — plug the stick in, or run with --diag",
                _fontSmall, _textDim, msg, _centre);
            if (dials) DrawDials(g, client, mode, menuOpen: false, basis);
            if (!locked) DrawChromeToggle(g, client, config, basis);
            return;
        }

        var all = BuildItems(device, config, minimal: false);
        var visible = minimal ? BuildItems(device, config, minimal: true) : all;

        var gaugeArea = new RectangleF(layout.Body.X, layout.Body.Y + shift, layout.Body.Width, layout.GaugeH);
        var metrics = ComputeMetrics(gaugeArea, all);

        bool labels = !HidesLabels(config, minimal);

        if (visible.Count > 0)
            DrawGauges(g, gaugeArea, device, config, visible, metrics, labels);

        float rowBottom = gaugeArea.Y + metrics.Side;
        var bottomBars = BuildBottomBars(device, config, minimal);

        if (bottomBars.Count > 0)
        {
            float rowWidth = RowWidth(visible, metrics);
            if (rowWidth < 8f) rowWidth = gaugeArea.Width;

            float thickness = BottomBarThickness(metrics);
            float y = rowBottom + metrics.Gap;

            foreach (var item in bottomBars)
            {
                bool centre = CentreOriginSet(config).Contains(item.Label);
                DrawHBar(g, new RectangleF(gaugeArea.X, y, rowWidth, thickness),
                    labels ? item.Label : null,
                    Value(device.State, item.Usage, centre ? 0.5 : 0.0, config),
                    labels && config.ShowAxisReadouts, centre, _fontTiny);
                y += thickness + metrics.Gap;
            }

            rowBottom = y - metrics.Gap;
        }

        if (layout.ButtonsInFullView && !Hides(config, minimal, "Buttons"))
        {
            float top = Math.Max(layout.Body.Y + shift + layout.GaugeH + layout.Pad * 0.4f,
                rowBottom + layout.Pad * 0.4f);
            var grid = new RectangleF(layout.Body.X, top, layout.Body.Width,
                layout.Body.Y + shift + layout.Body.Height - top);
            if (grid.Height > 8)
                DrawButtons(g, grid, device.State, ResolveButtonCount(device, config), config);
        }

        if (dials) DrawDials(g, client, mode, menuOpen: false, basis);
        if (!locked) DrawChromeToggle(g, client, config, basis);
    }

    /// <summary>
    /// The client size minimal view needs: the same layout as the full view, cropped to the
    /// gauges that survive <c>minimalHides</c>. Nothing is scaled down.
    /// </summary>
    public Size MeasureMinimal(Size basis, JoystickDevice? device, OverlayConfig config)
    {
        if (basis.Width <= 0 || basis.Height <= 0) return basis;

        EnsureFonts(basis.Height);
        var layout = ComputeLayout(basis, device, config);

        if (device is null) return basis;

        var all = BuildItems(device, config, minimal: false);
        var visible = BuildItems(device, config, minimal: true);
        if (visible.Count == 0) return basis;

        var gaugeArea = new RectangleF(layout.Body.X, layout.Body.Y, layout.Body.Width, layout.GaugeH);
        var m = ComputeMetrics(gaugeArea, all);

        float width = RowWidth(visible, m);

        float shift = HidesTitle(config, minimal: true) ? -layout.TitleH : 0f;

        int bars = BuildBottomBars(device, config, minimal: true).Count;
        float bottomHeight = bars * (BottomBarThickness(m) + m.Gap);

        // Keep the full height whenever the button grid survives into minimal view.
        bool buttonsStay = layout.ButtonsInFullView && !Hides(config, minimal: true, "Buttons");

        return new Size(
            (int)Math.Ceiling(width + layout.Pad * 2),
            buttonsStay
                ? (int)Math.Ceiling(basis.Height + shift)
                : (int)Math.Ceiling(layout.Body.Y + shift + m.Side + bottomHeight + layout.Pad * 0.75f));
    }

    // ---- Layout ---------------------------------------------------------

    private Layout ComputeLayout(Size basis, JoystickDevice? device, OverlayConfig config)
    {
        float pad = Math.Max(6f, basis.Height * 0.025f);
        float titleH = _fontTitle.Height + 4f;

        var body = new RectangleF(pad, pad * 0.5f + titleH, basis.Width - pad * 2,
            basis.Height - titleH - pad * 1.5f);

        // Whether the gauges share the window with a button grid is a property of the FULL
        // view. Hiding the grid in minimal view must not make the gauges grow.
        bool buttons = device is not null && config.ShowButtons && ResolveButtonCount(device, config) > 0;
        float gaugeH = buttons ? body.Height * 0.60f : body.Height;

        return new Layout(pad, titleH, body, gaugeH, buttons);
    }

    private static GaugeMetrics ComputeMetrics(RectangleF area, List<GaugeItem> allItems)
    {
        const float minBar = 12f, maxBar = 46f, targetBar = 34f;

        float gap = Math.Max(4f, area.Height * 0.04f);
        float gaps = gap * Math.Max(0, allItems.Count - 1);
        int barCount = allItems.Count(i => i.Kind == GaugeKind.Bar);
        float factorSum = allItems.Where(i => i.Kind != GaugeKind.Bar).Sum(i => i.Factor);

        float side = Math.Min(area.Height, area.Width * 0.42f);
        if (factorSum > 0)
        {
            float allowance = area.Width - gaps - barCount * targetBar;
            if (side * factorSum > allowance) side = Math.Max(allowance / factorSum, 14f);
        }

        float barW = barCount > 0
            ? Math.Clamp((area.Width - (side * factorSum + gaps)) / barCount, minBar, maxBar)
            : 0f;

        return new GaugeMetrics(side, barW, gap);
    }

    /// <summary>The top row: device name plus the lock/minimal hint.</summary>
    private static bool HidesTitle(OverlayConfig config, bool minimal) =>
        Hides(config, minimal, "Title") || Hides(config, minimal, "Text");

    /// <summary>The small captions inside each gauge and the percentage readouts.</summary>
    private static bool HidesLabels(OverlayConfig config, bool minimal) =>
        Hides(config, minimal, "Labels");

    private static bool Hides(OverlayConfig config, bool minimal, string token) =>
        minimal && config.MinimalHides is { Count: > 0 } &&
        config.MinimalHides.Any(h => string.Equals(h?.Trim(), token, StringComparison.OrdinalIgnoreCase));

    private List<GaugeItem> BuildItems(JoystickDevice device, OverlayConfig config, bool minimal)
    {
        var present = new HashSet<ushort>(device.Axes.Select(a => a.Usage));
        var bottom = BottomSet(config);
        var order = config.GaugeOrder is { Count: > 0 }
            ? config.GaugeOrder
            : new List<string> { "XY", "Z", "Slider", "RXRY", "HAT" };

        var items = new List<GaugeItem>();
        var placedBars = new HashSet<ushort>();

        foreach (string? raw in order)
        {
            string token = raw?.Trim() ?? "";
            if (token.Length == 0 || Hides(config, minimal, token)) continue;

            switch (token.ToUpperInvariant())
            {
                case "XY":
                    if (present.Contains(Native.USAGE_X) && present.Contains(Native.USAGE_Y))
                        items.Add(new GaugeItem(GaugeKind.StickXY, 0, "X / Y", 1.0f));
                    break;

                case "RXRY":
                    if (present.Contains(Native.USAGE_RX) && present.Contains(Native.USAGE_RY))
                        items.Add(new GaugeItem(GaugeKind.StickRXRY, 0, "RX / RY", 0.62f));
                    break;

                case "HAT":
                case "HATSWITCH":
                    if (present.Contains(Native.USAGE_HATSWITCH))
                        items.Add(new GaugeItem(GaugeKind.Hat, Native.USAGE_HATSWITCH, "HAT", 0.5f));
                    break;

                default:
                    ushort usage = BarUsageFor(token);
                    if (usage == 0 || bottom.Contains(AxisInfo.NameFor(usage))) break;
                    if (present.Contains(usage) && placedBars.Add(usage))
                        items.Add(new GaugeItem(GaugeKind.Bar, usage, AxisInfo.NameFor(usage), 0f));
                    break;
            }
        }

        // Bar axes the device reports but the configured order never mentions.
        foreach (ushort usage in BarUsages)
        {
            if (!present.Contains(usage) || placedBars.Contains(usage)) continue;
            if (bottom.Contains(AxisInfo.NameFor(usage))) continue;
            if (Hides(config, minimal, AxisInfo.NameFor(usage))) continue;
            items.Add(new GaugeItem(GaugeKind.Bar, usage, AxisInfo.NameFor(usage), 0f));
        }

        return items;
    }

    private static ushort BarUsageFor(string token) => token.ToUpperInvariant() switch
    {
        "Z" => Native.USAGE_Z,
        "RZ" => Native.USAGE_RZ,
        "SLIDER" => Native.USAGE_SLIDER,
        "DIAL" => Native.USAGE_DIAL,
        "WHEEL" => Native.USAGE_WHEEL,
        _ => (ushort)0
    };

    /// <summary>Axes pulled out of the row and drawn full width underneath it.</summary>
    private List<GaugeItem> BuildBottomBars(JoystickDevice device, OverlayConfig config, bool minimal)
    {
        var items = new List<GaugeItem>();
        if (config.BottomBars is not { Count: > 0 }) return items;

        var present = new HashSet<ushort>(device.Axes.Select(a => a.Usage));
        var placed = new HashSet<ushort>();

        foreach (string? raw in config.BottomBars)
        {
            string token = raw?.Trim() ?? "";
            if (token.Length == 0 || Hides(config, minimal, token)) continue;

            ushort usage = BarUsageFor(token);
            if (usage == 0 || !present.Contains(usage) || !placed.Add(usage)) continue;

            items.Add(new GaugeItem(GaugeKind.Bar, usage, AxisInfo.NameFor(usage), 0f));
        }

        return items;
    }

    /// <summary>Width the visible row actually occupies, which the bottom bars span.</summary>
    private static float RowWidth(List<GaugeItem> items, GaugeMetrics m)
    {
        if (items.Count == 0) return 0f;
        float width = 0f;
        foreach (var item in items)
            width += (item.Kind == GaugeKind.Bar ? m.BarW : m.Side * item.Factor) + m.Gap;
        return width - m.Gap;
    }

    private static float BottomBarThickness(GaugeMetrics m) => Math.Clamp(m.Side * 0.14f, 14f, 34f);

    private static int ResolveButtonCount(JoystickDevice device, OverlayConfig config)
    {
        if (config.ButtonCount > 0) return config.ButtonCount;
        return Math.Max(device.DeclaredButtonCount, device.State.HighestButtonSeen);
    }

    // ---- Drawing --------------------------------------------------------

    private void DrawGauges(Graphics g, RectangleF area, JoystickDevice device, OverlayConfig config,
        List<GaugeItem> items, GaugeMetrics m, bool labels)
    {
        var state = device.State;

        // One font for every bar caption and readout, sized so the widest of them fits.
        Font barFont = _fontTiny;
        if (labels && items.Any(i => i.Kind == GaugeKind.Bar))
        {
            string widest = "100%";
            foreach (var item in items)
                if (item.Kind == GaugeKind.Bar && item.Label.Length > widest.Length) widest = item.Label;
            barFont = FitBarFont(g, widest, m.BarW);
        }

        float x = area.X;

        foreach (var item in items)
        {
            float w = item.Kind == GaugeKind.Bar ? m.BarW : m.Side * item.Factor;
            float h = item.Kind == GaugeKind.Bar ? m.Side : w;
            if (w < 4f) break;

            var r = new RectangleF(x, area.Y + (m.Side - h) / 2f, w, h);

            switch (item.Kind)
            {
                case GaugeKind.StickXY:
                    DrawStickBox(g, r, labels ? item.Label : null,
                        Value(state, Native.USAGE_X, 0.5, config),
                        Value(state, Native.USAGE_Y, 0.5, config));
                    break;

                case GaugeKind.StickRXRY:
                    DrawStickBox(g, r, labels ? item.Label : null,
                        Value(state, Native.USAGE_RX, 0.5, config),
                        Value(state, Native.USAGE_RY, 0.5, config));
                    break;

                case GaugeKind.Hat:
                    DrawHat(g, r, state.Hat, labels);
                    break;

                case GaugeKind.Bar:
                    bool centred = CentreOriginSet(config).Contains(item.Label);
                    DrawBar(g, r, labels ? item.Label : null,
                        Value(state, item.Usage, centred ? 0.5 : 0.0, config),
                        labels && config.ShowAxisReadouts, centred, barFont);
                    break;
            }

            x += w + m.Gap;
        }
    }

    /// <summary>Normalised axis value, flipped when the axis is listed in <c>invertAxes</c>.</summary>
    private double Value(DeviceState state, ushort usage, double fallback, OverlayConfig config)
    {
        double v = state.Axis.TryGetValue(usage, out double n) ? n : fallback;
        return InvertSet(config).Contains(AxisInfo.NameFor(usage)) ? 1.0 - v : v;
    }

    private HashSet<string> InvertSet(OverlayConfig config)
    {
        if (_invert is null || !ReferenceEquals(_invertSource, config.InvertAxes))
        {
            _invertSource = config.InvertAxes;
            _invert = NameSet(config.InvertAxes);
        }
        return _invert;
    }

    private HashSet<string> BottomSet(OverlayConfig config)
    {
        if (_bottom is null || !ReferenceEquals(_bottomSource, config.BottomBars))
        {
            _bottomSource = config.BottomBars;
            _bottom = NameSet(config.BottomBars);
        }
        return _bottom;
    }

    private HashSet<string> CentreOriginSet(OverlayConfig config)
    {
        if (_centreOrigin is null || !ReferenceEquals(_centreOriginSource, config.CentreOrigin))
        {
            _centreOriginSource = config.CentreOrigin;
            _centreOrigin = NameSet(config.CentreOrigin);
        }
        return _centreOrigin;
    }

    private static HashSet<string> NameSet(List<string>? names)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? name in names ?? new List<string>())
            if (!string.IsNullOrWhiteSpace(name)) set.Add(name.Trim());
        return set;
    }

    private void DrawTitle(Graphics g, RectangleF r, JoystickDevice? device, bool locked, ViewMode mode,
        string? lockHotkey, string? modeHotkey)
    {
        string lockKey = lockHotkey ?? "tray menu";
        string modeKey = modeHotkey ?? "tray menu";
        string hint = locked
            ? (mode == ViewMode.Full ? $"LOCKED  {lockKey}" : $"{mode.ToString().ToUpperInvariant()} · LOCKED  {lockKey}")
            : $"UNLOCKED · {modeKey} mode";

        // Give the hint exactly the room it needs, so trimming the window does not clip it.
        float hintW = Math.Min(
            g.MeasureString(hint, _fontTiny, PointF.Empty, StringFormat.GenericTypographic).Width + 6f,
            r.Width * 0.65f);

        string name = device?.DisplayName ?? "Waiting for device";
        using var left = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        g.DrawString(name, _fontTitle, _text, new RectangleF(r.X, r.Y, Math.Max(10f, r.Width - hintW), r.Height), left);

        using var right = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Far,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip
        };
        g.DrawString(hint, _fontTiny, locked ? _textDim : _accent,
            new RectangleF(r.Right - hintW, r.Y, hintW, r.Height), right);
    }

    private void DrawStickBox(Graphics g, RectangleF r, string? label, double nx, double ny)
    {
        g.FillRectangle(_panel, r);
        g.DrawRectangle(_edge, r.X, r.Y, r.Width, r.Height);

        float cx = r.X + r.Width / 2f;
        float cy = r.Y + r.Height / 2f;
        g.DrawLine(_gridPen, r.X, cy, r.Right, cy);
        g.DrawLine(_gridPen, cx, r.Y, cx, r.Bottom);
        float inset = r.Width * 0.25f;
        g.DrawEllipse(_gridPen, cx - inset, cy - inset, inset * 2, inset * 2);

        // HID Y grows downward, which already matches screen space.
        float px = Math.Clamp(r.X + (float)(nx * r.Width), r.X + 1, r.Right - 1);
        float py = Math.Clamp(r.Y + (float)(ny * r.Height), r.Y + 1, r.Bottom - 1);

        g.DrawLine(_gridPen, cx, cy, px, py);

        float dot = Math.Max(3f, r.Width * 0.055f);
        g.FillEllipse(_accentSoft, px - dot * 2.2f, py - dot * 2.2f, dot * 4.4f, dot * 4.4f);
        g.FillEllipse(_accent, px - dot, py - dot, dot * 2, dot * 2);

        if (label is not null && r.Height > 40)
            g.DrawString(label, _fontTiny, _textDim, r.X + 3, r.Y + 2);
    }

    private void DrawHat(Graphics g, RectangleF r, int hat, bool label)
    {
        g.FillRectangle(_panel, r);
        g.DrawRectangle(_edge, r.X, r.Y, r.Width, r.Height);

        float cx = r.X + r.Width / 2f;
        float cy = r.Y + r.Height / 2f;
        float radius = Math.Min(r.Width, r.Height) * 0.33f;
        float dot = Math.Max(2.5f, radius * 0.28f);

        for (int i = 0; i < 8; i++)
        {
            double angle = (Math.PI / 4.0) * i - Math.PI / 2.0;   // 0 = north, clockwise
            float dx = cx + (float)(Math.Cos(angle) * radius);
            float dy = cy + (float)(Math.Sin(angle) * radius);
            bool on = hat == i;
            g.FillEllipse(on ? _accent : _panel, dx - dot, dy - dot, dot * 2, dot * 2);
            g.DrawEllipse(on ? _accentPen : _gridPen, dx - dot, dy - dot, dot * 2, dot * 2);
        }

        g.FillEllipse(hat < 0 ? _textDim : _accentSoft, cx - dot * 0.6f, cy - dot * 0.6f, dot * 1.2f, dot * 1.2f);

        if (label && r.Height > 34)
            g.DrawString("HAT", _fontTiny, _textDim, r.X + 3, r.Y + 2);
    }

    private void DrawBar(Graphics g, RectangleF r, string? label, double value, bool readout,
        bool centreOrigin, Font font)
    {
        float labelH = label is not null && r.Height > 44 ? font.Height + 2f : 0f;
        float readoutH = readout && r.Height > 60 ? font.Height + 2f : 0f;
        var track = new RectangleF(r.X, r.Y + labelH, r.Width, r.Height - labelH - readoutH);

        g.FillRectangle(_panel, track);
        g.DrawRectangle(_edge, track.X, track.Y, track.Width, track.Height);

        float y = track.Bottom - (float)(Math.Clamp(value, 0.0, 1.0) * track.Height);
        float origin = centreOrigin ? track.Y + track.Height / 2f : track.Bottom;

        if (centreOrigin)
            g.DrawLine(_gridPen, track.X + 1, origin, track.Right - 1, origin);

        float top = Math.Min(origin, y);
        float height = Math.Abs(origin - y);
        if (height > 0.5f)
            g.FillRectangle(_accentSoft, new RectangleF(track.X + 1, top, track.Width - 1, height));
        g.DrawLine(_accentPen, track.X + 1, y, track.Right - 1, y);

        if (labelH > 0)
            g.DrawString(label!, font, _textDim, new RectangleF(r.X, r.Y, r.Width, labelH), _centreTight);
        if (readoutH > 0)
            g.DrawString(Readout(value, centreOrigin), font, _text,
                new RectangleF(r.X, track.Bottom, r.Width, readoutH), _centreTight);
    }

    /// <summary>
    /// A full-width horizontal bar: caption on the left, track in the middle, readout on the
    /// right. With <paramref name="centreOrigin"/> the fill grows from the centre tick towards
    /// whichever side the axis has moved, which is how a yaw axis reads naturally.
    /// </summary>
    private void DrawHBar(Graphics g, RectangleF r, string? label, double value, bool readout,
        bool centreOrigin, Font font)
    {
        float labelW = label is null ? 0f : MeasureText(g, label, font) + 6f;
        float readoutW = readout ? MeasureText(g, centreOrigin ? "-100%" : "100%", font) + 6f : 0f;

        var track = new RectangleF(r.X + labelW, r.Y,
            Math.Max(8f, r.Width - labelW - readoutW), r.Height);

        g.FillRectangle(_panel, track);
        g.DrawRectangle(_edge, track.X, track.Y, track.Width, track.Height);

        float x = track.X + (float)(Math.Clamp(value, 0.0, 1.0) * track.Width);
        float origin = centreOrigin ? track.X + track.Width / 2f : track.X;

        if (centreOrigin)
            g.DrawLine(_gridPen, origin, track.Y + 1, origin, track.Bottom - 1);

        float left = Math.Min(origin, x);
        float width = Math.Abs(origin - x);
        if (width > 0.5f)
            g.FillRectangle(_accentSoft, new RectangleF(left, track.Y + 1, width, track.Height - 1));
        g.DrawLine(_accentPen, x, track.Y + 1, x, track.Bottom - 1);

        if (label is not null)
            g.DrawString(label, font, _textDim, new RectangleF(r.X, r.Y, labelW, r.Height), _leftTight);
        if (readout)
            g.DrawString(Readout(value, centreOrigin), font, _text,
                new RectangleF(track.Right, r.Y, readoutW, r.Height), _rightTight);
    }

    /// <summary>Centre-origin axes read as a signed deviation, so centred shows 0 rather than 50.</summary>
    private static string Readout(double value, bool centreOrigin) =>
        centreOrigin ? $"{(value - 0.5) * 200:+0;-0;0}%" : $"{value * 100:0}%";

    private static float MeasureText(Graphics g, string text, Font font) =>
        g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width;

    private void DrawButtons(Graphics g, RectangleF area, DeviceState state, int count, OverlayConfig config)
    {
        int cols = Math.Max(1, (int)Math.Round(Math.Sqrt(count * (area.Width / Math.Max(area.Height, 1f)))));
        cols = Math.Min(cols, count);
        int rows = (int)Math.Ceiling(count / (double)cols);

        float gap = Math.Max(1.5f, Math.Min(area.Width / cols, area.Height / rows) * 0.12f);
        float cw = (area.Width - gap * (cols - 1)) / cols;
        float ch = (area.Height - gap * (rows - 1)) / rows;
        if (cw < 2 || ch < 2) return;

        // Shrink the cell font until the widest label fits, so three-digit numbers on a
        // 128-button stick are not clipped to "12".
        string widest = WidestLabel(count, config);
        float needed = g.MeasureString(widest, _fontTiny, PointF.Empty, StringFormat.GenericTypographic).Width;
        float cellSize = _fontTiny.Size * Math.Min(1f, (cw - 3f) / Math.Max(needed, 1f));
        bool labelFits = cellSize >= MinFontPx && ch >= cellSize * 1.7f;
        if (labelFits) EnsureCellFont(cellSize);
        Font cellFont = _cellFont ?? _fontTiny;

        for (int i = 0; i < count; i++)
        {
            var cell = new RectangleF(area.X + (i % cols) * (cw + gap), area.Y + (i / cols) * (ch + gap), cw, ch);
            int number = i + 1;
            bool on = state.Buttons.Contains(number);

            g.FillRectangle(on ? _accent : _panel, cell);
            g.DrawRectangle(on ? _accentPen : _gridPen, cell.X, cell.Y, cell.Width, cell.Height);

            if (!labelFits) continue;
            string label = config.ButtonLabels.TryGetValue(number.ToString(), out var custom) && custom.Length > 0
                ? custom
                : number.ToString();
            using var brush = new SolidBrush(on ? Background : TextDim);
            g.DrawString(label, cellFont, brush, cell, _centreTight);
        }
    }

    // ---- Font caching ---------------------------------------------------

    private Font FitBarFont(Graphics g, string widest, float barWidth)
    {
        float needed = g.MeasureString(widest, _fontTiny, PointF.Empty, StringFormat.GenericTypographic).Width;
        float size = _fontTiny.Size * Math.Min(1f, (barWidth - 3f) / Math.Max(needed, 1f));
        if (size >= _fontTiny.Size - 0.05f) return _fontTiny;

        size = Math.Max(MinFontPx, size);
        if (_barFont is null || Math.Abs(_barFont.Size - size) > 0.15f)
        {
            _barFont?.Dispose();
            _barFont = NewFont("Segoe UI", size);
        }
        return _barFont;
    }

    private void EnsureCellFont(float size)
    {
        if (_cellFont is not null && Math.Abs(_cellFont.Size - size) < 0.15f) return;
        _cellFont?.Dispose();
        _cellFont = NewFont("Segoe UI", size);
    }

    private string WidestLabel(int count, OverlayConfig config)
    {
        if (_labelCount == count && ReferenceEquals(_labelSource, config.ButtonLabels)) return _widestLabel;

        _labelCount = count;
        _labelSource = config.ButtonLabels;
        _widestLabel = count.ToString();
        foreach (var kv in config.ButtonLabels)
            if (kv.Value is { Length: > 0 } && kv.Value.Length > _widestLabel.Length)
                _widestLabel = kv.Value;
        return _widestLabel;
    }

    private void EnsureBackgroundAlpha(double alpha)
    {
        int a = (int)Math.Round(Math.Clamp(alpha, 0.0, 1.0) * 255);
        if (a == _bgAlpha) return;

        _bgAlpha = a;
        _bg.Dispose();
        _panel.Dispose();
        _bg = new SolidBrush(Color.FromArgb(a, Background));
        _panel = new SolidBrush(Color.FromArgb(a, Panel));
    }

    private void EnsureFonts(int basisHeight)
    {
        float scale = Math.Clamp(basisHeight / 320f, 0.7f, 2.4f);
        if (Math.Abs(scale - _fontScale) < 0.05f) return;
        _fontScale = scale;

        _fontTiny.Dispose();
        _fontSmall.Dispose();
        _fontTitle.Dispose();
        _fontTiny = NewFont("Segoe UI", TinyPx * scale);
        _fontSmall = NewFont("Segoe UI", SmallPx * scale);
        _fontTitle = NewFont("Segoe UI Semibold", TitlePx * scale);
    }

    public void Dispose()
    {
        _bg.Dispose(); _panel.Dispose(); _text.Dispose(); _textDim.Dispose();
        _accent.Dispose(); _accentSoft.Dispose();
        _edge.Dispose(); _accentPen.Dispose(); _gridPen.Dispose();
        _fontTiny.Dispose(); _fontSmall.Dispose(); _fontTitle.Dispose();
        _cellFont?.Dispose(); _barFont?.Dispose();
        DisposeModeResources();
        _centre.Dispose(); _centreTight.Dispose();
        _leftTight.Dispose(); _rightTight.Dispose();
    }
}
