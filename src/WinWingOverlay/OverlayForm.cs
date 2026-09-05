using System.ComponentModel;

namespace WinWingOverlay;

/// <summary>
/// The overlay window itself: borderless, always on top, never takes focus, and click-through
/// while locked. It draws its own content and is not related to the game process in any way.
/// </summary>
internal sealed class OverlayForm : Form
{
    private const int HotkeyToggleLock = 1;
    private const int HotkeyToggleShow = 2;
    private const int HotkeyToggleMinimal = 3;
    private const int ResizeBorder = 7;
    private const int MinHeightFull = 120;
    private const int MinHeightMinimal = 72;

    private readonly OverlayConfig _config;
    private readonly RawInputManager _input = new();
    private readonly OverlayRenderer _renderer = new();
    private readonly LayeredSurface _surface = new();
    private readonly System.Windows.Forms.Timer _frameTimer = new();
    private readonly System.Windows.Forms.Timer _topmostTimer = new();
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _lockItem;
    private readonly ToolStripMenuItem _showItem;
    private readonly ToolStripMenuItem _minimalItem;
    private readonly ToolStripMenuItem _collectiveItem;
    private readonly ToolStripMenuItem _menuPageItem;
    private readonly ToolStripMenuItem _windowListItem;

    private string? _lockKey;
    private string? _showKey;
    private string? _minimalKey;

    private SettingsForm? _settings;
    private JoystickDevice? _device;
    private bool _dirty = true;
    private int _idleTicks;
    private bool _locked;
    private ViewMode _mode;
    private bool _menuOpen;
    private string? _dragSlider;
    private bool _shown;

    public OverlayForm(OverlayConfig config)
    {
        _config = config;
        _locked = config.Locked;
        _mode = config.View;

        // Everything is custom-drawn and scaled from the basis size, so WinForms auto-scaling
        // would only fight the measured minimal-view size.
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(180, MinHeightFull);
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        Bounds = ClampToScreens(new Rectangle(config.X, config.Y, config.Width, config.Height));
        Text = "WinWing Overlay";

        _frameTimer.Interval = Math.Max(8, 1000 / Math.Clamp(config.MaxFps, 15, 144));
        _frameTimer.Tick += OnFrameTick;

        _topmostTimer.Interval = 3000;
        _topmostTimer.Tick += (_, _) => ReassertTopmost();
        _topmostTimer.Start();

        _lockItem = new ToolStripMenuItem("Lock / unlock", null, (_, _) => ToggleLock());
        _showItem = new ToolStripMenuItem("Show / hide", null, (_, _) => ToggleVisible());
        _minimalItem = new ToolStripMenuItem("Minimal view", null, (_, _) => SetMode(ViewMode.Minimal));
        _collectiveItem = new ToolStripMenuItem("Collective view", null, (_, _) => SetMode(ViewMode.Collective));
        _menuPageItem = new ToolStripMenuItem("Settings page", null, (_, _) => ToggleMenuPage());
        _windowListItem = new ToolStripMenuItem("Show in capture window list (OBS)", null,
            (_, _) => ToggleWindowList());
        _tray = BuildTrayIcon();
    }

    /// <summary>
    /// The full-view window size. All layout is computed from this in both modes, so minimal
    /// view crops the window instead of rescaling what stays on screen.
    /// </summary>
    private Size BasisSize => new(
        _config.Width > 0 ? _config.Width : 460,
        _config.Height > 0 ? _config.Height : 320);

