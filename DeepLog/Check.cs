// DeepLog v2.4 -- "Check": why does a game or Windows not see my controller?
//
// No recording. About a minute. It puts three views of the same moment side by side:
//   1. Windows' view of the device: the pad's USB node and every interface under it
//      (is the Xbox driver bound and started on interface 0, any problem code)
//   2. what the input APIs return while the person moves a stick: every XInput slot
//      through each installed xinput DLL (does the packet number advance?), or the
//      raw HID report stream in PS4 mode
//   3. the pad's own view: its settings and, on Seidr 0.9+, its count of reports the
//      PC took, read in the same seconds
// plus Windows' own device history for this pad (Kernel-PnP events and setupapi.dev.log
// for the last day), leftovers of other identities (usbflags, device nodes that are
// not present) and the usual system snapshot.
//
// Read-only: nothing is installed, changed or reset. No admin.

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

partial class Program
{
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] static extern uint XInput14(uint i, out XINPUT_STATE s);
    [DllImport("xinput1_3.dll", EntryPoint = "XInputGetState")] static extern uint XInput13(uint i, out XINPUT_STATE s);
    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")] static extern uint XInput910(uint i, out XINPUT_STATE s);

    delegate uint XInputGet(uint i, out XINPUT_STATE s);
    const double CheckWindowSec = 4.0;
    static readonly string[] PadIdPrefixes = { @"USB\VID_39AE", @"USB\VID_054C&PID_05C4", @"USB\VID_1A86&PID_1235" };

    // ---- 1. Windows' view of the device ---------------------------------------------

    static List<Dictionary<string, object?>> DeviceTree(string? padInstanceId)
    {
        var rows = new List<Dictionary<string, object?>>();
        uint pad = padInstanceId == null ? 0 : DevNode.Locate(padInstanceId);
        if (pad == 0) return rows;
        void Add(uint d, int depth)
        {
            string id = DevNode.Id(d);
            var st = DevNode.Status(d);
            var mi = Regex.Match(id, @"&MI_(\d\d)", RegexOptions.IgnoreCase);
            rows.Add(new()
            {
                ["depth"] = depth,
                ["instanceId"] = UsbProbe.MaskSerial(id),
                ["interface"] = mi.Success ? int.Parse(mi.Groups[1].Value) : null,
                ["name"] = DevNode.Name(d),
                ["class"] = DevNode.Str(d, DevNode.Class),
                ["service"] = DevNode.Str(d, DevNode.Service),
                ["started"] = st == null ? null : (st.Value.Status & 0x8) != 0,       // DN_STARTED
                ["hasProblem"] = st == null ? null : (st.Value.Status & 0x400) != 0,  // DN_HAS_PROBLEM
                ["problemCode"] = st?.Problem,
                ["statusHex"] = st == null ? null : $"0x{st.Value.Status:X8}",
                ["driverInf"] = DevNode.Str(d, DevNode.DriverInfPath),
                ["driverProvider"] = DevNode.Str(d, DevNode.DriverProvider),
                ["upperFilters"] = DevNode.StrList(d, DevNode.UpperFilters),
                ["lowerFilters"] = DevNode.StrList(d, DevNode.LowerFilters),
                ["dState"] = DevNode.DState(d),
            });
            if (depth < 3) foreach (uint c in DevNode.Children(d)) Add(c, depth + 1);
        }
        Add(pad, 0);
        return rows;
    }

    // ---- 2. what the input APIs return ----------------------------------------------

    static List<Dictionary<string, object?>> XInputLook(string dll, XInputGet get, double seconds)
    {
        var rows = new List<Dictionary<string, object?>>();
        var first = new uint[4]; var last = new uint[4]; var adv = new long[4]; var ok = new long[4];
        var calls = new long[4]; var codes = new HashSet<uint>[4]; var seen = new bool[4];
        var stick = new bool[4];
        for (int i = 0; i < 4; i++) codes[i] = new HashSet<uint>();
        try
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                for (uint i = 0; i < 4; i++)
                {
                    uint rc = get(i, out var st);
                    calls[i]++;
                    codes[i].Add(rc);
                    if (rc != 0) continue;
                    ok[i]++;
                    if (!seen[i]) { seen[i] = true; first[i] = st.dwPacketNumber; }
                    else if (st.dwPacketNumber != last[i]) adv[i]++;
                    last[i] = st.dwPacketNumber;
                    if (Math.Abs((int)st.Gamepad.sThumbLX) > 12000 || Math.Abs((int)st.Gamepad.sThumbLY) > 12000 ||
                        Math.Abs((int)st.Gamepad.sThumbRX) > 12000 || Math.Abs((int)st.Gamepad.sThumbRY) > 12000) stick[i] = true;
                }
                Thread.Sleep(1);
            }
        }
        catch (DllNotFoundException) { rows.Add(new() { ["dll"] = dll, ["installed"] = false }); return rows; }
        catch (EntryPointNotFoundException) { rows.Add(new() { ["dll"] = dll, ["installed"] = false }); return rows; }
        for (int i = 0; i < 4; i++)
            rows.Add(new()
            {
                ["dll"] = dll, ["installed"] = true, ["slot"] = i,
                ["connected"] = ok[i] > 0,
                ["calls"] = calls[i], ["callsOk"] = ok[i],
                ["resultCodes"] = codes[i].OrderBy(c => c).ToList(),
                ["packetFirst"] = seen[i] ? first[i] : null, ["packetLast"] = seen[i] ? last[i] : null,
                ["packetChanges"] = adv[i],
                ["stickSeenMoved"] = stick[i],
            });
        return rows;
    }

    static Dictionary<string, object?> HidLook(double seconds)
    {
        var res = new Dictionary<string, object?>();
        string? path = FindHidPath(0x054C, 0x05C4);
        if (path == null) { res["opened"] = false; res["why"] = "no readable PS4-mode HID interface found"; return res; }
        var h = CreateFile(path, GENERIC_READ, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            res["opened"] = false; res["why"] = $"open failed ({Marshal.GetLastWin32Error()})";
            return res;
        }
        HidD_GetPreparsedData(h, out IntPtr pp);
        HidP_GetCaps(pp, out var caps);
        HidD_FreePreparsedData(pp);
        int len = Math.Max((int)caps.InputReportByteLength, 1);
        long n = 0, changed = 0;
        string? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var stream = new FileStream(h, FileAccess.Read, len, false);
                var buf = new byte[len]; var prev = new byte[5];
                while (true)
                {
                    int got = stream.Read(buf, 0, len);
                    if (got <= 0) break;
                    if (n > 0 && got >= 5 && !buf.AsSpan(1, 4).SequenceEqual(prev.AsSpan(1, 4))) changed++;
                    Array.Copy(buf, prev, Math.Min(5, got));
                    Interlocked.Increment(ref n);
                }
            }
            catch (Exception ex) { error = ex.Message; }
        }) { IsBackground = true };
        var sw = Stopwatch.StartNew();
        t.Start();
        Thread.Sleep((int)(seconds * 1000));
        double el = sw.Elapsed.TotalSeconds;
        long count = Interlocked.Read(ref n);
        try { h.Close(); } catch { }
        t.Join(1000);
        res["opened"] = true;
        res["reports"] = count;
        res["reportsPerSecond"] = Math.Round(count / el, 0);
        res["reportsWithStickChange"] = changed;
        res["readError"] = count == 0 ? error : null;
        return res;
    }

    // ---- Windows' own history for this pad -------------------------------------------

    static string RunHidden(string exe, string arguments, int timeoutMs)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo(exe, arguments)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                },
            };
            p.Start();
            var outTask = p.StandardOutput.ReadToEndAsync();
            p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return ""; }
            return outTask.Wait(2000) ? outTask.Result : "";
        }
        catch { return ""; }
    }

    static string MaskIds(string s)
    {
        s = Regex.Replace(s, @"((?:USB|HID)\\VID_[0-9A-Fa-f]{4}&PID_[0-9A-Fa-f]{4}\\)([0-9A-Za-z_.\-]{6,})(?![&0-9A-Za-z])",
            m => m.Groups[2].Value.Contains('&') ? m.Value : m.Groups[1].Value + "<serial>");
        return Regex.Replace(s, @"(?i)([A-Z]:\\Users\\)[^\\\s""]+", "$1<user>");
    }

    static bool MentionsPad(string s) =>
        s.Contains("VID_39AE", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("VID_054C&PID_05C4", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("VID_1A86&PID_1235", StringComparison.OrdinalIgnoreCase);

    /// Kernel-PnP events of the last day that name the pad. wevtutil, read-only, no admin for these logs.
    static Dictionary<string, object?> PnpEvents()
    {
        var res = new Dictionary<string, object?>();
        var events = new List<Dictionary<string, object?>>();
        var logs = new Dictionary<string, object?>();
        var counts = new Dictionary<string, int>();
        foreach (string log in new[] { "Microsoft-Windows-Kernel-PnP/Configuration", "Microsoft-Windows-Kernel-PnP/Device Management" })
        {
            string xml = RunHidden("wevtutil.exe",
                $"qe \"{log}\" \"/q:*[System[TimeCreated[timediff(@SystemTime) <= 86400000]]]\" /f:xml /rd:true /c:4000", 10000);
            var all = Regex.Matches(xml, @"<Event[ >].*?</Event>", RegexOptions.Singleline);
            int kept = 0, about = 0;
            foreach (Match m in all)                       // newest first
            {
                string e = System.Net.WebUtility.HtmlDecode(m.Value);
                if (!MentionsPad(e)) continue;
                about++;
                string eid = Regex.Match(e, @"<EventID[^>]*>(\d+)</EventID>").Groups[1].Value;
                counts[eid] = counts.GetValueOrDefault(eid) + 1;
                if (kept >= 150) continue;                 // every one is counted, the newest 150 are kept
                kept++;
                var data = new Dictionary<string, string>();
                foreach (Match d in Regex.Matches(e, @"<Data Name=['""]([^'""]+)['""]>(.*?)</Data>", RegexOptions.Singleline))
                    data[d.Groups[1].Value] = MaskIds(d.Groups[2].Value);
                events.Add(new()
                {
                    ["log"] = log.EndsWith("Configuration") ? "Configuration" : "Device Management",
                    ["id"] = int.TryParse(Regex.Match(e, @"<EventID[^>]*>(\d+)</EventID>").Groups[1].Value, out int id) ? id : null,
                    ["timeUtc"] = Regex.Match(e, @"SystemTime=['""]([^'""]+)['""]").Groups[1].Value,
                    ["data"] = data,
                });
            }
            logs[log] = new Dictionary<string, object?> { ["readable"] = xml.Length > 0, ["eventsInLastDay"] = all.Count, ["aboutMariusPads"] = about, ["kept"] = kept };
        }
        res["logs"] = logs;
        res["counts"] = counts.ToDictionary(c => c.Key, c => (object?)c.Value);
        res["events"] = events.OrderBy(e => (string)e["timeUtc"]!).ToList();
        res["what"] = "Kernel-PnP events of the last 24 h that name a Marius pad's USB ids (any such pad on this PC, not only the one plugged in now): 400/410 configured/started, 411 problem starting, 420 deleted, 430 needs further installation, 1010 removed without warning";
        return res;
    }

    /// The last sections of Windows' driver-install log that name the pad.
    static Dictionary<string, object?> SetupApiTail()
    {
        var res = new Dictionary<string, object?>();
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", "setupapi.dev.log");
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long take = Math.Min(f.Length, 3L * 1024 * 1024);
            f.Position = f.Length - take;
            var buf = new byte[take];
            f.ReadExactly(buf, 0, (int)take);
            var sections = Regex.Split(Encoding.UTF8.GetString(buf), @"(?m)^(?=>>>  \[)")
                .Where(s => s.StartsWith(">>>  [") && MentionsPad(s)).ToList();
            res["sectionsAboutThisPad"] = sections.Count;
            res["lastSections"] = sections.TakeLast(6).Select(s =>
            {
                var lines = s.Split('\n').Select(l => MaskIds(l.TrimEnd())).Where(l => l.Length > 0).ToList();
                return lines.Count <= 70 ? lines : lines.Take(35).Append($"... {lines.Count - 70} lines left out ...").Concat(lines.TakeLast(35)).ToList();
            }).ToList();
        }
        catch (Exception ex) { res["error"] = ex.Message; }
        return res;
    }

    /// Leftovers: per-device USB flags Windows keeps, and device nodes that exist but are not present.
    static Dictionary<string, object?> Leftovers()
    {
        var res = new Dictionary<string, object?>();
        try
        {
            var flags = new Dictionary<string, object?>();
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\usbflags");
            if (k != null)
            {
                static string Show(object? v) => v is byte[] b ? Convert.ToHexString(b) : v?.ToString() ?? "";
                bool Ours(string n) => n.Contains("39AE", StringComparison.OrdinalIgnoreCase) || n.Contains("054C05C4", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("1A861235", StringComparison.OrdinalIgnoreCase);
                foreach (string v in k.GetValueNames().Where(Ours)) flags[v] = Show(k.GetValue(v));
                foreach (string sub in k.GetSubKeyNames().Where(Ours))
                {
                    using var s = k.OpenSubKey(sub);
                    flags[sub] = s?.GetValueNames().ToDictionary(n => n, n => Show(s.GetValue(n)));
                }
            }
            res["usbflags"] = flags;
        }
        catch (Exception ex) { res["usbflagsError"] = ex.Message; }
        try
        {
            var present = new HashSet<string>(DevNode.PresentUsb(), StringComparer.OrdinalIgnoreCase);
            var mine = DevNode.List("USB", DevNode.CM_GETIDLIST_FILTER_ENUMERATOR)
                .Where(id => PadIdPrefixes.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase))).ToList();
            res["deviceNodes"] = mine.GroupBy(id => Regex.Match(id, @"VID_[0-9A-Fa-f]{4}&PID_[0-9A-Fa-f]{4}(&MI_\d\d)?").Value.ToUpperInvariant())
                .OrderBy(g => g.Key)
                .ToDictionary(g => g.Key, g => (object?)new Dictionary<string, object?>
                {
                    ["present"] = g.Count(present.Contains),
                    ["notPresent"] = g.Count(id => !present.Contains(id)),
                    ["distinctSerials"] = g.Select(id => id[(id.LastIndexOf('\\') + 1)..]).Where(t => !t.Contains('&')).Distinct().Count(),
                });
        }
        catch (Exception ex) { res["deviceNodesError"] = ex.Message; }
        return res;
    }

    // ---- the check ---------------------------------------------------------------------

    static (Dictionary<string, object?> Data, List<string> Verdict) RunCheck(string vidPid, bool ps4, Dictionary<string, object?> usb)
    {
        var data = new Dictionary<string, object?> { ["startedUtc"] = DateTime.UtcNow.ToString("o") };
        string? padId = PadInstanceId(usb);
        var tree = DeviceTree(padId);
        data["deviceTree"] = tree;

        Console.WriteLine();
        Console.WriteLine("  Move a stick in circles NOW and keep going until it says stop.");
        Console.Write("  Looking");
        Dictionary<string, object?>? pad = null;
        var padThread = new Thread(() => { pad = PadSettings.Read(vidPid, ps4 ? FindHidPath(0x054C, 0x05C4) : null, padId); })
        { IsBackground = true };
        var programs = ProgramsNow("check", 0);

        List<Dictionary<string, object?>> xin = new();
        Dictionary<string, object?>? hid = null;
        Thread.Sleep(700);                                  // let the hand get going
        padThread.Start();
        if (ps4) hid = HidLook(CheckWindowSec);
        else
        {
            xin.AddRange(XInputLook("xinput1_4", XInput14, CheckWindowSec)); Console.Write(".");
            xin.AddRange(XInputLook("xinput1_3", XInput13, 0.6)); Console.Write(".");
            xin.AddRange(XInputLook("xinput9_1_0", XInput910, 0.6)); Console.Write(".");
        }
        padThread.Join(5000);
        Console.WriteLine("  stop. Thank you.");
        pad?.Remove("readAtQpc");
        data["xinput"] = ps4 ? null : xin;
        data["hid"] = hid;
        data["padSettings"] = pad;
        data["programs"] = programs;

        Console.Write("  Reading Windows' device history");
        data["pnpEvents"] = PnpEvents(); Console.Write(".");
        data["setupApi"] = SetupApiTail(); Console.Write(".");
        data["leftovers"] = Leftovers(); Console.WriteLine(".");

        // ---- verdict, in plain words ----------------------------------------------------
        var v = new List<string>();
        var top = tree.FirstOrDefault();
        var problems = tree.Where(n => n["hasProblem"] is true || (n["problemCode"] is uint pc && pc != 0)).ToList();
        string? fw = pad?.GetValueOrDefault("firmwareName") as string;
        var count = pad?.GetValueOrDefault("reportCount") as Dictionary<string, object?>;
        double? padRate = count?["reportsPerSecondByPadCount"] is double r ? r : null;
        string? padSkipped = pad?.GetValueOrDefault("skipped") as string;

        if (top == null) v.Add("Windows does not list the controller on USB right now.");
        foreach (var p in problems)
            v.Add($"Windows reports a problem (code {p["problemCode"]}) on \"{p["name"]}\"" +
                  (p["interface"] != null ? $", interface {p["interface"]}." : "."));

        string state;
        if (ps4)
        {
            long reports = hid?["reports"] is long l ? l : 0;
            if (hid?["opened"] is not true) { state = "not-readable"; v.Add("Windows lists the controller, but its input could not be opened: " + hid?["why"]); }
            else if (reports > 0) { state = "reading"; v.Add($"Windows is reading the controller: {hid!["reportsPerSecond"]} reports per second."); }
            else { state = "never-read"; v.Add("The controller is connected, but no report arrived in 4 seconds."); }
        }
        else
        {
            var main = xin.Where(x => (string)x["dll"]! == "xinput1_4" && x["installed"] is true).ToList();
            var connected = main.Where(x => x["connected"] is true).ToList();
            var gamepadIf = tree.FirstOrDefault(n => n["interface"] is int i && i == 0);
            if (connected.Count == 0)
            {
                state = "no-slot";
                v.Add("Windows has the controller on USB, but it has no XInput slot, so games cannot see it.");
                if (gamepadIf != null)
                    v.Add($"Its gamepad interface uses the driver \"{gamepadIf["service"] ?? "none"}\" and is {(gamepadIf["started"] is true ? "started" : "NOT started")}.");
            }
            else if (connected.Any(x => (long)x["packetChanges"]! > 0))
            {
                state = "reading";
                v.Add("Windows is reading the controller: its XInput data changed while you moved the stick.");
            }
            else
            {
                state = "never-read";
                v.Add("The controller has an XInput slot, but nothing arrived from it in 4 seconds.");
                v.Add("If you were moving the stick, that is the fault. If not, run the check again and move it.");
                if (padRate is > 0) v.Add($"The controller's own count says the PC took about {padRate:0} reports per second, so they are lost inside Windows.");
                else if (padRate == 0) v.Add("The controller's own count says the PC took no reports from it.");
            }
            if (connected.Count > 1) v.Add($"Note: {connected.Count} XInput controllers are connected, so DeepLog cannot tell which slot is this one.");
            var other = xin.Where(x => (string)x["dll"]! != "xinput1_4" && x["installed"] is true)
                .GroupBy(x => (string)x["dll"]!).Where(g => g.Count(x => x["connected"] is true) != connected.Count).Select(g => g.Key).ToList();
            if (other.Count > 0) v.Add("Older XInput versions see a different number of controllers: " + string.Join(", ", other) + ".");
        }
        if (padSkipped != null && padSkipped.StartsWith("port busy"))
            v.Add("Another program has the controller's setup port open (the setup site in a browser?). Close it and run the check again.");
        if (fw != null) v.Add($"Controller firmware: {fw}.");
        var ev = (data["pnpEvents"] as Dictionary<string, object?>)?["counts"] as Dictionary<string, object?>;
        if (ev != null && ev.Count > 0)
        {
            int Get(string k) => ev.TryGetValue(k, out var o) && o is int i ? i : 0;
            if (Get("1010") + Get("411") > 0)
                v.Add($"Windows' log for the last 24 hours: a Marius controller vanished without warning {Get("1010")} time(s) and failed to start {Get("411")} time(s). Unplugging by hand counts too.");
        }
        data["state"] = state;
        data["verdict"] = v;
        return (data, v);
    }

    static void RunCheckFlow(string[] args, string vidPid, string name, bool ps4, Dictionary<string, object?> usb)
    {
        Console.WriteLine();
        Console.WriteLine("  Check: this only looks, it changes nothing. It takes under a minute.");
        var (data, verdict) = RunCheck(vidPid, ps4, usb);

        Console.WriteLine();
        Console.WriteLine("  RESULT");
        foreach (string line in verdict) Console.WriteLine("    " + line);
        Console.WriteLine();

        string note, nickname, email;
        if (Headless)
        {
            note = ArgValue(args, "--note") ?? "(headless check)";
            nickname = ArgValue(args, "--nickname") ?? "anonymous";
            email = "";
        }
        else
        {
            Console.WriteLine("  What is the problem? Which game? (ENTER to skip)");
            Console.Write("  > ");
            note = (Console.ReadLine() ?? "").Trim();
            if (note.Length == 0) note = "(no note)";
            Console.Write("  Your nickname (Discord/name, ENTER for anonymous): ");
            nickname = (Console.ReadLine() ?? "").Trim();
            if (nickname.Length == 0) nickname = "anonymous";
            Console.Write("  Your email (optional, for follow-up -- ENTER to skip): ");
            email = (Console.ReadLine() ?? "").Trim();
        }

        string workDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepLog", $"deeplog_{DateTime.Now:yyyy-MM-dd_HHmmss}");
        Directory.CreateDirectory(workDir);
        string installId = InstallId();
        var uid = PadUid.Read(vidPid, ps4 ? FindHidPath(0x054C, 0x05C4) : null, PadInstanceId(usb));
        var meta = new Dictionary<string, object?>
        {
            ["tool"] = "deeplog", ["toolVersion"] = VERSION, ["mode"] = "check",
            ["createdUtc"] = DateTime.UtcNow.ToString("o"), ["installId"] = installId,
            ["note"] = note, ["nickname"] = nickname, ["email"] = email,
            ["device"] = new Dictionary<string, object?>
            { ["vidPid"] = vidPid, ["name"] = name, ["backend"] = ps4 ? "hid-raw" : "xinput", ["uid"] = uid["uid"] },
            ["padUid"] = uid,
            ["headless"] = Headless ? true : null,
            ["check"] = data,
        };
        File.WriteAllText(Path.Combine(workDir, "meta.json"), JsonSerializer.Serialize(meta, JsonOut));
        File.WriteAllText(Path.Combine(workDir, "note.txt"), note + Environment.NewLine);
        File.WriteAllText(Path.Combine(workDir, "snapshot.json"), JsonSerializer.Serialize(CollectSnapshot(usb, installId), JsonOut));
        string zipPath = Path.Combine(workDir, "bundle.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            foreach (string f in new[] { "meta.json", "note.txt", "snapshot.json" })
                zip.CreateEntryFromFile(Path.Combine(workDir, f), f, CompressionLevel.Optimal);
        double zipKb = new FileInfo(zipPath).Length / 1024.0;

        Console.WriteLine();
        Console.WriteLine($"  Ready to send ({zipKb:F0} KB, no recording):");
        Console.WriteLine("    the result above, and what each way of reading a controller returned");
        Console.WriteLine("    how Windows has the controller installed: its USB parts, their drivers and any problem code");
        Console.WriteLine("    Windows' own log lines about this controller for the last day (plugged in, started, failed, removed)");
        Console.WriteLine("    leftover Windows entries for this controller model");
        Console.WriteLine("    the controller's own settings (firmware version, mode, rate, button remap, click triggers)");
        Console.WriteLine("    your note and contact (nickname / email, if you gave them)");
        Console.WriteLine("    system info (Windows version and update level, USB + power settings)");
        Console.WriteLine("    signs of a modified Windows, which USB port/hub the controller is on, the names of the");
        Console.WriteLine("      other USB devices, and every driver on your USB devices and controllers");
        Console.WriteLine("    which launchers, overlays, controller tools and browsers are running (names only)");
        Console.WriteLine("    the controller's chip ID, and a random ID for this PC made by DeepLog");
        Console.WriteLine();
        Console.WriteLine("  Nothing else is included.");
        Console.WriteLine();

        if (Headless)
        {
            if (args.Contains("--upload"))
            {
                string? id = Upload(zipPath, nickname);
                Console.WriteLine(id != null ? $"  Sent!  Log ID: {id}" : "  Upload failed.");
            }
            Console.WriteLine($"  Bundle: {zipPath}");
            return;
        }
        Console.Write("  Send to Marius now? [Y/n] ");
        string yn = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        if (yn == "" || yn == "y" || yn == "yes")
        {
            string? logId = Upload(zipPath, nickname);
            Console.WriteLine();
            if (logId != null)
            {
                Console.WriteLine($"  Sent!  Log ID: {logId}");
                Console.WriteLine();
                Console.WriteLine("  Paste this in the Discord support channel:");
                Console.WriteLine();
                Console.WriteLine($"    DeepLog check {logId} -- \"{note}\"");
            }
            else
            {
                Console.WriteLine("  Upload didn't go through. The result is saved here:");
                Console.WriteLine($"  {zipPath}");
            }
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("  Not sent. The result is saved here if you change your mind:");
            Console.WriteLine($"  {zipPath}");
        }
        Console.WriteLine();
    }
}
