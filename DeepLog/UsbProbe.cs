// Where the pad sits on the USB tree, what else shares its controller and hub,
// which drivers are really in its device stack, and its power state. All from
// CfgMgr32 (DevNode.cs) + the Service Control Manager: no WMI, no admin, and
// nothing is ever cycled, disabled or restarted -- read-only.
//
// Instance IDs whose last segment is a device serial number are masked
// ("<serial>"): serials are hardware identifiers and are not sent.

using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

static class UsbProbe
{
    // ---- inventory of the whole USB tree -------------------------------------

    /// Unmasked instance ID of the pad found by the last Collect (never serialized).
    public static string? LastPadInstanceId;

    class Node
    {
        public uint Dev;
        public string Id = "";
        public string Path = "";            // "c0/p9/p1" = controller 0, root port 9, hub port 1
        public int Controller = -1;
        public int HubDepth;                // external hubs between this device and the root hub
    }

    /// Full USB inventory plus, if padVidPid matches a present device, its placement.
    public static Dictionary<string, object?> Collect(string? padVidPid)
    {
        var res = new Dictionary<string, object?>();
        var controllers = new List<Dictionary<string, object?>>();
        var devices = new List<Dictionary<string, object?>>();
        var nodes = new List<Node>();

        // Root hubs are USB-enumerated; their parents are the host controllers.
        var rootHubs = DevNode.PresentUsb().Where(id => id.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var rh in rootHubs)
        {
            uint rhDev = DevNode.Locate(rh);
            uint ctrl = DevNode.Parent(rhDev);
            if (rhDev == 0 || ctrl == 0) continue;
            int ci = controllers.Count;
            controllers.Add(DescribeController(ctrl, ci, rh));
            Walk(rhDev, $"c{ci}", ci, 0, nodes);
        }

        foreach (var n in nodes)
        {
            var st = DevNode.Status(n.Dev);
            devices.Add(new Dictionary<string, object?>
            {
                ["path"] = n.Path,
                ["vidPid"] = DevNode.VidPid(n.Id),
                ["interface"] = Regex.Match(n.Id, @"&MI_([0-9A-Fa-f]{2})") is { Success: true } m ? m.Groups[1].Value : null,
                ["name"] = DevNode.Name(n.Dev),
                ["product"] = DevNode.Str(n.Dev, DevNode.BusReportedDeviceDesc),
                ["class"] = DevNode.Str(n.Dev, DevNode.Class),
                ["service"] = DevNode.Str(n.Dev, DevNode.Service),
                ["hubDepth"] = n.HubDepth,
                ["problem"] = st?.Problem ?? 0,
                ["instanceId"] = MaskSerial(n.Id),
            });
        }
        res["usbHostControllers"] = controllers;
        res["usbDevices"] = devices;

        // ---- the pad -----------------------------------------------------------
        Node? pad = FindPad(nodes, padVidPid, out string how);
        LastPadInstanceId = pad?.Id;
        res["padMatch"] = how;
        if (pad != null) res["padPlacement"] = Placement(pad, nodes, controllers);
        return res;
    }

    static void Walk(uint dev, string path, int ci, int depth, List<Node> nodes)
    {
        foreach (uint c in DevNode.Children(dev))
        {
            string id = DevNode.Id(c);
            if (!id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) continue;   // HID/XUSB children etc.
            bool iface = id.Contains("&MI_", StringComparison.OrdinalIgnoreCase);
            string p = iface
                ? $"{path}/if{Regex.Match(id, @"&MI_([0-9A-Fa-f]{2})").Groups[1].Value}"
                : $"{path}/p{DevNode.U32(c, DevNode.Address)?.ToString() ?? "?"}";
            var n = new Node { Dev = c, Id = id, Path = p, Controller = ci, HubDepth = depth };
            nodes.Add(n);
            bool isHub = string.Equals(DevNode.Str(c, DevNode.Class), "USB", StringComparison.OrdinalIgnoreCase)
                         && (DevNode.Str(c, DevNode.Service) ?? "").StartsWith("USBHUB", StringComparison.OrdinalIgnoreCase);
            Walk(c, p, ci, isHub ? depth + 1 : depth, nodes);
        }
    }

