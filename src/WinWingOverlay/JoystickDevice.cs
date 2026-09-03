using System.Runtime.InteropServices;
using System.Text;

namespace WinWingOverlay;

internal sealed class AxisInfo
{
    public ushort UsagePage;
    public ushort Usage;
    public int LogicalMin;
    public int LogicalMax;
    public ushort BitSize;
    public ushort LinkCollection;

    public string Name => NameFor(Usage);

    public static string NameFor(ushort usage) => usage switch
    {
        Native.USAGE_X => "X",
        Native.USAGE_Y => "Y",
        Native.USAGE_Z => "Z",
        Native.USAGE_RX => "RX",
        Native.USAGE_RY => "RY",
        Native.USAGE_RZ => "RZ",
        Native.USAGE_SLIDER => "Slider",
        Native.USAGE_DIAL => "Dial",
        Native.USAGE_WHEEL => "Wheel",
        Native.USAGE_HATSWITCH => "Hat",
        _ => $"0x{usage:X2}"
    };
}

/// <summary>Live values for one device. Mutated on the UI thread from WM_INPUT.</summary>
internal sealed class DeviceState
{
    public readonly Dictionary<ushort, double> Axis = new();     // usage -> 0..1
    public readonly Dictionary<ushort, int> AxisRaw = new();     // usage -> raw logical value
    public readonly HashSet<int> Buttons = new();                // 1-based button numbers
    public int Hat = -1;                                         // -1 = centred, else 0..7 clockwise from north
    public int HighestButtonSeen;
    public long Packets;
}

internal sealed class JoystickDevice : IDisposable
{
    public IntPtr Handle { get; }
    public string DevicePath { get; }
    public string ProductName { get; private set; } = "";
    public uint VendorId { get; }
    public uint ProductId { get; }
    public int DeclaredButtonCount { get; private set; }
    public List<AxisInfo> Axes { get; } = new();
    public DeviceState State { get; } = new();

    private IntPtr _preparsed;
    private readonly List<ushort> _buttonPages = new();
    private ushort[] _usageScratch = Array.Empty<ushort>();

    private JoystickDevice(IntPtr handle, string devicePath, uint vid, uint pid)
    {
        Handle = handle;
        DevicePath = devicePath;
        VendorId = vid;
        ProductId = pid;
    }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(ProductName) ? $"HID {VendorId:X4}:{ProductId:X4}" : ProductName;

    /// <summary>Enumerate every HID joystick / gamepad currently attached.</summary>
    public static List<JoystickDevice> Enumerate()
    {
        var result = new List<JoystickDevice>();
        uint count = 0;
        uint listSize = (uint)Marshal.SizeOf<Native.RAWINPUTDEVICELIST>();

        if (Native.GetRawInputDeviceList(null, ref count, listSize) == unchecked((uint)-1) || count == 0)
            return result;

        var list = new Native.RAWINPUTDEVICELIST[count];
        if (Native.GetRawInputDeviceList(list, ref count, listSize) == unchecked((uint)-1))
            return result;

        for (int i = 0; i < count; i++)
        {
            if (list[i].dwType != Native.RIM_TYPEHID) continue;

            var info = new Native.RID_DEVICE_INFO { cbSize = (uint)Marshal.SizeOf<Native.RID_DEVICE_INFO>() };
            uint infoSize = info.cbSize;
            IntPtr infoPtr = Marshal.AllocHGlobal((int)infoSize);
            try
            {
                Marshal.StructureToPtr(info, infoPtr, false);
                if (Native.GetRawInputDeviceInfo(list[i].hDevice, Native.RIDI_DEVICEINFO, infoPtr, ref infoSize) == unchecked((uint)-1))
                    continue;
                info = Marshal.PtrToStructure<Native.RID_DEVICE_INFO>(infoPtr);
            }
            finally { Marshal.FreeHGlobal(infoPtr); }

            if (info.hid.usUsagePage != Native.USAGE_PAGE_GENERIC) continue;
            if (info.hid.usUsage != Native.USAGE_JOYSTICK &&
                info.hid.usUsage != Native.USAGE_GAMEPAD &&
                info.hid.usUsage != Native.USAGE_MULTI_AXIS) continue;

            string path = GetDevicePath(list[i].hDevice);
            var dev = new JoystickDevice(list[i].hDevice, path, info.hid.dwVendorId, info.hid.dwProductId);
            if (dev.LoadCapabilities())
            {
                dev.ProductName = ReadProductString(path);
                result.Add(dev);
            }
            else
            {
                dev.Dispose();
            }
        }

        return result;
    }

    private static string GetDevicePath(IntPtr hDevice)
    {
        uint size = 0;
        if (Native.GetRawInputDeviceInfoStr(hDevice, Native.RIDI_DEVICENAME, null, ref size) != 0 || size == 0)
            return "";
        var sb = new StringBuilder((int)size + 1);
        if (Native.GetRawInputDeviceInfoStr(hDevice, Native.RIDI_DEVICENAME, sb, ref size) == unchecked((uint)-1))
            return "";
        return sb.ToString();
    }

    private static string ReadProductString(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        // Zero desired-access opens the device for metadata only; it never takes exclusive
        // ownership and never reads reports behind the running game.
        IntPtr h = Native.CreateFileW(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == IntPtr.Zero || h == new IntPtr(-1)) return "";
        try
        {
            var sb = new StringBuilder(256);
            return Native.HidD_GetProductString(h, sb, sb.Capacity * 2) ? sb.ToString().Trim() : "";
        }
        finally { Native.CloseHandle(h); }
    }

