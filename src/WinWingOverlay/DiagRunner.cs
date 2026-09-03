namespace WinWingOverlay;

/// <summary>
/// Console mode (--diag). Lists every HID joystick with its declared capabilities, then
/// prints a change log as you move the stick so each physical control can be identified.
/// </summary>
internal static class DiagRunner
{
    public static void Run()
    {
        AttachConsole();

        Console.WriteLine("WinWing Overlay — device diagnostics");
        Console.WriteLine(new string('=', 66));

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Application.Exit(); };

        using var window = new DiagWindow();
        Application.Run(window.Context);   // creates the hidden HWND that Raw Input targets
    }

    private static void AttachConsole()
    {
        if (!Native.AttachConsole(Native.ATTACH_PARENT_PROCESS))
            Native.AllocConsole();

        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);
    }

    private sealed class DiagWindow : Form
    {
        private readonly RawInputManager _input = new();
        private readonly Dictionary<IntPtr, HashSet<int>> _lastButtons = new();
        private readonly Dictionary<IntPtr, Dictionary<ushort, int>> _lastAxes = new();
        private readonly Dictionary<IntPtr, int> _lastHat = new();

        public ApplicationContext Context { get; }

        public DiagWindow()
        {
            Context = new ApplicationContext(this);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            Opacity = 0;
            Size = new Size(1, 1);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            _input.Start(Handle);

            var devices = _input.Devices.ToList();
            if (devices.Count == 0)
            {
                Console.WriteLine("No HID joystick or gamepad devices found.");
            }

            foreach (var d in devices)
            {
                Console.WriteLine();
                Console.WriteLine($"Device : {d.DisplayName}");
                Console.WriteLine($"  VID/PID        : {d.VendorId:X4}:{d.ProductId:X4}");
                Console.WriteLine($"  Declared buttons: {d.DeclaredButtonCount}");
                Console.WriteLine($"  Path           : {d.DevicePath}");
                Console.WriteLine($"  Axes ({d.Axes.Count}):");
                foreach (var a in d.Axes)
                    Console.WriteLine($"    {a.Name,-8} usage 0x{a.Usage:X2}  page 0x{a.UsagePage:X2}  " +
                                      $"range {a.LogicalMin}..{a.LogicalMax}  {a.BitSize} bit");
            }

            Console.WriteLine();
            Console.WriteLine(new string('-', 66));
            Console.WriteLine("Move controls to identify them. Press Ctrl+C in this window to quit.");
            Console.WriteLine();

            _input.Updated += OnUpdated;
        }

        private void OnUpdated(JoystickDevice device)
        {
            var state = device.State;

            if (!_lastButtons.TryGetValue(device.Handle, out var prev))
            {
                prev = new HashSet<int>();
                _lastButtons[device.Handle] = prev;
                _lastAxes[device.Handle] = new Dictionary<ushort, int>();
                _lastHat[device.Handle] = -1;
            }

            foreach (int b in state.Buttons)
                if (!prev.Contains(b)) Console.WriteLine($"  BUTTON {b,3}  down");
            foreach (int b in prev)
                if (!state.Buttons.Contains(b)) Console.WriteLine($"  BUTTON {b,3}  up");

            prev.Clear();
            foreach (int b in state.Buttons) prev.Add(b);

            var lastAxes = _lastAxes[device.Handle];
            foreach (var axis in device.Axes)
            {
                if (axis.Usage == Native.USAGE_HATSWITCH) continue;
                if (!state.AxisRaw.TryGetValue(axis.Usage, out int raw)) continue;

                int span = Math.Max(1, Math.Abs(axis.LogicalMax - axis.LogicalMin));
                int threshold = Math.Max(1, span / 40);   // ignore jitter below ~2.5%
                if (lastAxes.TryGetValue(axis.Usage, out int old) && Math.Abs(raw - old) < threshold) continue;

                lastAxes[axis.Usage] = raw;
                double pct = state.Axis.TryGetValue(axis.Usage, out double n) ? n * 100 : 0;
                Console.WriteLine($"  AXIS   {axis.Name,-8} {pct,5:0.0}%   raw {raw}");
            }

            if (_lastHat[device.Handle] != state.Hat)
            {
                _lastHat[device.Handle] = state.Hat;
                string dir = state.Hat < 0
                    ? "centre"
                    : new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" }[state.Hat];
                Console.WriteLine($"  HAT    {dir}");
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_INPUT) _input.ProcessInputMessage(m.LParam);
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _input.Dispose();
            base.Dispose(disposing);
        }
    }
}