    static Node? FindPad(List<Node> nodes, string? vidPid, out string how)
    {
        var devs = nodes.Where(n => !n.Id.Contains("&MI_", StringComparison.OrdinalIgnoreCase)).ToList();
        if (vidPid != null && vidPid != "OTHER")
        {
            var hit = devs.Where(n => DevNode.VidPid(n.Id) == vidPid).ToList();
            how = hit.Count switch { 0 => $"{vidPid} not on the USB tree", 1 => $"by VID:PID {vidPid}",
                _ => $"by VID:PID {vidPid} ({hit.Count} present, first taken)" };
            return hit.FirstOrDefault();
        }
        // Unknown XInput pad: the USB device whose subtree carries the XInput driver.
        var xin = devs.Where(n => SubtreeServices(n.Dev, 3).Any(s =>
            s.Equals("xusb22", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("dc1-controller", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("xboxgip", StringComparison.OrdinalIgnoreCase))).ToList();
        how = xin.Count switch { 0 => "no XInput device on the USB tree", 1 => "by XInput driver (xusb22) in subtree",
            _ => $"by XInput driver ({xin.Count} candidates, first taken)" };
        return xin.FirstOrDefault();
    }

    static IEnumerable<string> SubtreeServices(uint dev, int depth)
    {
        if (DevNode.Str(dev, DevNode.Service) is string s) yield return s;
        if (depth == 0) yield break;
        foreach (uint c in DevNode.Children(dev))
            foreach (var x in SubtreeServices(c, depth - 1)) yield return x;
    }

    static Dictionary<string, object?> Placement(Node pad, List<Node> nodes, List<Dictionary<string, object?>> controllers)
    {
        var chain = new List<Dictionary<string, object?>>();    // pad -> hubs -> root hub
        uint d = pad.Dev;
        while (d != 0)
        {
            uint parent = DevNode.Parent(d);
            if (parent == 0) break;
            string pid = DevNode.Id(parent);
            chain.Add(PowerInfo(d, DevNode.Id(d), port: DevNode.U32(d, DevNode.Address)));
            if (pid.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase))
            {
                chain.Add(PowerInfo(parent, pid, port: null));
                break;
            }
            if (!pid.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) break;
            d = parent;
        }
        var ctrl = pad.Controller >= 0 && pad.Controller < controllers.Count ? controllers[pad.Controller] : null;
        string padPath = pad.Path;
        string hubPrefix = padPath[..padPath.LastIndexOf('/')];
        string ctrlPrefix = $"c{pad.Controller}/";

        return new Dictionary<string, object?>
        {
            ["path"] = padPath,
            ["hubLevels"] = pad.HubDepth,
            ["directOnRootPort"] = pad.HubDepth == 0,
            ["controller"] = pad.Controller,
            ["controllerName"] = ctrl?["name"],
            ["controllerPlacement"] = ctrl?["placement"],
            ["chainPadToRootHub"] = chain,
            ["sameHub"] = nodes.Where(n => n != pad && !n.Path.StartsWith(padPath + "/") &&
                              n.Path.StartsWith(hubPrefix + "/") && n.Path.Count(ch => ch == '/') == padPath.Count(ch => ch == '/'))
                          .Select(n => $"{n.Path} {DevNode.VidPid(n.Id)} {DevNode.Name(n.Dev)}").ToList(),
            ["sameController"] = nodes.Where(n => n != pad && n.Path.StartsWith(ctrlPrefix) &&
                              !n.Path.StartsWith(padPath + "/") && !n.Id.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
                          .Select(n => $"{n.Path} {DevNode.VidPid(n.Id)} {DevNode.Name(n.Dev)}").ToList(),
            ["deviceStack"] = StackTree(pad.Dev, 3),
        };
    }

    // ---- power: D-state + per-device selective-suspend flags -------------------

    static readonly Regex PowerValue = new("Suspend|Idle|PowerManagement|D3|WaitWake|RemoteWake", RegexOptions.IgnoreCase);

    static Dictionary<string, object?> PowerInfo(uint dev, string id, uint? port) => new()
    {
        ["instanceId"] = MaskSerial(id),
        ["name"] = DevNode.Name(dev),
        ["port"] = port,
        ["dState"] = DevNode.DState(dev),
        ["deviceParameters"] = DevNode.DeviceParameters(dev, n => PowerValue.IsMatch(n)),
    };

    // ---- device stack: which drivers are really loaded on the pad --------------

    static List<Dictionary<string, object?>> StackTree(uint dev, int depth)
    {
        var list = new List<Dictionary<string, object?>>();
        void Rec(uint d, int left)
        {
            list.Add(new Dictionary<string, object?>
            {
                ["instanceId"] = MaskSerial(DevNode.Id(d)),
                ["class"] = DevNode.Str(d, DevNode.Class),
                ["service"] = DevNode.Str(d, DevNode.Service),
                // DEVPKEY_Device_Stack is the live stack, top first. UpperFilters alone
                // reads empty when the filter is a class filter (HidHide), so both are kept.
                ["stack"] = DevNode.StrList(d, DevNode.Stack),
                ["upperFilters"] = DevNode.StrList(d, DevNode.UpperFilters),
                ["lowerFilters"] = DevNode.StrList(d, DevNode.LowerFilters),
                ["dState"] = DevNode.DState(d),
                ["deviceParameters"] = DevNode.DeviceParameters(d, n => PowerValue.IsMatch(n)),
            });
            if (left > 0) foreach (uint c in DevNode.Children(d)) Rec(c, left - 1);
        }
        Rec(dev, depth);
        return list;
    }

    /// Filter/remapper drivers found anywhere in the pad's live stack.
    public static List<string> ThirdPartyInStack(Dictionary<string, object?> usb)
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (usb.GetValueOrDefault("padPlacement") is Dictionary<string, object?> pl &&
            pl["deviceStack"] is List<Dictionary<string, object?>> st)
            foreach (var n in st)
                foreach (var s in (List<string>)n["stack"]! )
                    if (Regex.IsMatch(s, "HidHide|ViGEm|USBPcap|reWASD|nefcon|Wireshark|vjoy|ds4", RegexOptions.IgnoreCase))
                        found.Add(s);
        return found.ToList();
    }

    // ---- host controllers: CPU-direct vs chipset --------------------------------

    static Dictionary<string, object?> DescribeController(uint ctrl, int index, string rootHubId)
    {
        string id = DevNode.Id(ctrl);
        var paths = DevNode.StrList(ctrl, DevNode.LocationPaths);
        var bridges = new List<string>();
        for (uint p = DevNode.Parent(ctrl); p != 0; p = DevNode.Parent(p))
        {
            string pid = DevNode.Id(p);
            if (!pid.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)) break;
            bridges.Add($"{PciIds(pid)} {DevNode.Name(p)}");
        }
        string pciPath = paths.FirstOrDefault(x => x.StartsWith("PCIROOT", StringComparison.OrdinalIgnoreCase)) ?? "";
        var (placement, basis) = Classify(id, pciPath);
        return new Dictionary<string, object?>
        {
            ["index"] = index,
            ["name"] = DevNode.Name(ctrl),
            ["pciIds"] = PciIds(id),
            ["instanceId"] = id,
            ["service"] = DevNode.Str(ctrl, DevNode.Service),
            ["locationPath"] = pciPath,
            ["locationInfo"] = DevNode.Str(ctrl, DevNode.LocationInfo),
            ["pciBus"] = DevNode.U32(ctrl, DevNode.BusNumber),
            ["bridgesToRoot"] = bridges,          // nearest first
            ["placement"] = placement,            // cpu | chipset | add-in | unknown
            ["placementBasis"] = basis,
            ["rootHub"] = rootHubId,
        };
    }