    private bool LoadCapabilities()
    {
        uint size = 0;
        if (Native.GetRawInputDeviceInfo(Handle, Native.RIDI_PREPARSEDDATA, IntPtr.Zero, ref size) != 0 || size == 0)
            return false;

        _preparsed = Marshal.AllocHGlobal((int)size);
        if (Native.GetRawInputDeviceInfo(Handle, Native.RIDI_PREPARSEDDATA, _preparsed, ref size) == unchecked((uint)-1))
            return false;

        var caps = new HIDP_CAPS();
        if (Hid.HidP_GetCaps(_preparsed, ref caps) != Hid.HIDP_STATUS_SUCCESS)
            return false;

        if (caps.NumberInputButtonCaps > 0)
        {
            var buttonCaps = new HIDP_BUTTON_CAPS[caps.NumberInputButtonCaps];
            ushort len = caps.NumberInputButtonCaps;
            if (Hid.HidP_GetButtonCaps(HidpReportType.Input, buttonCaps, ref len, _preparsed) == Hid.HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < len; i++)
                {
                    var bc = buttonCaps[i];
                    if (!_buttonPages.Contains(bc.UsagePage)) _buttonPages.Add(bc.UsagePage);
                    if (bc.UsagePage == Native.USAGE_PAGE_BUTTON)
                    {
                        int hi = bc.IsRange ? bc.UsageMax : bc.UsageMin;
                        if (hi > DeclaredButtonCount) DeclaredButtonCount = hi;
                    }
                }
            }
        }

        if (caps.NumberInputValueCaps > 0)
        {
            var valueCaps = new HIDP_VALUE_CAPS[caps.NumberInputValueCaps];
            ushort len = caps.NumberInputValueCaps;
            if (Hid.HidP_GetValueCaps(HidpReportType.Input, valueCaps, ref len, _preparsed) == Hid.HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < len; i++)
                {
                    var vc = valueCaps[i];
                    ushort first = vc.UsageMin;
                    ushort last = vc.IsRange ? vc.UsageMax : vc.UsageMin;
                    for (ushort u = first; u <= last; u++)
                    {
                        if (Axes.Any(a => a.Usage == u && a.UsagePage == vc.UsagePage)) continue;
                        Axes.Add(new AxisInfo
                        {
                            UsagePage = vc.UsagePage,
                            Usage = u,
                            LogicalMin = vc.LogicalMin,
                            LogicalMax = vc.LogicalMax,
                            BitSize = vc.BitSize,
                            LinkCollection = vc.LinkCollection
                        });
                        if (u == ushort.MaxValue) break;
                    }
                }
            }
        }

        int maxUsageList = 0;
        foreach (var page in _buttonPages)
        {
            int n = Hid.HidP_MaxUsageListLength(HidpReportType.Input, page, _preparsed);
            if (n > maxUsageList) maxUsageList = n;
        }
        _usageScratch = new ushort[Math.Max(maxUsageList, 1)];

        return true;
    }

    /// <summary>Decode one raw HID input report into <see cref="State"/>.</summary>
    public void ParseReport(byte[] report, int length)
    {
        if (_preparsed == IntPtr.Zero) return;

        State.Buttons.Clear();
        foreach (var page in _buttonPages)
        {
            uint len = (uint)_usageScratch.Length;
            if (len == 0) continue;
            if (Hid.HidP_GetUsages(HidpReportType.Input, page, 0, _usageScratch, ref len, _preparsed, report, (uint)length)
                != Hid.HIDP_STATUS_SUCCESS) continue;
            for (int i = 0; i < len; i++)
            {
                int btn = _usageScratch[i];
                State.Buttons.Add(btn);
                if (btn > State.HighestButtonSeen) State.HighestButtonSeen = btn;
            }
        }

        foreach (var axis in Axes)
        {
            if (Hid.HidP_GetUsageValue(HidpReportType.Input, axis.UsagePage, 0, axis.Usage,
                    out uint raw, _preparsed, report, (uint)length) != Hid.HIDP_STATUS_SUCCESS)
                continue;

            int value = Signed(raw, axis);
            State.AxisRaw[axis.Usage] = value;

            if (axis.Usage == Native.USAGE_HATSWITCH && axis.UsagePage == Native.USAGE_PAGE_GENERIC)
            {
                int span = axis.LogicalMax - axis.LogicalMin + 1;
                State.Hat = (span <= 0 || value < axis.LogicalMin || value > axis.LogicalMax)
                    ? -1
                    : (int)Math.Round((value - axis.LogicalMin) * 8.0 / span) % 8;
                continue;
            }

            State.Axis[axis.Usage] = Normalize(value, axis);
        }

        State.Packets++;
    }

    private static int Signed(uint raw, AxisInfo axis)
    {
        if (axis.LogicalMin >= 0 || axis.BitSize == 0 || axis.BitSize >= 32) return unchecked((int)raw);
        // Sign-extend from the declared field width when the descriptor uses a signed range.
        uint signBit = 1u << (axis.BitSize - 1);
        if ((raw & signBit) != 0)
            return unchecked((int)(raw | ~((1u << axis.BitSize) - 1)));
        return (int)raw;
    }

    private static double Normalize(int value, AxisInfo axis)
    {
        int min = axis.LogicalMin;
        int max = axis.LogicalMax;
        if (max <= min)
        {
            // Descriptors commonly report a 16-bit unsigned maximum as a negative signed value.
            min = 0;
            max = axis.BitSize is > 0 and < 32 ? (int)((1L << axis.BitSize) - 1) : 65535;
        }
        if (max <= min) return 0.5;
        return Math.Clamp((value - (double)min) / (max - min), 0.0, 1.0);
    }

    public void Dispose()
    {
        if (_preparsed != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_preparsed);
            _preparsed = IntPtr.Zero;
        }
    }
}
