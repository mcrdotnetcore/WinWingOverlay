using System.Runtime.InteropServices;

namespace WinWingOverlay;

/// <summary>
/// Owns Raw Input registration and turns WM_INPUT messages into decoded device state.
///
/// Only the Generic Desktop joystick / gamepad / multi-axis usages are registered.
/// Keyboard and mouse are deliberately never registered, so this process cannot observe
/// typing or mouse movement even in principle.
/// </summary>
internal sealed class RawInputManager : IDisposable
{
    private static readonly int HeaderSize = IntPtr.Size == 8 ? 24 : 16;

    private readonly Dictionary<IntPtr, JoystickDevice> _devices = new();
    private IntPtr _buffer;
    private int _bufferSize;
    private byte[] _report = new byte[256];
    private IntPtr _hwnd;

    public IReadOnlyCollection<JoystickDevice> Devices => _devices.Values;

    /// <summary>Fires when a decoded report has updated device state.</summary>
    public event Action<JoystickDevice>? Updated;

    public void Start(IntPtr hwnd)
    {
        _hwnd = hwnd;

        foreach (var dev in JoystickDevice.Enumerate())
            _devices[dev.Handle] = dev;

        var registrations = new[]
        {
            MakeRegistration(Native.USAGE_JOYSTICK, hwnd),
            MakeRegistration(Native.USAGE_GAMEPAD, hwnd),
            MakeRegistration(Native.USAGE_MULTI_AXIS, hwnd)
        };

        if (!Native.RegisterRawInputDevices(registrations, (uint)registrations.Length,
                (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>()))
        {
            throw new InvalidOperationException(
                $"RegisterRawInputDevices failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }
    }

    private static Native.RAWINPUTDEVICE MakeRegistration(ushort usage, IntPtr hwnd) => new()
    {
        UsagePage = Native.USAGE_PAGE_GENERIC,
        Usage = usage,
        // INPUTSINK keeps reports flowing while the game holds foreground focus.
        Flags = Native.RIDEV_INPUTSINK,
        Target = hwnd
    };

    /// <summary>Handle a WM_INPUT message. Returns true if device state changed.</summary>
    public bool ProcessInputMessage(IntPtr lParam)
    {
        uint size = 0;
        if (Native.GetRawInputData(lParam, Native.RID_INPUT, IntPtr.Zero, ref size, (uint)HeaderSize) != 0 || size == 0)
            return false;

        EnsureBuffer((int)size);
        if (Native.GetRawInputData(lParam, Native.RID_INPUT, _buffer, ref size, (uint)HeaderSize) != size)
            return false;

        uint type = (uint)Marshal.ReadInt32(_buffer, 0);
        if (type != Native.RIM_TYPEHID) return false;

        IntPtr hDevice = Marshal.ReadIntPtr(_buffer, 8);
        if (!_devices.TryGetValue(hDevice, out var device))
        {
            // Device appeared after startup (hot-plug); re-enumerate once and retry.
            RefreshDevices();
            if (!_devices.TryGetValue(hDevice, out device)) return false;
        }

        int sizeHid = Marshal.ReadInt32(_buffer, HeaderSize);
        int count = Marshal.ReadInt32(_buffer, HeaderSize + 4);
        if (sizeHid <= 0 || count <= 0) return false;

        if (_report.Length < sizeHid) _report = new byte[sizeHid];

        IntPtr data = _buffer + HeaderSize + 8;
        for (int i = 0; i < count; i++)
        {
            Marshal.Copy(data + (i * sizeHid), _report, 0, sizeHid);
            device.ParseReport(_report, sizeHid);
        }

        Updated?.Invoke(device);
        return true;
    }

    public void RefreshDevices()
    {
        foreach (var dev in JoystickDevice.Enumerate())
        {
            if (_devices.ContainsKey(dev.Handle)) { dev.Dispose(); continue; }
            _devices[dev.Handle] = dev;
        }
    }

    private void EnsureBuffer(int size)
    {
        if (_bufferSize >= size) return;
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        _buffer = Marshal.AllocHGlobal(size);
        _bufferSize = size;
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            var remove = new[]
            {
                new Native.RAWINPUTDEVICE { UsagePage = Native.USAGE_PAGE_GENERIC, Usage = Native.USAGE_JOYSTICK, Flags = Native.RIDEV_REMOVE },
                new Native.RAWINPUTDEVICE { UsagePage = Native.USAGE_PAGE_GENERIC, Usage = Native.USAGE_GAMEPAD, Flags = Native.RIDEV_REMOVE },
                new Native.RAWINPUTDEVICE { UsagePage = Native.USAGE_PAGE_GENERIC, Usage = Native.USAGE_MULTI_AXIS, Flags = Native.RIDEV_REMOVE }
            };
            Native.RegisterRawInputDevices(remove, (uint)remove.Length, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>());
            _hwnd = IntPtr.Zero;
        }

        foreach (var dev in _devices.Values) dev.Dispose();
        _devices.Clear();

        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            _bufferSize = 0;
        }
    }
}
