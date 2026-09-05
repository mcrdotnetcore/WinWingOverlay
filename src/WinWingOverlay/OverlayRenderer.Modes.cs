using System.Drawing.Drawing2D;

namespace WinWingOverlay;

/// <summary>
/// Collective mode, the radial mode dials, and the in-overlay settings page.
///
/// Every interactive thing drawn here also records a <see cref="HitRegion"/>, so the form
/// never has to duplicate layout maths to know what was clicked.
/// </summary>
internal sealed partial class OverlayRenderer
{
    private enum MenuRowKind { Slider, Toggle, Button }

    private readonly record struct MenuRow(string Id, string Label, MenuRowKind Kind);

    private static readonly MenuRow[] Rows =
    {
        new("opacity", "Everything", MenuRowKind.Slider),
        new("bgopacity", "Background", MenuRowKind.Slider),
        new("obs", "Show in capture list (OBS)", MenuRowKind.Toggle),
        new("buttons", "Button grid", MenuRowKind.Toggle),
        new("readouts", "Axis readouts", MenuRowKind.Toggle),
        new("lock", "Lock overlay", MenuRowKind.Button),
        new("reset", "Reset position", MenuRowKind.Button),
        new("rescan", "Rescan devices", MenuRowKind.Button),
        new("config", "Open config folder", MenuRowKind.Button),
        new("exit", "Exit overlay", MenuRowKind.Button)
    };

    /// <summary>Clickable regions from the most recent render. Empty while locked.</summary>
    public List<HitRegion> Hits { get; } = new();

    private Font? _bigFont;
    private float _bigFontPx = -1f;
    private Font? _dialFont;
    private float _dialFontPx = -1f;

    private Bitmap? _scratch;
    private Graphics? _measure;

    /// <summary>A throwaway surface purely for text measurement outside a paint pass.</summary>
    private Graphics Measure()
    {
        if (_measure is null)
        {
            _scratch = new Bitmap(1, 1);
            _measure = Graphics.FromImage(_scratch);
        }
        return _measure;
    }

    private static Color ParseColour(string? text, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        try { return ColorTranslator.FromHtml(text.Trim()); }
        catch { return fallback; }
    }

    // ---- Collective ------------------------------------------------------

    private Font CollectiveFontPx(float px)
    {
        px = Math.Clamp(px, 10f, 400f);
        if (_bigFont is null || Math.Abs(_bigFontPx - px) > 0.5f)
        {
            _bigFont?.Dispose();
            _bigFont = new Font("Segoe UI Semibold", px, FontStyle.Regular, GraphicsUnit.Pixel);
            _bigFontPx = px;
        }
        return _bigFont;
    }

    /// <summary>Number size for a Collective view that has never been resized by hand.</summary>
    private static float DefaultCollectivePx(Size basis) =>
        Math.Clamp(Math.Min(basis.Height * 0.30f, basis.Width * 0.20f), 22f, 190f);

    /// <summary>
    /// The widest string Collective can produce. Sizing against it, rather than the current
    /// value, keeps the number stable as the axis moves and when the % symbol is toggled off.
    /// </summary>
    private const string WidestCollective = "-100%";

    /// <summary>Collective mode is sized to the number, but never narrower than the dial row.</summary>
    public Size MeasureCollective(Size basis, OverlayConfig config)
    {
        EnsureFonts(basis.Height);

        float pad = Math.Max(6f, basis.Height * 0.025f);
        var font = CollectiveFontPx(DefaultCollectivePx(basis));

        float textW = MeasureText(Measure(), WidestCollective, font);
        float minW = DialsWidth(basis) + pad * 2f;

        return new Size(
            (int)Math.Ceiling(Math.Max(textW + pad * 2.5f, minW)),
            (int)Math.Ceiling(font.Height + pad * 1.6f));
    }