    /// <summary>Diagnostic trace, enabled by setting the WINWING_TRACE environment variable.</summary>
    private static void Trace(string message)
    {
        if (Environment.GetEnvironmentVariable("WINWING_TRACE") is null) return;
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "winwing-trace.log"),
                $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>
    /// Resize the window to suit the current view. Everything except Full is measured from the
    /// basis size, never guessed, so each view is a crop of the same layout.
    /// </summary>
    private void ApplyModeSize()
    {
        if (!_shown) return;

        Size target;

        if (_menuOpen)
        {
            target = _renderer.MeasureMenu(BasisSize);
        }
        else
        {
            target = _mode switch
            {
                ViewMode.Minimal => _renderer.MeasureMinimal(BasisSize, _device, _config),
                ViewMode.Collective => CollectiveSize(),
                _ => BasisSize
            };
        }

        // Unlocked, the dials get their own strip along the bottom rather than covering a gauge.
        target.Height += DialStrip;

        // Collective is just a number, so it is allowed to be tiny.
        var min = _menuOpen
            ? new Size(80, MinHeightMinimal)
            : _mode switch
            {
                ViewMode.Collective => new Size(48, 30),
                ViewMode.Minimal => new Size(80, MinHeightMinimal),
                _ => new Size(180, MinHeightFull)
            };
        min.Height += DialStrip;
        MinimumSize = min;

        ClientSize = new Size(Math.Max(MinimumSize.Width, target.Width),
            Math.Max(MinimumSize.Height, target.Height));

        Trace($"ApplyModeSize: mode={_mode} menu={_menuOpen} basis={BasisSize} " +
              $"measured={target} -> ClientSize={ClientSize} handle=0x{Handle.ToInt64():X}");

        // Resizing can rebuild the window handle, and a rebuilt handle comes back with only
        // the CreateParams styles. Anything set with SetWindowLong has to be re-applied.
        ApplyClickThrough();

        Redraw();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED;

            // A tool window is hidden from alt-tab, but capture tools skip it too. Omitting
            // the style is what puts the overlay in the OBS Window Capture list.
            if (_config?.ShowInWindowList != true) cp.ExStyle |= Native.WS_EX_TOOLWINDOW;

            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        try
        {
            _input.Updated += OnDeviceUpdated;
            _input.Start(Handle);
            SelectDevice();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not start Raw Input:\n\n{ex.Message}", "WinWing Overlay",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        RegisterHotkeys();
        ApplyLockState();
        ApplyWindowListStyle();
        _frameTimer.Start();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // Size the window only once the frame has settled. During handle creation WinForms
        // still reports a caption and border for a borderless form, and a ClientSize set then
        // gets that phantom non-client area folded into it.
        _shown = true;
        ApplyModeSize();
        Redraw();
    }

    // ---- Hotkeys --------------------------------------------------------

    private void RegisterHotkeys()
    {
        var keys = _config.Hotkeys ?? new HotkeyConfig();

        (_lockKey, bool lockFell) = Register(HotkeyToggleLock, keys.ToggleLock, "Ctrl+Alt+L", "Ctrl+Alt+Shift+L");
        (_showKey, bool showFell) = Register(HotkeyToggleShow, keys.ToggleShow, "Ctrl+Alt+O", "Ctrl+Alt+Shift+O");
        (_minimalKey, bool minFell) = Register(HotkeyToggleMinimal, keys.ToggleMinimal,
            "Ctrl+Alt+N", "Ctrl+Alt+Shift+M", "Ctrl+Shift+M");

        _lockItem.Text = MenuText("Lock / unlock", _lockKey);
        _showItem.Text = MenuText("Show / hide", _showKey);
        _minimalItem.Text = MenuText("Minimal view", _minimalKey);

        if (lockFell || showFell || minFell)
            ReportHotkeyFallback(keys);
    }

    private static string MenuText(string label, string? key) =>
        key is null ? $"{label}  (no hotkey — combination in use)" : $"{label}  ({key})";

    /// <summary>
    /// Registers the first combination that Windows will actually grant. RegisterHotKey fails
    /// with ERROR_HOTKEY_ALREADY_REGISTERED when another process owns the combination, and a
    /// silent failure there is indistinguishable from a broken hotkey, so the result is
    /// reported rather than assumed.
    /// </summary>
    private (string? Key, bool UsedFallback) Register(int id, string? configured, params string[] fallbacks)
    {
        bool first = true;
        foreach (string? candidate in new[] { configured }.Concat(fallbacks))
        {
            if (Hotkey.TryParse(candidate, out var hotkey))
            {
                if (Native.RegisterHotKey(Handle, id, hotkey.Modifiers | Native.MOD_NOREPEAT, (uint)hotkey.Key))
                    return (hotkey.ToString(), !first);
                first = false;
            }
            else if (!string.IsNullOrWhiteSpace(candidate))
            {
                first = false;   // unparseable configuration counts as a miss
            }
        }
        return (null, true);
    }

    private void ReportHotkeyFallback(HotkeyConfig keys)
    {
        var lines = new List<string>();
        if (!Matches(_lockKey, keys.ToggleLock)) lines.Add($"Lock: {_lockKey ?? "none"} (wanted {keys.ToggleLock})");
        if (!Matches(_showKey, keys.ToggleShow)) lines.Add($"Show: {_showKey ?? "none"} (wanted {keys.ToggleShow})");
        if (!Matches(_minimalKey, keys.ToggleMinimal)) lines.Add($"Minimal: {_minimalKey ?? "none"} (wanted {keys.ToggleMinimal})");
        if (lines.Count == 0) return;

        _tray.BalloonTipTitle = "WinWing Overlay — hotkey in use";
        _tray.BalloonTipText = string.Join(Environment.NewLine, lines) +
                               Environment.NewLine + "Another program owns it. Edit hotkeys in config.json to change.";
        _tray.BalloonTipIcon = ToolTipIcon.Info;
        _tray.ShowBalloonTip(8000);
    }

    private static bool Matches(string? actual, string? wanted) =>
        actual is not null && Hotkey.TryParse(wanted, out var w) && string.Equals(actual, w.ToString(), StringComparison.OrdinalIgnoreCase);

    private void SelectDevice()
    {
        var devices = _input.Devices.ToList();
        if (devices.Count == 0) { _device = null; return; }

        _device = devices.FirstOrDefault(d =>
                     (_config.VendorId == 0 || d.VendorId == (uint)_config.VendorId) &&
                     (_config.ProductId == 0 || d.ProductId == (uint)_config.ProductId))
                 ?? devices[0];
    }

    private void OnDeviceUpdated(JoystickDevice device)
    {
        if (_device is null)
        {
            _device = device;
            // The minimal size depends on which gauges the device actually has.
            if (_mode != ViewMode.Full) ApplyModeSize();
            UpdateTrayText();
        }
        if (device != _device) return;

        _dirty = true;
        _idleTicks = 0;
        if (!_frameTimer.Enabled && Visible) _frameTimer.Start();
    }

    private void OnFrameTick(object? sender, EventArgs e)
    {
        if (_dirty)
        {
            _dirty = false;
            _idleTicks = 0;
            Redraw();
            return;
        }

        // Nothing moved for roughly half a second: stop the timer entirely so the
        // process goes fully idle until the next WM_INPUT wakes it.
        if (++_idleTicks > 30) _frameTimer.Stop();
    }

    // The window is a per-pixel layered window: its content is the surface blitted by
    // Redraw(), so WM_PAINT has nothing to do.
    protected override void OnPaint(PaintEventArgs e) { }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    /// <summary>
    /// Background alpha actually used. While unlocked it is floored, because a layered window
    /// hit-tests by alpha and a fully transparent background would leave nothing to grab.
    /// </summary>
    private double EffectiveBackgroundAlpha =>
        _locked ? _config.BackgroundOpacity : Math.Max(_config.BackgroundOpacity, 0.12);

    /// <summary>Draw a frame and present it. Replaces Invalidate for this window.</summary>
    private void Redraw()
    {
        if (!IsHandleCreated || !Visible) return;
        if (!_surface.Ensure(ClientSize) || _surface.Graphics is null) return;

        _renderer.Render(_surface.Graphics, new Rectangle(Point.Empty, ClientSize), _device, _config,
            _locked, _mode, _lockKey, _minimalKey, BasisSize, EffectiveBackgroundAlpha, _menuOpen);

        _surface.Present(Handle, (byte)Math.Round(Math.Clamp(_config.Opacity, 0.15, 1.0) * 255));
    }

    private void ShowSettings()
    {
        if (_settings is null || _settings.IsDisposed)
        {
            _settings = new SettingsForm(_config, Redraw);
            _settings.FormClosed += (_, _) => _settings = null;
            _settings.Show();
        }
        else
        {
            _settings.Sync();
            _settings.Activate();
        }
    }

    // ---- Interaction ----------------------------------------------------

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_INPUT:
                _input.ProcessInputMessage(m.LParam);
                break;

            case Native.WM_HOTKEY:
                if ((int)m.WParam == HotkeyToggleLock) ToggleLock();
                else if ((int)m.WParam == HotkeyToggleShow) ToggleVisible();
                else if ((int)m.WParam == HotkeyToggleMinimal) CycleMode();
                return;

            case Native.WM_MOUSEACTIVATE:
                // Never steal focus from the game when clicked.
                m.Result = new IntPtr(Native.MA_NOACTIVATE);
                return;

            case Native.WM_NCHITTEST:
                if (!_locked)
                {
                    m.Result = new IntPtr(HitTest(m.LParam));
                    return;
                }
                break;
        }

