using System.Runtime.InteropServices;

namespace WinWingOverlay;

internal enum HidpReportType
{
    Input = 0,
    Output = 1,
    Feature = 2
}

[StructLayout(LayoutKind.Sequential)]
internal struct HIDP_CAPS
{
    public ushort Usage;
    public ushort UsagePage;
    public ushort InputReportByteLength;
    public ushort OutputReportByteLength;
    public ushort FeatureReportByteLength;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
    public ushort[] Reserved;
    public ushort NumberLinkCollectionNodes;
    public ushort NumberInputButtonCaps;
    public ushort NumberInputValueCaps;
    public ushort NumberInputDataIndices;
    public ushort NumberOutputButtonCaps;
    public ushort NumberOutputValueCaps;
    public ushort NumberOutputDataIndices;
    public ushort NumberFeatureButtonCaps;
    public ushort NumberFeatureValueCaps;
    public ushort NumberFeatureDataIndices;
}

/// <summary>
/// The trailing union is declared as its Range layout. When IsRange is false the
/// NotRange.Usage field occupies the same offset as Range.UsageMin, so reading
/// UsageMin is correct either way.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct HIDP_BUTTON_CAPS
{
    public ushort UsagePage;
    public byte ReportID;
    [MarshalAs(UnmanagedType.U1)] public bool IsAlias;
    public ushort BitField;
    public ushort LinkCollection;
    public ushort LinkUsage;
    public ushort LinkUsagePage;
    [MarshalAs(UnmanagedType.U1)] public bool IsRange;
    [MarshalAs(UnmanagedType.U1)] public bool IsStringRange;
    [MarshalAs(UnmanagedType.U1)] public bool IsDesignatorRange;
    [MarshalAs(UnmanagedType.U1)] public bool IsAbsolute;
    public ushort ReportCount;
    public ushort Reserved2;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
    public uint[] Reserved;
    public ushort UsageMin;
    public ushort UsageMax;
    public ushort StringMin;
    public ushort StringMax;
    public ushort DesignatorMin;
    public ushort DesignatorMax;
    public ushort DataIndexMin;
    public ushort DataIndexMax;
}

[StructLayout(LayoutKind.Sequential)]
internal struct HIDP_VALUE_CAPS
{
    public ushort UsagePage;
    public byte ReportID;
    [MarshalAs(UnmanagedType.U1)] public bool IsAlias;
    public ushort BitField;
    public ushort LinkCollection;
    public ushort LinkUsage;
    public ushort LinkUsagePage;
    [MarshalAs(UnmanagedType.U1)] public bool IsRange;
    [MarshalAs(UnmanagedType.U1)] public bool IsStringRange;
    [MarshalAs(UnmanagedType.U1)] public bool IsDesignatorRange;
    [MarshalAs(UnmanagedType.U1)] public bool IsAbsolute;
    [MarshalAs(UnmanagedType.U1)] public bool HasNull;
    public byte Reserved;
    public ushort BitSize;
    public ushort ReportCount;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
    public ushort[] Reserved2;
    public uint UnitsExp;
    public uint Units;
    public int LogicalMin;
    public int LogicalMax;
    public int PhysicalMin;
    public int PhysicalMax;
    public ushort UsageMin;
    public ushort UsageMax;
    public ushort StringMin;
    public ushort StringMax;
    public ushort DesignatorMin;
    public ushort DesignatorMax;
    public ushort DataIndexMin;
    public ushort DataIndexMax;
}

internal static class Hid
{
    public const int HIDP_STATUS_SUCCESS = unchecked((int)0x00110000);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern int HidP_GetCaps(IntPtr preparsedData, ref HIDP_CAPS capabilities);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern int HidP_GetButtonCaps(HidpReportType reportType,
        [Out] HIDP_BUTTON_CAPS[] buttonCaps, ref ushort buttonCapsLength, IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern int HidP_GetValueCaps(HidpReportType reportType,
        [Out] HIDP_VALUE_CAPS[] valueCaps, ref ushort valueCapsLength, IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern int HidP_GetUsages(HidpReportType reportType, ushort usagePage, ushort linkCollection,
        [Out] ushort[] usageList, ref uint usageLength, IntPtr preparsedData, byte[] report, uint reportLength);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern int HidP_GetUsageValue(HidpReportType reportType, ushort usagePage, ushort linkCollection,
        ushort usage, out uint usageValue, IntPtr preparsedData, byte[] report, uint reportLength);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern int HidP_MaxUsageListLength(HidpReportType reportType, ushort usagePage, IntPtr preparsedData);
}