    private void DrawCollective(Graphics g, Rectangle client, JoystickDevice? device,
        OverlayConfig config, Size basis, bool locked, double backgroundAlpha, float bottomInset)
    {
        var navy = ParseColour(config.CollectiveBackground, Color.FromArgb(16, 30, 58));
        int alpha = (int)Math.Round(Math.Clamp(backgroundAlpha, 0.0, 1.0) * 255);

        using (var back = new SolidBrush(Color.FromArgb(alpha, navy)))
            g.FillRectangle(back, client);

        using (var border = new Pen(locked ? Color.FromArgb(46, 66, 104) : Accent, locked ? 1f : 2f))
            g.DrawRectangle(border, 0, 0, client.Width - 1, client.Height - 1);

        ushort usage = BarUsageFor(string.IsNullOrWhiteSpace(config.CollectiveAxis)
            ? "Slider"
            : config.CollectiveAxis.Trim());

        string text = "--";
        if (device is not null && usage != 0 && device.Axes.Any(a => a.Usage == usage))
        {
            bool centred = CentreOriginSet(config).Contains(AxisInfo.NameFor(usage));
            text = Readout(Value(device.State, usage, centred ? 0.5 : 0.0, config), centred);

            // The corner button only drops the symbol; the value stays a percentage.
            if (!config.CollectiveShowPercent) text = text.TrimEnd('%');
        }

        var area = new RectangleF(client.X, client.Y, client.Width,
            Math.Max(10f, client.Height - bottomInset));

        // Fit the number to the box on both axes, against the text actually being shown, so a
        // small window still gets a large number. Short readings therefore draw bigger than
        // long ones.
        float marginX = Math.Max(2f, area.Width * 0.05f);
        float marginY = Math.Max(2f, area.Height * 0.07f);

        var reference = g.MeasureString(text, _fontSmall, PointF.Empty, StringFormat.GenericTypographic);
        float px = area.Height * 0.9f;
        if (reference.Width > 0.1f && reference.Height > 0.1f)
        {
            px = _fontSmall.Size * Math.Min(
                (area.Width - marginX) / reference.Width,
                (area.Height - marginY) / reference.Height);
        }

        var font = CollectiveFontPx(MathF.Round(px));

        using var brush = new SolidBrush(ParseColour(config.CollectiveText, Color.White));
        g.DrawString(text, font, brush, area, _centreTight);

        if (!locked) DrawPercentToggle(g, client, config, basis);
    }

    /// <summary>
    /// The tiny corner button that hides or shows the F / M / C bar. It stays visible whenever
    /// the overlay is unlocked, otherwise there would be no way to bring the bar back.
    /// </summary>
    private void DrawChromeToggle(Graphics g, Rectangle client, OverlayConfig config, Size basis)
    {
        float d = DialDiameter(basis) * 0.6f;
        float inset = Math.Max(3f, basis.Height * 0.012f);
        var rect = new RectangleF(client.X + inset, client.Y + inset, d, d);

        g.FillEllipse(_panel, rect);
        g.DrawEllipse(config.ShowDials ? _accentPen : _gridPen, rect);

        if (config.ShowDials)
        {
            float r = d * 0.24f;
            g.FillEllipse(_accent, rect.X + d / 2f - r, rect.Y + d / 2f - r, r * 2, r * 2);
        }

        Hits.Add(new HitRegion("dials", rect, HitKind.Button));
    }

    /// <summary>The small corner button that shows or hides the % symbol.</summary>
    private void DrawPercentToggle(Graphics g, Rectangle client, OverlayConfig config, Size basis)
    {
        float d = DialDiameter(basis) * 0.72f;
        float inset = Math.Max(4f, basis.Height * 0.014f);
        var rect = new RectangleF(client.Right - inset - d, client.Y + inset, d, d);

        bool on = config.CollectiveShowPercent;
        g.FillEllipse(on ? _accent : _panel, rect);
        g.DrawEllipse(on ? _accentPen : _gridPen, rect);

        using (var glyph = new SolidBrush(on ? Background : Text))
            g.DrawString("%", DialFont(basis), glyph, rect, _centreTight);

        Hits.Add(new HitRegion("collective:percent", rect, HitKind.Button));
    }

    // ---- Radial mode dials ----------------------------------------------

    private static float DialDiameter(Size basis) => Math.Clamp(basis.Height * 0.055f, 18f, 30f);

    private static float DialGap(Size basis) => DialDiameter(basis) * 0.4f;

    private static float DialsWidth(Size basis) => 4f * DialDiameter(basis) + 3f * DialGap(basis);

