// The pad's own settings, read once before the recording starts, so a capture can
// be tied to the setup it was made with (firmware, mode, rate, remap, click triggers).
//
// Only pure reads with no payload, the list given by the firmware owner (2026-11-19):
//   0x02 GET_APP_INFO   0x06 GET_PERSONA   0x91 GET_BINTERVAL   0x9F GET_UPDATE_ON
//   0x55 READ_FULL_REMAP   0x51 READ_BUTTON_REMAP   0x15 GET_TRIG_CLICK (Seidr 0.7+ only)
// NOTHING else is ever sent. In particular never 0xB2 with a payload: older pads
// park their report stream on it.
//
// 0x02 goes first. The rest are sent only when its reply names Seidr firmware, so
// older firmware gets exactly one command, one it has always served.
//
// PS4 mode:  HID feature reports, SET 0x90 [0x90, opcode, zeros], GET 0x91 [0x91, status, data].
// XInput:    the pad's own serial port (a child of the same USB device), frame
//            [0x7E, opcode] -> 64 bytes [0x7E, status, data]. Opened once, closed once.
//            If the setup site holds the port the read is skipped and that is recorded.
// The raw reply bytes are stored as hex; only name/version, mode and rate are decoded.
// Never throws, never holds the log up for more than a few seconds.

using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