    static string PciIds(string id)
    {
        var m = Regex.Match(id, @"VEN_([0-9A-Fa-f]{4})&DEV_([0-9A-Fa-f]{4})");
        return m.Success ? $"{m.Groups[1].Value.ToUpperInvariant()}:{m.Groups[2].Value.ToUpperInvariant()}" : id.Split('\\')[0];
    }

    // Silicon table from deepscan engine/modules/h_hardware.py _SILICON (verified IDs).
    static readonly Dictionary<string, (string Placement, string What)> Silicon = new()
    {
        ["8086:7F6E"] = ("chipset", "Intel 800-series (Z890 / Arrow Lake PCH) xHCI"),
        ["8086:7EC0"] = ("cpu", "Intel Arrow Lake SoC CPU-die xHCI"),
        ["8086:7EC2"] = ("cpu", "Intel Arrow Lake SoC CPU-die USB4 host router"),
        ["8086:A71C"] = ("chipset", "Intel 600/700-series PCH xHCI"),
        ["8086:7AE0"] = ("chipset", "Intel 600-series (Z690) PCH xHCI"),
        ["8086:51ED"] = ("chipset", "Intel Alder Lake-P PCH xHCI"),
        ["8086:43ED"] = ("chipset", "Intel 500-series (Z590) PCH xHCI"),
        ["8086:A36D"] = ("chipset", "Intel 300-series (Z390) PCH xHCI"),
        ["8086:A2AF"] = ("chipset", "Intel 200-series (Z270) PCH xHCI"),
        ["8086:9DED"] = ("chipset", "Intel Cannon Point-LP PCH xHCI"),
        ["8086:1E31"] = ("chipset", "Intel 7-series PCH xHCI"),
        ["8086:8C31"] = ("chipset", "Intel 8/9-series (Z87/Z97) PCH xHCI"),
        ["8086:15EB"] = ("add-in", "Intel JHL7540 Titan Ridge discrete Thunderbolt 3 xHCI"),
        ["8086:15DB"] = ("add-in", "Intel JHL6540 Alpine Ridge discrete Thunderbolt 3 xHCI"),
        ["8086:1137"] = ("add-in", "Intel Maple Ridge discrete Thunderbolt 4 xHCI"),
        ["1022:43D5"] = ("chipset", "AMD 400-series (X470/B450) Promontory xHCI"),
        ["1022:43EE"] = ("chipset", "AMD 500-series (X570/B550) Promontory xHCI"),
        ["1022:43F7"] = ("chipset", "AMD 600-series (X670/B650) xHCI"),
        ["1022:15B6"] = ("cpu", "AMD Ryzen (Zen) SoC CPU-die xHCI"),
        ["1022:15B7"] = ("cpu", "AMD Ryzen (Zen) SoC CPU-die xHCI"),
        ["1022:15C1"] = ("cpu", "AMD Ryzen (Matisse/Vermeer) SoC CPU-die xHCI"),
        ["1022:161A"] = ("cpu", "AMD Ryzen (Raphael/Zen4) SoC CPU-die xHCI"),
        ["1912:0014"] = ("add-in", "Renesas uPD720201 xHCI"),
        ["1912:0015"] = ("add-in", "Renesas uPD720202 xHCI"),
        ["1B73:1100"] = ("add-in", "Fresco Logic FL1100 xHCI"),
        ["1106:3483"] = ("add-in", "VIA VL805 xHCI"),
    };

