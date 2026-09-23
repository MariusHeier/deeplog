// CfgMgr32 device-tree access. Replaces every WMI (Win32_PnPEntity) query v2.0
// made: no WMI, no admin. Property keys are from the Windows SDK devpkey.h.

using System.Runtime.InteropServices;
using System.Text;

static class DevNode
{
    const int CR_SUCCESS = 0, CR_BUFFER_SMALL = 0x1A;
    public const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x1, CM_GETIDLIST_FILTER_SERVICE = 0x2,
                      CM_GETIDLIST_FILTER_PRESENT = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_ID_List_SizeW(out uint len, string? filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_ID_ListW(string? filter, char[] buffer, uint len, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint devInst, string id, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_Child(out uint child, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_Sibling(out uint sib, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_IDW(uint devInst, char[] buf, uint len, uint flags);
    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type,
        byte[]? buf, ref uint size, uint flags);
    [DllImport("cfgmgr32.dll")]
    static extern int CM_Open_DevNode_Key(uint devInst, uint samDesired, uint profile, uint disposition,
        out IntPtr hkey, uint flags);

    static DEVPROPKEY K(string g, uint pid) => new() { fmtid = new Guid(g), pid = pid };
    const string DEV = "a45c254e-df1c-4efd-8020-67d146a850e0";
    const string DEV2 = "540b947e-8b40-45bc-a8a2-6a0b894cbda2";
    public static readonly DEVPROPKEY DeviceDesc = K(DEV, 2), Service = K(DEV, 6), Class = K(DEV, 9),
        Manufacturer = K(DEV, 13), FriendlyName = K(DEV, 14), LocationInfo = K(DEV, 15),
        UpperFilters = K(DEV, 19), LowerFilters = K(DEV, 20), BusNumber = K(DEV, 23),
        Address = K(DEV, 30), PowerData = K(DEV, 32), LocationPaths = K(DEV, 37),
        BusReportedDeviceDesc = K(DEV2, 4), Stack = K(DEV2, 14);

    /// Instance IDs of present devices, optionally filtered by enumerator ("USB") or service.
    public static List<string> List(string? filter, uint flags)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out uint len, filter, flags) != CR_SUCCESS) return new();
            var buf = new char[len];
            int cr = CM_Get_Device_ID_ListW(filter, buf, len, flags);
            if (cr == CR_BUFFER_SMALL) continue;        // device arrived between the two calls
            if (cr != CR_SUCCESS) return new();
            return MultiSz(buf, buf.Length);
        }
        return new();
    }

    public static List<string> PresentUsb() =>
        List("USB", CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT);

    public static uint Locate(string id) => CM_Locate_DevNodeW(out uint d, id, 0) == CR_SUCCESS ? d : 0;
    public static uint Parent(uint d) => CM_Get_Parent(out uint p, d, 0) == CR_SUCCESS ? p : 0;

    public static List<uint> Children(uint d)
    {
        var list = new List<uint>();
        if (CM_Get_Child(out uint c, d, 0) != CR_SUCCESS) return list;
        do list.Add(c); while (CM_Get_Sibling(out c, c, 0) == CR_SUCCESS);
        return list;
    }

    public static string Id(uint d)
    {
        var buf = new char[512];
        return CM_Get_Device_IDW(d, buf, (uint)buf.Length, 0) == CR_SUCCESS
            ? new string(buf, 0, Array.IndexOf(buf, '\0') is int i && i >= 0 ? i : buf.Length) : "";
    }

    public static (uint Status, uint Problem)? Status(uint d) =>
        CM_Get_DevNode_Status(out uint s, out uint p, d, 0) == CR_SUCCESS ? (s, p) : null;

    public static byte[]? Raw(uint d, DEVPROPKEY key)
    {
        uint size = 0;
        int cr = CM_Get_DevNode_PropertyW(d, ref key, out _, null, ref size, 0);
        if (cr != CR_BUFFER_SMALL || size == 0) return null;
        var buf = new byte[size];
        return CM_Get_DevNode_PropertyW(d, ref key, out _, buf, ref size, 0) == CR_SUCCESS ? buf : null;
    }

    public static string? Str(uint d, DEVPROPKEY key)
    {
        var b = Raw(d, key);
        return b == null ? null : Encoding.Unicode.GetString(b).TrimEnd('\0');
    }

    public static List<string> StrList(uint d, DEVPROPKEY key)
    {
        var b = Raw(d, key);
        if (b == null) return new();
        var chars = Encoding.Unicode.GetString(b).ToCharArray();
        return MultiSz(chars, chars.Length);
    }

    public static uint? U32(uint d, DEVPROPKEY key)
    {
        var b = Raw(d, key);
        return b != null && b.Length >= 4 ? BitConverter.ToUInt32(b, 0) : null;
    }

    /// FriendlyName, else DeviceDesc -- what Win32_PnPEntity.Name returned.
    public static string Name(uint d) => Str(d, FriendlyName) ?? Str(d, DeviceDesc) ?? "";

    /// CM_POWER_DATA: PD_Size, PD_MostRecentPowerState (1=D0 .. 4=D3), PD_Capabilities, ...
    public static string? DState(uint d)
    {
        var b = Raw(d, PowerData);
        if (b == null || b.Length < 8) return null;
        int s = BitConverter.ToInt32(b, 4);
        return s is >= 1 and <= 4 ? "D" + (s - 1) : "unspecified";
    }

    // Device Parameters key (CM_REGISTRY_HARDWARE), read-only.
    [DllImport("advapi32.dll")] static extern int RegCloseKey(IntPtr hkey);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RegEnumValueW(IntPtr hkey, uint index, char[] name, ref uint nameLen,
        IntPtr reserved, out uint type, byte[]? data, ref uint dataLen);

    const uint KEY_READ = 0x20019, RegDisposition_OpenExisting = 1, CM_REGISTRY_HARDWARE = 0;

    /// DWORD (and <=4-byte binary) values of the devnode's "Device Parameters" key whose names match `keep`.
    public static Dictionary<string, uint> DeviceParameters(uint d, Func<string, bool> keep)
    {
        var res = new Dictionary<string, uint>();
        if (CM_Open_DevNode_Key(d, KEY_READ, 0, RegDisposition_OpenExisting, out IntPtr hk,
                CM_REGISTRY_HARDWARE) != CR_SUCCESS) return res;
        try
        {
            var name = new char[256];
            var data = new byte[64];
            for (uint i = 0; ; i++)
            {
                uint nl = (uint)name.Length, dl = (uint)data.Length;
                int rc = RegEnumValueW(hk, i, name, ref nl, IntPtr.Zero, out uint type, data, ref dl);
                if (rc == 259) break;                             // ERROR_NO_MORE_ITEMS
                if (rc != 0) continue;                            // too long etc.
                string n = new string(name, 0, (int)nl);
                if (!keep(n)) continue;
                if (type == 4 && dl >= 4) res[n] = BitConverter.ToUInt32(data, 0);          // REG_DWORD
                else if (type == 3 && dl is > 0 and <= 4)                                   // small REG_BINARY
                {
                    uint v = 0;
                    for (int b = (int)dl - 1; b >= 0; b--) v = (v << 8) | data[b];
                    res[n] = v;
                }
            }
        }
        finally { RegCloseKey(hk); }
        return res;
    }

    static List<string> MultiSz(char[] buf, int len)
    {
        var list = new List<string>();
        int start = 0;
        for (int i = 0; i < len; i++)
        {
            if (buf[i] != '\0') continue;
            if (i == start) break;
            list.Add(new string(buf, start, i - start));
            start = i + 1;
        }
        return list;
    }

    // "USB\VID_39AE&PID_400A\..." -> "39AE:400A"
    public static string? VidPid(string id)
    {
        var m = System.Text.RegularExpressions.Regex.Match(id, @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})");
        return m.Success ? $"{m.Groups[1].Value.ToUpperInvariant()}:{m.Groups[2].Value.ToUpperInvariant()}" : null;
    }
}