static class PadSettings
{
    static readonly (byte Op, string Name)[] SeidrReads =
    {
        (0x06, "GET_PERSONA"), (0x91, "GET_BINTERVAL"), (0x9F, "GET_UPDATE_ON"),
        (0x55, "READ_FULL_REMAP"), (0x51, "READ_BUTTON_REMAP"),
    };
    // v2.3.2: three more no-payload reads, served by every Seidr from 0.6. 0xB2 [0xFF] is
    // Seidr 0.9 and later ONLY: older pads park their report stream on it.
    static readonly (byte Op, string Name)[] Seidr09Reads =
    {
        (0x96, "GET_DIRTY_STATUS"), (0xA8, "GET_LUT_PROVENANCE"), (0x9B, "GET_ADC_TIMING"),
    };
    const byte OpAppInfo = 0x02, OpTrigClick = 0x15, OpReportCount = 0xB2;
    /// Time between the two report-count reads. The check widens it to span the moment the person moves.
    public static int CountGapMs = 1000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr sec,
        uint disposition, uint flags, IntPtr template);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr preparsed, byte[] caps);

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetCommState(SafeFileHandle h, byte[] dcb);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetCommState(SafeFileHandle h, byte[] dcb);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetCommTimeouts(SafeFileHandle h, uint[] timeouts);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool EscapeCommFunction(SafeFileHandle h, uint func);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool PurgeComm(SafeFileHandle h, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(SafeFileHandle h, byte[] buf, uint len, out uint written, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(SafeFileHandle h, byte[] buf, uint len, out uint read, IntPtr overlapped);

    const uint GENERIC_RW = 0x80000000 | 0x40000000, OPEN_EXISTING = 3;

    /// Exchange one opcode; returns (status, 62 data bytes) or null when nothing came back.
    delegate (byte Status, byte[] Data)? Exchange(byte opcode, byte[] payload);

    public static Dictionary<string, object?> Read(string vidPid, string? hidPath, string? padInstanceId)
    {
        var res = new Dictionary<string, object?> { ["readAtQpc"] = System.Diagnostics.Stopwatch.GetTimestamp() };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var t = new Thread(() =>
        {
            try
            {
                if (vidPid == "054C:05C4") ReadHid(hidPath, res);
                else if (vidPid.StartsWith("39AE:")) ReadSerial(padInstanceId, res);
                else res["skipped"] = "not a pad with a settings channel DeepLog knows";
            }
            catch (Exception ex) { lock (res) res["error"] = ex.Message; }
        }) { IsBackground = true };
        t.Start();
        if (!t.Join(3000 + CountGapMs)) lock (res) res["error"] = "timed out";
        lock (res)
        {
            res["ms"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
            return new Dictionary<string, object?>(res);
        }
    }

    static void RunReads(Exchange x, Dictionary<string, object?> res)
    {
        var commands = new List<Dictionary<string, object?>>();
        lock (res) res["commands"] = commands;

        (byte Status, byte[] Data)? Do(byte op, string name, params byte[] payload)
        {
            var r = x(op, payload);
            var row = new Dictionary<string, object?>
            {
                ["opcode"] = $"0x{op:X2}",
                ["name"] = name,
                ["payloadHex"] = payload.Length == 0 ? null : Convert.ToHexString(payload),
                ["status"] = r == null ? null : $"0x{r.Value.Status:X2}",
                ["dataHex"] = r == null ? null : Convert.ToHexString(r.Value.Data),
            };
            lock (res) commands.Add(row);
            return r;
        }

        var info = Do(OpAppInfo, "GET_APP_INFO");
        if (info == null || info.Value.Status != 0xAA)
        {
            lock (res) res["stoppedAfter"] = "GET_APP_INFO did not answer OK, so nothing else was asked";
            return;
        }
        // Printable runs in the record; the firmware name is one of them ("MH4-D SEIDR v0.9").
        var strings = Regex.Matches(Encoding.Latin1.GetString(info.Value.Data), @"[\x20-\x7E]{4,}")
            .Select(m => m.Value.Trim()).Where(s => s.Length >= 4).ToList();
        string? name = strings.FirstOrDefault(s => s.Contains("SEIDR", StringComparison.OrdinalIgnoreCase));
        lock (res) { res["firmwareStrings"] = strings; res["firmwareName"] = name ?? strings.FirstOrDefault(); }
        if (name == null)
        {
            lock (res) res["stoppedAfter"] = "firmware name does not say SEIDR, so only GET_APP_INFO was asked";
            return;
        }
        var ver = Regex.Match(name, @"SEIDR\s+v(\d+)\.(\d+)", RegexOptions.IgnoreCase);
        int major = ver.Success ? int.Parse(ver.Groups[1].Value) : -1, minor = ver.Success ? int.Parse(ver.Groups[2].Value) : -1;
        lock (res) res["seidrVersion"] = ver.Success ? $"{major}.{minor}" : null;

        foreach (var (op, opName) in SeidrReads)
        {
            var r = Do(op, opName);
            if (r == null || r.Value.Status != 0xAA) continue;
            if (op == 0x06) lock (res) res["mode"] = r.Value.Data[0] switch { 0 => "PS4", 1 => "XInput", var v => $"unknown ({v})" };
            if (op == 0x91)
            {
                int code = r.Value.Data[0];
                lock (res)
                {
                    res["rateCode"] = code;
                    res["rateHz"] = code >= 1 && code <= 8 ? 8000 >> (code - 1) : null;
                }
            }
        }
        // Click triggers: the command exists from Seidr 0.7. Older Seidr is not asked.
        if (ver.Success && (major > 0 || minor >= 7)) Do(OpTrigClick, "GET_TRIG_CLICK");
        else lock (res) res["trigClickSkipped"] = "only asked on Seidr 0.7 and later";

        // Pure no-payload reads on every Seidr from 0.6 (firmware owner, 2026-11-19).
        foreach (var (op, opName) in Seidr09Reads) Do(op, opName);

        if (ver.Success && (major > 0 || minor >= 9))
        {
            // The pad's own count of reports the PC took (u16, wraps every 8 s at 8 kHz, restarts
            // at a USB re-setup). Read twice about a second apart and timed with this PC's clock.
            // Only the count is decoded: over HID the rest of the reply is cut short by the pad.
            // The payload is exactly 0xFF, nothing else, ever, and never below Seidr 0.9.
            var a = Do(OpReportCount, "GET_REPORT_COUNT", 0xFF);
            long qa = System.Diagnostics.Stopwatch.GetTimestamp();
            var ua = DateTime.UtcNow;
            Thread.Sleep(CountGapMs);
            var b = Do(OpReportCount, "GET_REPORT_COUNT", 0xFF);
            long qb = System.Diagnostics.Stopwatch.GetTimestamp();
            var ub = DateTime.UtcNow;
            if (a != null && b != null && a.Value.Status == 0xAA && b.Value.Status == 0xAA)
            {
                int ca = BitConverter.ToUInt16(a.Value.Data, 6), cb = BitConverter.ToUInt16(b.Value.Data, 6);
                double dt = (qb - qa) / (double)System.Diagnostics.Stopwatch.Frequency;
                lock (res) res["reportCount"] = new Dictionary<string, object?>
                {
                    ["first"] = ca, ["second"] = cb, ["secondsBetween"] = Math.Round(dt, 4),
                    ["firstReadUtc"] = ua.ToString("o"), ["secondReadUtc"] = ub.ToString("o"),
                    ["byte0First"] = (int)a.Value.Data[0], ["byte0Second"] = (int)b.Value.Data[0],   // 0 = not paused
                    ["advanced"] = ((cb - ca) & 0xFFFF) != 0,
                    ["reportsPerSecondByPadCount"] = Math.Round(((cb - ca) & 0xFFFF) / dt, 0),
                    ["note"] = "the pad's own count of reports the PC took; in XInput the pad sends only on change, so a pad at rest counts nothing",
                };
            }
        }
        else lock (res) res["reportCountSkipped"] = "only asked on Seidr 0.9 and later";
    }

    // ---- PS4 mode: HID feature reports ---------------------------------------------

    static void ReadHid(string? path, Dictionary<string, object?> res)
    {
        lock (res) res["channel"] = "hid-feature 0x90/0x91";
        if (path == null) { lock (res) res["skipped"] = "PS4 HID collection not found"; return; }
        using var h = CreateFile(path, GENERIC_RW, 0x03, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) { lock (res) res["skipped"] = $"open failed ({Marshal.GetLastWin32Error()})"; return; }
        int len = 64;
        if (HidD_GetPreparsedData(h, out IntPtr pp))
        {
            var caps = new byte[64];
            HidP_GetCaps(pp, caps);
            HidD_FreePreparsedData(pp);
            len = Math.Max(BitConverter.ToUInt16(caps, 8), (ushort)64);
        }

        RunReads((op, payload) =>
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var send = new byte[len];
                send[0] = 0x90; send[1] = op;                    // rest stays zero
                payload.CopyTo(send, 2);
                if (!HidD_SetFeature(h, send, len)) return null;
                Thread.Sleep(30);
                var recv = new byte[len];
                recv[0] = 0x91;
                if (!HidD_GetFeature(h, recv, len)) return null;
                if (recv[1] == 0xBB && attempt == 0) { Thread.Sleep(50); continue; }   // busy: once more
                return (recv[1], recv.Skip(2).Take(62).ToArray());
            }
            return null;
        }, res);
    }

    // ---- XInput mode: the pad's serial port ----------------------------------------

    static void ReadSerial(string? padInstanceId, Dictionary<string, object?> res)
    {
        lock (res) res["channel"] = "serial";
        if (padInstanceId == null) { lock (res) res["skipped"] = "pad not found in the USB tree"; return; }
        uint pad = DevNode.Locate(padInstanceId);
        string? com = null;
        foreach (uint child in pad == 0 ? new List<uint>() : DevNode.Children(pad))
        {
            if (!string.Equals(DevNode.Str(child, DevNode.Class), "Ports", StringComparison.OrdinalIgnoreCase)) continue;
            var m = Regex.Match(DevNode.Name(child), @"\((COM\d+)\)");
            if (m.Success) { com = m.Groups[1].Value; break; }
        }
        if (com == null)
        {
            lock (res) res["skipped"] = "this pad has no serial port (firmware before Seidr), so its settings cannot be read in XInput mode";
            return;
        }

        var h = CreateFile(@"\\.\" + com, GENERIC_RW, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int e = Marshal.GetLastWin32Error();
            lock (res) res["skipped"] = e == 5
                ? "port busy: another program (the setup site?) has the pad's serial port open"
                : $"serial port open failed ({e})";
            return;
        }
        try                                                           // opened once, closed once
        {
            var dcb = new byte[28];
            BitConverter.GetBytes(28).CopyTo(dcb, 0);
            if (GetCommState(h, dcb))
            {
                BitConverter.GetBytes(115200).CopyTo(dcb, 4);
                BitConverter.GetBytes((uint)(0x0001 | 0x0010)).CopyTo(dcb, 8);   // binary, DTR on, no flow control
                dcb[18] = 8; dcb[19] = 0; dcb[20] = 0;                           // 8N1
                SetCommState(h, dcb);
            }
            SetCommTimeouts(h, new uint[] { 0, 0, 400, 0, 400 });
            EscapeCommFunction(h, 5);                                 // SETDTR
            PurgeComm(h, 0x0008 | 0x0004);

            RunReads((op, payload) =>
            {
                PurgeComm(h, 0x0008);
                var frame = new byte[2 + payload.Length];
                frame[0] = 0x7E; frame[1] = op;
                payload.CopyTo(frame, 2);
                if (!WriteFile(h, frame, (uint)frame.Length, out uint w, IntPtr.Zero) || w != frame.Length) return null;
                var reply = new byte[64];
                int got = 0;
                var one = new byte[64];
                var until = DateTime.UtcNow.AddMilliseconds(600);
                while (got < 64 && DateTime.UtcNow < until)
                {
                    if (!ReadFile(h, one, (uint)(64 - got), out uint n, IntPtr.Zero)) return null;
                    Array.Copy(one, 0, reply, got, (int)n);
                    got += (int)n;
                }
                if (got < 64 || reply[0] != 0x7E) return null;
                return (reply[1], reply.Skip(2).Take(62).ToArray());
            }, res);
        }
        finally { h.Dispose(); }
    }
}