    /// <summary>
    /// Height the window gains while unlocked so the dials get their own strip along the
    /// bottom instead of covering a gauge. Zero while locked, when no dials are drawn.
    /// </summary>
    public float DialStripHeight(Size basis) =>
        DialDiameter(basis) + Math.Max(6f, basis.Height * 0.025f) * 1.6f;

    private Font DialFont(Size basis)
    {
        float px = Math.Max(MinFontPx, DialDiameter(basis) * 0.46f);
        if (_dialFont is null || Math.Abs(_dialFontPx - px) > 0.3f)
        {
            _dialFont?.Dispose();
            _dialFont = new Font("Segoe UI Semibold", px, FontStyle.Regular, GraphicsUnit.Pixel);
            _dialFontPx = px;
        }
        return _dialFont;
    }

    /// <summary>
    /// The mode selector: one round dial per mode plus a menu dial, drawn bottom-right over
    /// whatever is behind them. Only ever shown while the overlay is unlocked.
    /// </summary>
    private void DrawDials(Graphics g, Rectangle client, ViewMode mode, bool menuOpen, Size basis)
    {
        float d = DialDiameter(basis);
        float gap = DialGap(basis);
        float pad = Math.Max(6f, basis.Height * 0.025f);
        float total = DialsWidth(basis);

        float x = client.Right - pad - total;
        float y = client.Bottom - pad - d;
        if (x < pad) x = pad;
        if (y < pad) y = pad;

        // A backdrop keeps the dials readable when they sit over a gauge.
        var tray = new RectangleF(x - gap * 0.7f, y - gap * 0.7f,
            total + gap * 1.4f, d + gap * 1.4f);
        using (var backdrop = new SolidBrush(Color.FromArgb(190, 10, 13, 18)))
        using (var path = RoundedRect(tray, d * 0.55f))
            g.FillPath(backdrop, path);

        (string Id, string Glyph, bool Active)[] dials =
        {
            ("mode:full", "F", !menuOpen && mode == ViewMode.Full),
            ("mode:minimal", "M", !menuOpen && mode == ViewMode.Minimal),
            ("mode:collective", "C", !menuOpen && mode == ViewMode.Collective),
            ("menu", "=", menuOpen)
        };

        var font = DialFont(basis);

        foreach (var (id, glyph, active) in dials)
        {
            var rect = new RectangleF(x, y, d, d);

            g.FillEllipse(active ? _accent : _panel, rect);
            g.DrawEllipse(active ? _accentPen : _gridPen, rect);

            using var text = new SolidBrush(active ? Background : Text);
            g.DrawString(glyph, font, text, rect, _centreTight);

            Hits.Add(new HitRegion(id, rect, HitKind.Button));
            x += d + gap;
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ---- Settings page ---------------------------------------------------

    private (float Pad, float RowH, float Gap, float TitleH, float LabelW, Size Size) MenuMetrics(Size basis)
    {
        EnsureFonts(basis.Height);

        float pad = Math.Max(8f, basis.Height * 0.028f);
        float rowH = _fontSmall.Height + 12f;
        float gap = Math.Max(4f, rowH * 0.16f);
        float titleH = _fontTitle.Height + 6f;

        float labelW = 0f;
        var g = Measure();
        foreach (var row in Rows)
            if (row.Kind != MenuRowKind.Button)
                labelW = Math.Max(labelW, MeasureText(g, row.Label, _fontSmall));
        labelW += pad;

        float width = Math.Clamp(labelW + 150f + pad * 2f, 300f, 460f);
        float height = pad + titleH + Rows.Length * (rowH + gap)
                       + DialDiameter(basis) + pad * 2f;

        return (pad, rowH, gap, titleH, labelW,
            new Size((int)Math.Ceiling(width), (int)Math.Ceiling(height)));
    }

    public Size MeasureMenu(Size basis) => MenuMetrics(basis).Size;

    private void DrawMenuPage(Graphics g, Rectangle client, OverlayConfig config, Size basis, bool locked)
    {
        var (pad, rowH, gap, titleH, labelW, _) = MenuMetrics(basis);

        g.FillRectangle(_bg, client);
        using (var border = new Pen(locked ? Edge : Accent, locked ? 1f : 2f))
            g.DrawRectangle(border, 0, 0, client.Width - 1, client.Height - 1);

        g.DrawString("Overlay settings", _fontTitle, _text, pad, pad * 0.5f);

        float y = pad * 0.5f + titleH;
        float right = client.Width - pad;

        foreach (var row in Rows)
        {
            var area = new RectangleF(pad, y, right - pad, rowH);

            switch (row.Kind)
            {
                case MenuRowKind.Slider:
                {
                    double value = row.Id == "opacity" ? config.Opacity : config.BackgroundOpacity;
                    DrawMenuSlider(g, area, row, labelW, value);
                    break;
                }

                case MenuRowKind.Toggle:
                {
                    bool on = row.Id switch
                    {
                        "obs" => config.ShowInWindowList,
                        "buttons" => config.ShowButtons,
                        _ => config.ShowAxisReadouts
                    };
                    DrawMenuToggle(g, area, row, labelW, on);
                    break;
                }

                default:
                    DrawMenuButton(g, area, row);
                    break;
            }

            y += rowH + gap;
        }
    }

    private void DrawMenuSlider(Graphics g, RectangleF area, MenuRow row, float labelW, double value)
    {
        g.DrawString(row.Label, _fontSmall, _textDim,
            new RectangleF(area.X, area.Y, labelW, area.Height), _leftTight);

        float knob = area.Height * 0.28f;
        float readoutW = MeasureText(g, "100%", _fontSmall) + 10f;

        // Inset by the knob radius at both ends so a knob at 0 % or 100 % stays inside the row.
        var track = new RectangleF(area.X + labelW + knob, area.Y + area.Height * 0.35f,
            Math.Max(20f, area.Width - labelW - readoutW - knob * 2f), area.Height * 0.30f);

        g.FillRectangle(_panel, track);
        g.DrawRectangle(_edge, track.X, track.Y, track.Width, track.Height);

        float x = track.X + (float)(Math.Clamp(value, 0.0, 1.0) * track.Width);
        if (x > track.X + 1f)
            g.FillRectangle(_accentSoft, new RectangleF(track.X + 1, track.Y + 1, x - track.X - 1, track.Height - 1));

        g.FillEllipse(_accent, x - knob, track.Y + track.Height / 2f - knob, knob * 2, knob * 2);

        g.DrawString($"{value * 100:0}%", _fontSmall, _text,
            new RectangleF(area.Right - readoutW, area.Y, readoutW, area.Height), _rightTight);

        // Grab anywhere on the row height, not just the thin track.
        Hits.Add(new HitRegion(row.Id, new RectangleF(track.X, area.Y, track.Width, area.Height),
            HitKind.Slider));
    }

    private void DrawMenuToggle(Graphics g, RectangleF area, MenuRow row, float labelW, bool on)
    {
        g.DrawString(row.Label, _fontSmall, _textDim,
            new RectangleF(area.X, area.Y, labelW, area.Height), _leftTight);

        float h = area.Height * 0.52f;
        float w = h * 1.9f;
        var pill = new RectangleF(area.Right - w, area.Y + (area.Height - h) / 2f, w, h);

        using (var path = RoundedRect(pill, h / 2f))
        {
            g.FillPath(on ? _accent : _panel, path);
            g.DrawPath(on ? _accentPen : _gridPen, path);
        }

        float knob = h * 0.36f;
        float cx = on ? pill.Right - h / 2f : pill.X + h / 2f;
        using (var dot = new SolidBrush(on ? Background : TextDim))
            g.FillEllipse(dot, cx - knob, pill.Y + h / 2f - knob, knob * 2, knob * 2);

        Hits.Add(new HitRegion(row.Id, area, HitKind.Button));
    }

    private void DrawMenuButton(Graphics g, RectangleF area, MenuRow row)
    {
        using (var path = RoundedRect(area, area.Height * 0.25f))
        {
            g.FillPath(_panel, path);
            g.DrawPath(_gridPen, path);
        }

        g.DrawString(row.Label, _fontSmall, _text, area, _centreTight);
        Hits.Add(new HitRegion(row.Id, area, HitKind.Button));
    }

    private void DisposeModeResources()
    {
        _bigFont?.Dispose();
        _dialFont?.Dispose();
        _measure?.Dispose();
        _scratch?.Dispose();
    }
}