    /// Table first (deepscan's verified IDs), then a labelled location/vendor rule.
    /// Raw facts (pciIds, locationPath, bridges) are always recorded next to it so
    /// a wrong guess can be re-decided later.
    static (string, string) Classify(string id, string pciPath)
    {
        string ids = PciIds(id);
        if (Silicon.TryGetValue(ids, out var s)) return (s.Placement, $"table: {s.What}");
        if (ids.StartsWith("1B21:")) return ("add-in", "table: ASMedia add-in xHCI");
        var hops = Regex.Matches(pciPath, @"#PCI\(([0-9A-Fa-f]{2})([0-9A-Fa-f]{2})\)")
                        .Select(m => (dev: Convert.ToInt32(m.Groups[1].Value, 16), fn: Convert.ToInt32(m.Groups[2].Value, 16))).ToList();
        string vendor = ids.Length >= 4 ? ids[..4] : "";
        string dev = ids.Length >= 9 ? ids[5..9] : "";

        if (vendor == "8086" && hops.Count == 1)
        {
            if (hops[0].dev == 0x14) return ("chipset", $"heuristic: Intel {ids} at 00:14.0, the PCH/SoC xHCI slot");
            if (hops[0].dev == 0x0D) return ("cpu", $"heuristic: Intel {ids} at 00:0D.0, the CPU-die TCSS xHCI slot");
        }
        if (vendor == "1022")
        {
            if (dev.StartsWith("43")) return ("chipset", $"heuristic: AMD {ids}, Promontory chipset device-id family");
            if (dev.StartsWith("14") || dev.StartsWith("15") || dev.StartsWith("16"))
                return ("cpu", $"heuristic: AMD {ids}, CPU/SoC-integrated device-id family");
        }
        if (vendor is "1912" or "1106" or "1B73" or "104C" or "1B6F" && hops.Count > 1)
            return ("add-in", $"heuristic: {ids} behind {hops.Count - 1} PCI bridge(s), discrete USB controller (see bridgesToRoot)");
        return ("unknown", $"{ids} at {pciPath}: no rule matched");
    }

