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

    private string? _lockKey;
    private string? _showKey;
    private string? _minimalKey;

    private SettingsForm? _settings;
    private JoystickDevice? _device;
    private bool _dirty = true;
    private int _idleTicks;
    private bool _locked;
    private bool _minimal;

    public OverlayForm(OverlayConfig config)
    {
        _config = config;
        _locked = config.Locked;
        _minimal = config.Minimal;

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
        _minimalItem = new ToolStripMenuItem("Minimal view", null, (_, _) => ToggleMinimal());
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

    /// <summary>Resize the window to suit the current mode. Minimal is measured, never guessed.</summary>
    private void ApplyMinimalSize()
    {
        if (_minimal)
        {
            var size = _renderer.MeasureMinimal(BasisSize, _device, _config);
            MinimumSize = new Size(80, MinHeightMinimal);
            ClientSize = new Size(Math.Max(MinimumSize.Width, size.Width), Math.Max(MinimumSize.Height, size.Height));
            Trace($"ApplyMinimalSize: basis={BasisSize} measured={size} -> ClientSize={ClientSize} Size={Size}");
        }
        else
        {
            MinimumSize = new Size(180, MinHeightFull);
            ClientSize = BasisSize;
        }

        Redraw();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED;
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
        _frameTimer.Start();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // Size the window only once the frame has settled. During handle creation WinForms
        // still reports a caption and border for a borderless form, and a ClientSize set then
        // gets that phantom non-client area folded into it.
        if (_minimal) ApplyMinimalSize();
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
            if (_minimal) ApplyMinimalSize();
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
            _locked, _minimal, _lockKey, _minimalKey, BasisSize, EffectiveBackgroundAlpha);

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
                else if ((int)m.WParam == HotkeyToggleMinimal) ToggleMinimal();
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
        // Minimal view is sized to fit its content, so there is nothing to drag-resize.
        if (_minimal) return Native.HTCAPTION;

        int x = unchecked((short)(long)lParam);
        int y = unchecked((short)((long)lParam >> 16));
        Point p = PointToClient(new Point(x, y));

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
    /// Minimal view drops the button grid and the hat (see <c>minimalHides</c>) and shrinks the
    /// window to suit. Deliberately only available while unlocked, so it cannot be triggered
    /// by accident mid-flight.
    /// </summary>
    private void ToggleMinimal()
    {
        if (_locked) return;

        // Leaving full view: remember its size, since it is the basis for both layouts.
        if (!_minimal)
        {
            _config.Width = Width;
            _config.Height = Height;
        }

        _minimal = !_minimal;
        _config.Minimal = _minimal;

        ApplyMinimalSize();
        UpdateTrayText();
        SaveBounds();
    }

    private void ApplyLockState()
    {
        int ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
        ex = _locked ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT;
        Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, ex);

        Cursor = _locked ? Cursors.Default : Cursors.SizeAll;
        _config.Locked = _locked;
        _minimalItem.Enabled = !_locked;
        Redraw();
        UpdateTrayText();
    }

    private void ReassertTopmost()
    {
        if (!Visible || !IsHandleCreated) return;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    protected override void OnResizeEnd(EventArgs e) { base.OnResizeEnd(e); SaveBounds(); }
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Trace($"OnResize: ClientSize={ClientSize} Size={Size} minimal={_minimal}");
        Redraw();
    }

    private void SaveBounds()
    {
        if (WindowState != FormWindowState.Normal) return;
        _config.X = Bounds.X;
        _config.Y = Bounds.Y;
        // Minimal size is derived, never stored: only the full view defines the basis.
        if (!_minimal)
        {
            _config.Width = Bounds.Width;
            _config.Height = Bounds.Height;
        }
        _config.Save();
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
        var resetItem = new ToolStripMenuItem("Reset position", null, (_, _) =>
        {
            var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
            _config.Width = 460;
            _config.Height = 320;
            Location = new Point(wa.X + 40, wa.Y + 40);
            ApplyMinimalSize();
            SaveBounds();
        });
        var rescanItem = new ToolStripMenuItem("Rescan devices", null, (_, _) =>
        {
            _input.RefreshDevices();
            SelectDevice();
            if (_minimal) ApplyMinimalSize();
            _dirty = true;
            _frameTimer.Start();
        });
        var configItem = new ToolStripMenuItem("Open config folder", null, (_, _) =>
        {
            _config.Save();
            var dir = Path.GetDirectoryName(OverlayConfig.Path)!;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
        });
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => Close());

        menu.Items.AddRange(new ToolStripItem[]
        {
            _lockItem, _showItem, _minimalItem, new ToolStripSeparator(), opacityItem, resetItem,
            rescanItem, configItem,
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
        string mode = (_locked ? "locked" : "unlocked") + (_minimal ? ", minimal" : "");
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