        base.WndProc(ref m);
    }

    private int HitTest(IntPtr lParam)
    {
        int x = unchecked((short)(long)lParam);
        int y = unchecked((short)((long)lParam >> 16));
        Point p = PointToClient(new Point(x, y));

        // Dials and menu controls must receive real clicks rather than starting a window drag.
        if (FindHit(p) is not null) return Native.HTCLIENT;

        // The settings page is a fixed layout; every view can be resized.
        if (_menuOpen) return Native.HTCAPTION;

        bool left = p.X <= ResizeBorder;
        bool right = p.X >= ClientSize.Width - ResizeBorder;
        bool top = p.Y <= ResizeBorder;
        bool bottom = p.Y >= ClientSize.Height - ResizeBorder;

        if (top && left) return Native.HTTOPLEFT;
        if (top && right) return Native.HTTOPRIGHT;
        if (bottom && left) return Native.HTBOTTOMLEFT;
        if (bottom && right) return Native.HTBOTTOMRIGHT;
        if (left) return Native.HTLEFT;
        if (right) return Native.HTRIGHT;
        if (top) return Native.HTTOP;
        if (bottom) return Native.HTBOTTOM;
        return Native.HTCAPTION;   // drag from anywhere else
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (_locked) return;
        double step = e.Delta > 0 ? 0.05 : -0.05;
        _config.Opacity = Math.Clamp(_config.Opacity + step, 0.15, 1.0);
        _settings?.Sync();
        Redraw();
    }

    private void ToggleLock()
    {
        _locked = !_locked;
        ApplyLockState();
        SaveBounds();
    }

    private void ToggleVisible()
    {
        Visible = !Visible;
        if (Visible)
        {
            ReassertTopmost();
            Redraw();
            _dirty = true;
            _frameTimer.Start();
        }
        else
        {
            _frameTimer.Stop();
        }
    }

    /// <summary>
    /// Views are only switchable while unlocked, so a stray hotkey cannot change the layout
    /// mid-flight.
    /// </summary>
    private void CycleMode() => SetMode(_mode switch
    {
        ViewMode.Full => ViewMode.Minimal,
        ViewMode.Minimal => ViewMode.Collective,
        _ => ViewMode.Full
    });

    private void SetMode(ViewMode mode)
    {
        if (_locked) return;

        // Leaving Full view: remember its size, since it is the basis for every layout.
        if (_mode == ViewMode.Full && !_menuOpen)
        {
            _config.Width = Width;
            _config.Height = Height;
        }

        _menuOpen = false;
        _mode = mode;
        _config.View = mode;

        ApplyModeSize();
        UpdateTrayText();
        UpdateMenuChecks();
        SaveBounds();
    }

    /// <summary>The in-overlay settings page, so opacity can be changed without leaving the game.</summary>
    private void ToggleMenuPage()
    {
        if (_locked) return;

        if (!_menuOpen && _mode == ViewMode.Full)
        {
            _config.Width = Width;
            _config.Height = Height;
        }

        _menuOpen = !_menuOpen;

        ApplyModeSize();
        UpdateTrayText();
        UpdateMenuChecks();
    }

    private void UpdateMenuChecks()
    {
        _minimalItem.Checked = !_menuOpen && _mode == ViewMode.Minimal;
        _collectiveItem.Checked = !_menuOpen && _mode == ViewMode.Collective;
        _menuPageItem.Checked = _menuOpen;
    }

    // ---- Clicking the dials and the settings page ------------------------

    private HitRegion? FindHit(Point client)
    {
        if (_locked) return null;

        for (int i = _renderer.Hits.Count - 1; i >= 0; i--)
            if (_renderer.Hits[i].Rect.Contains(client.X, client.Y))
                return _renderer.Hits[i];

        return null;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (FindHit(e.Location) is not { } hit) return;

        if (hit.Kind == HitKind.Slider)
        {
            _dragSlider = hit.Id;
            ApplySlider(hit, e.X);
            return;
        }

        Activate(hit.Id);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragSlider is null) return;

        foreach (var hit in _renderer.Hits)
        {
            if (hit.Id != _dragSlider) continue;
            ApplySlider(hit, e.X);
            return;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragSlider is null) return;

        _dragSlider = null;
        _config.Save();
    }

    private void ApplySlider(HitRegion hit, int x)
    {
        double t = Math.Clamp((x - hit.Rect.X) / Math.Max(1f, hit.Rect.Width), 0.0, 1.0);

        if (hit.Id == "opacity") _config.Opacity = Math.Clamp(t, 0.15, 1.0);
        else _config.BackgroundOpacity = t;

        _settings?.Sync();
        Redraw();
    }

    private void Activate(string id)
    {
        switch (id)
        {
            case "mode:full": SetMode(ViewMode.Full); break;
            case "mode:minimal": SetMode(ViewMode.Minimal); break;
            case "mode:collective": SetMode(ViewMode.Collective); break;
            case "menu": ToggleMenuPage(); break;

            case "collective:percent":
                _config.CollectiveShowPercent = !_config.CollectiveShowPercent;
                SaveAndRefresh();
                break;

            case "dials":
                _config.ShowDials = !_config.ShowDials;
                SaveAndRefresh();
                break;

            case "obs": ToggleWindowList(); Redraw(); break;
            case "buttons": _config.ShowButtons = !_config.ShowButtons; SaveAndRefresh(); break;
            case "readouts": _config.ShowAxisReadouts = !_config.ShowAxisReadouts; SaveAndRefresh(); break;

            case "lock": _menuOpen = false; ApplyModeSize(); ToggleLock(); break;
            case "reset": ResetPosition(); break;
            case "rescan": RescanDevices(); break;
            case "config": OpenConfigFolder(); break;
            case "exit": Close(); break;
        }
    }

    /// <summary>Anything that changes what is drawn can also change the measured size.</summary>
    private void SaveAndRefresh()
    {
        _config.Save();
        ApplyModeSize();
    }

    private void ToggleWindowList()
    {
        _config.ShowInWindowList = !_config.ShowInWindowList;
        ApplyWindowListStyle();
        _config.Save();
    }

    /// <summary>
    /// Add or remove WS_EX_TOOLWINDOW. The shell and capture tools only re-read this style
    /// when a window is shown, so the visibility is cycled rather than asking for a restart.
    /// </summary>
    private void ApplyWindowListStyle()
    {
        _windowListItem.Checked = _config.ShowInWindowList;
        if (!IsHandleCreated) return;

        bool wasVisible = Visible;
        if (wasVisible) Visible = false;

        int ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
        ex = _config.ShowInWindowList
            ? ex & ~Native.WS_EX_TOOLWINDOW
            : ex | Native.WS_EX_TOOLWINDOW;
        Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, ex);

        if (!wasVisible) return;

        Visible = true;
        ReassertTopmost();
        Redraw();
    }

    /// <summary>
    /// Locked means click-through: WS_EX_TRANSPARENT lets the game receive the mouse. Kept in
    /// its own method because it must be re-applied after anything that resizes the window.
    /// </summary>
    private void ApplyClickThrough()
    {
        if (!IsHandleCreated) return;

        int ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
        int wanted = _locked ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT;
        if (wanted != ex) Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, wanted);
    }

    private void ApplyLockState()
    {
        ApplyClickThrough();

        Cursor = _locked ? Cursors.Default : Cursors.SizeAll;
        _config.Locked = _locked;
        ApplyModeSize();
        _minimalItem.Enabled = !_locked;
        _collectiveItem.Enabled = !_locked;
        _menuPageItem.Enabled = !_locked;

        // The settings page is only reachable while unlocked; locking closes it.
        if (_locked && _menuOpen)
        {
            _menuOpen = false;
            ApplyModeSize();
        }
        Redraw();
        UpdateTrayText();
    }

    private void ReassertTopmost()
    {
        if (!Visible || !IsHandleCreated) return;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);

        if (!_menuOpen && _mode == ViewMode.Collective)
        {
            // Collective is just a number in a box, so it keeps whatever size you drag and the
            // number scales to fill it. No snapping.
            var size = ClientSize;
            size.Height -= DialStrip;
            if (size.Width >= 20 && size.Height >= 16)
            {
                _config.CollectiveWidth = size.Width;
                _config.CollectiveHeight = size.Height;
            }
        }
        else if (!_menuOpen && _mode == ViewMode.Minimal)
        {
            // Minimal is a crop of the Full layout, so a drag has to change the basis rather
            // than the window. Back-solve one that measures near the dragged size, then snap.
            RebaseFromCurrentSize();
            ApplyModeSize();
        }

        SaveBounds();
    }

    /// <summary>Height the dial bar occupies right now, or zero when it is not being drawn.</summary>
    private int DialStrip => !_locked && !_menuOpen && _config.ShowDials
        ? (int)Math.Ceiling(_renderer.DialStripHeight(BasisSize))
        : 0;

    /// <summary>Collective keeps a size of its own once dragged; otherwise it is derived.</summary>
    private Size CollectiveSize() =>
        _config.CollectiveWidth > 0 && _config.CollectiveHeight > 0
            ? new Size(_config.CollectiveWidth, _config.CollectiveHeight)
            : _renderer.MeasureCollective(BasisSize, _config);

    private Size MeasureFor(ViewMode mode, Size basis) => mode switch
    {
        ViewMode.Minimal => _renderer.MeasureMinimal(basis, _device, _config),
        ViewMode.Collective => _renderer.MeasureCollective(basis, _config),
        _ => basis
    };

    private void RebaseFromCurrentSize()
    {
        var target = ClientSize;
        target.Height -= DialStrip;
        if (target.Width < 40 || target.Height < 30) return;

        var basis = BasisSize;

        // The measurement is not linear in the basis, so converge on it instead of inverting it.
        for (int i = 0; i < 4; i++)
        {
            var measured = MeasureFor(_mode, basis);
            if (measured.Width <= 0 || measured.Height <= 0) break;

            basis = new Size(
                Math.Clamp((int)Math.Round(basis.Width * (double)target.Width / measured.Width), 220, 4000),
                Math.Clamp((int)Math.Round(basis.Height * (double)target.Height / measured.Height), 160, 3000));
        }

        _config.Width = basis.Width;
        _config.Height = basis.Height;
    }
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Trace($"OnResize: ClientSize={ClientSize} Size={Size} mode={_mode} menu={_menuOpen}");
        Redraw();
    }

    private void SaveBounds()
    {
        if (WindowState != FormWindowState.Normal) return;
        _config.X = Bounds.X;
        _config.Y = Bounds.Y;
        // Every other view is derived, never stored: only Full view defines the basis.
        if (_mode == ViewMode.Full && !_menuOpen)
        {
            _config.Width = Bounds.Width;
            _config.Height = Bounds.Height;
        }
        _config.Save();
    }

    private void ResetPosition()
    {
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        _config.Width = 460;
        _config.Height = 320;
        _config.CollectiveWidth = 0;
        _config.CollectiveHeight = 0;
        Location = new Point(wa.X + 40, wa.Y + 40);
        ApplyModeSize();
        SaveBounds();
    }

    private void RescanDevices()
    {
        _input.RefreshDevices();
        SelectDevice();
        ApplyModeSize();
        _dirty = true;
        _frameTimer.Start();
    }

    private void OpenConfigFolder()
    {
        _config.Save();
        var dir = Path.GetDirectoryName(OverlayConfig.Path)!;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
    }

    private static Rectangle ClampToScreens(Rectangle r)
    {
        if (r.Width < 180) r.Width = 460;
        if (r.Height < MinHeightMinimal) r.Height = 320;
        foreach (var screen in Screen.AllScreens)
            if (screen.WorkingArea.IntersectsWith(r)) return r;
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        return new Rectangle(wa.X + 40, wa.Y + 40, r.Width, r.Height);
    }

    // ---- Tray -----------------------------------------------------------

    private NotifyIcon BuildTrayIcon()
    {
        var menu = new ContextMenuStrip();

        var opacityItem = new ToolStripMenuItem("Opacity sliders...", null, (_, _) => ShowSettings());
        var resetItem = new ToolStripMenuItem("Reset position", null, (_, _) => ResetPosition());
        var rescanItem = new ToolStripMenuItem("Rescan devices", null, (_, _) => RescanDevices());
        var configItem = new ToolStripMenuItem("Open config folder", null, (_, _) => OpenConfigFolder());
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => Close());

        menu.Items.AddRange(new ToolStripItem[]
        {
            _lockItem, _showItem, _minimalItem, _collectiveItem, new ToolStripSeparator(),
            _menuPageItem, opacityItem, _windowListItem, resetItem, rescanItem, configItem,
            new ToolStripSeparator(), exitItem
        });

        var icon = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Visible = true,
            ContextMenuStrip = menu
        };
        icon.DoubleClick += (_, _) => ToggleLock();
        return icon;
    }

    private void UpdateTrayText()
    {
        string name = _device?.DisplayName ?? "no device";
        string mode = (_locked ? "locked" : "unlocked") + ", " + _mode.ToString().ToLowerInvariant();
        string text = $"WinWing Overlay — {mode} — {name}";
        _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveBounds();
        base.OnClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Native.UnregisterHotKey(Handle, HotkeyToggleLock);
            Native.UnregisterHotKey(Handle, HotkeyToggleShow);
            Native.UnregisterHotKey(Handle, HotkeyToggleMinimal);
            _frameTimer.Dispose();
            _topmostTimer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _settings?.Dispose();
            _surface.Dispose();
            _renderer.Dispose();
            _input.Dispose();
        }
        base.Dispose(disposing);
    }
}