    // ---- driver services: running vs merely installed (SCM, no WMI) -------------

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenSCManagerW(string? machine, string? db, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool QueryServiceStatus(IntPtr svc, out SERVICE_STATUS status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryServiceConfigW(IntPtr svc, IntPtr cfg, int size, out int needed);
    [DllImport("advapi32.dll")] static extern bool CloseServiceHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    struct SERVICE_STATUS { public uint Type, State, ControlsAccepted, Win32Exit, SvcExit, Checkpoint, WaitHint; }

    static readonly string[] StateNames = { "?", "stopped", "start-pending", "stop-pending", "running",
        "continue-pending", "pause-pending", "paused" };
    static readonly string[] StartNames = { "boot", "system", "auto", "demand", "disabled" };

    public static Dictionary<string, object?> Services(params string[] names)
    {
        var res = new Dictionary<string, object?>();
        IntPtr scm = OpenSCManagerW(null, null, 0x0001);              // SC_MANAGER_CONNECT
        if (scm == IntPtr.Zero) { res["error"] = $"OpenSCManager failed ({Marshal.GetLastWin32Error()})"; return res; }
        try
        {
            foreach (var n in names)
            {
                IntPtr s = OpenServiceW(scm, n, 0x0004 | 0x0001);     // QUERY_STATUS | QUERY_CONFIG
                if (s == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    res[n] = new Dictionary<string, object?> { ["state"] = err == 1060 ? "not-installed" : $"unreadable ({err})" };
                    continue;
                }
                try
                {
                    var info = new Dictionary<string, object?>();
                    info["state"] = QueryServiceStatus(s, out var st) && st.State < StateNames.Length ? StateNames[st.State] : "unknown";
                    QueryServiceConfigW(s, IntPtr.Zero, 0, out int need);
                    if (need > 0)
                    {
                        IntPtr buf = Marshal.AllocHGlobal(need);
                        try
                        {
                            if (QueryServiceConfigW(s, buf, need, out _))
                            {
                                uint start = (uint)Marshal.ReadInt32(buf, 4);
                                info["startType"] = start < StartNames.Length ? StartNames[start] : start.ToString();
                                string? bin = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf, 16));
                                info["binary"] = bin;
                                string? ver = DriverFileVersion(bin);
                                if (ver != null) info["fileVersion"] = ver;
                            }
                        }
                        finally { Marshal.FreeHGlobal(buf); }
                    }
                    res[n] = info;
                }
                finally { CloseServiceHandle(s); }
            }
        }
        finally { CloseServiceHandle(scm); }
        return res;
    }

    static string? DriverFileVersion(string? bin)
    {
        if (string.IsNullOrEmpty(bin)) return null;
        string p = bin.Trim('"');
        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), p[12..]);
        else if (p.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase))
            p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), p);
        else if (p.StartsWith(@"\??\")) p = p[4..];
        try { return File.Exists(p) ? System.Diagnostics.FileVersionInfo.GetVersionInfo(p).FileVersion : null; }
        catch { return null; }
    }

    // ---- helpers -----------------------------------------------------------------

    /// "USB\VID_0781&PID_5581\0401A6..." -> "USB\VID_0781&PID_5581\<serial>". Windows-made
    /// instance suffixes contain '&' ("6&35fba998&0&3") and are kept.
    public static string MaskSerial(string id)
    {
        int i = id.LastIndexOf('\\');
        if (i < 0 || !id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) return id;
        string tail = id[(i + 1)..];
        return tail.Contains('&') ? id : id[..(i + 1)] + "<serial>";
    }
}
