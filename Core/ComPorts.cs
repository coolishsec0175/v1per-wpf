using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace V1Per;

/// <summary>A serial COM port with USB identity (VID/PID/description).</summary>
public sealed record ComPortInfo(string Device, ushort? Vid, ushort? Pid, string Description);

/// <summary>
/// Enumerates COM ports via Windows SetupAPI so we get VID/PID + description,
/// which <see cref="System.IO.Ports.SerialPort.GetPortNames"/> alone cannot provide.
/// </summary>
public static class ComPorts
{
    private static Guid GuidPorts = new("4D36E978-E325-11CE-BFC1-08002BE10318");

    private const uint DigcfPresent = 0x00000002;
    private const uint SpdrpFriendlyName = 0x0000000C;
    private const uint SpdrpHardwareId = 0x00000001;

    public static List<ComPortInfo> Enumerate()
    {
        var result = new List<ComPortInfo>();
        IntPtr devInfoSet = SetupDiGetClassDevs(ref GuidPorts, IntPtr.Zero, IntPtr.Zero, DigcfPresent);
        if (devInfoSet == new IntPtr(-1))
            return result;

        try
        {
            uint index = 0;
            while (true)
            {
                var info = new SpDevInfoData { CbSize = (uint)Marshal.SizeOf<SpDevInfoData>() };
                if (!SetupDiEnumDeviceInfo(devInfoSet, index, ref info))
                    break;
                index++;

                string? friendly = GetProp(devInfoSet, ref info, SpdrpFriendlyName);
                string? hardwareId = GetProp(devInfoSet, ref info, SpdrpHardwareId);

                string? port = ParseComPort(friendly);
                if (port is null)
                    continue;

                (ushort? vid, ushort? pid) = ParseVidPid(hardwareId);
                result.Add(new ComPortInfo(port, vid, pid, friendly ?? string.Empty));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfoSet);
        }

        return result;
    }

    private static string? GetProp(IntPtr devInfoSet, ref SpDevInfoData info, uint prop)
    {
        uint required = 0;
        SetupDiGetDeviceRegistryProperty(devInfoSet, ref info, prop, IntPtr.Zero, null, 0, out required);
        if (required == 0)
            return null;

        byte[] buffer = new byte[required];
        if (!SetupDiGetDeviceRegistryProperty(devInfoSet, ref info, prop, IntPtr.Zero, buffer, required, out _))
            return null;

        string text = Encoding.Unicode.GetString(buffer);
        int nul = text.IndexOf('\0');
        return nul >= 0 ? text[..nul] : text;
    }

    /// <summary>Pulls "COM3" out of a friendly name like "MediaTek PreLoader USB VCOM Port (COM3)".</summary>
    private static string? ParseComPort(string? friendly)
    {
        if (string.IsNullOrEmpty(friendly))
            return null;

        int open = friendly.LastIndexOf("(COM", StringComparison.OrdinalIgnoreCase);
        if (open < 0)
            return null;
        int close = friendly.IndexOf(')', open);
        if (close < 0)
            return null;

        return friendly[(open + 1)..close];
    }

    /// <summary>Extracts VID_xxxx / PID_xxxx from a hardware ID string.</summary>
    private static (ushort? Vid, ushort? Pid) ParseVidPid(string? hardwareId)
    {
        if (string.IsNullOrEmpty(hardwareId))
            return (null, null);

        ushort? vid = TryHex(hardwareId, "VID_");
        ushort? pid = TryHex(hardwareId, "PID_");
        return (vid, pid);
    }

    private static ushort? TryHex(string text, string prefix)
    {
        int idx = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0 || idx + prefix.Length + 4 > text.Length)
            return null;
        if (ushort.TryParse(
                text.Substring(idx + prefix.Length, 4),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out ushort value))
            return value;
        return null;
    }

    // ── SetupAPI P/Invoke ────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid,
        IntPtr enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr devInfoSet,
        uint memberIndex,
        ref SpDevInfoData devInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr devInfoSet,
        ref SpDevInfoData devInfoData,
        uint property,
        IntPtr propertyRegDataType,
        byte[]? propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfoSet);
}