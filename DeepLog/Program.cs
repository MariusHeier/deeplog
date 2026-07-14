// DeepLog v2 -- 30-second controller recorder for MH gamepads.
//
// Replaces the v1 ETW ring-buffer incident logger. v2 records the actual
// controller DATA (stick/trigger/button values) for 30 seconds and uploads
// it with a short note. Two capture backends, chosen by device identity:
//
//   XInput  (39AE:* gaming PIDs, 1A86:1235, or any generic pad)
//           polled at 8 kHz via XInputGetState
//   PS4     (054C:05C4 MH PS4 mode)
//           raw HID input reports, blocking reads, stored verbatim
//
// Output bundle (zip): data.mhc (binary, see WriteMhc), meta.json, note.txt,
// snapshot.json. Upload: presigned-S3 pattern, logType MUST stay "deeplog"
// (the Lambda signs application/zip only for that type -- see
// toolsmariusheiercom/S3-UPLOAD-PATTERN.md).
//
// No admin required (v1 needed it for ETW; v2 has no ETW).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32.SafeHandles;

class Program
{
    const string VERSION = "2.0.0";

    const string UploadUrlEndpoint = "https://tools.mariusheier.com/deeppoll/upload-url";
    const string DiscordUrl = "https://discord.gg/4Q9SRUt85j";

    const double RecordSeconds = 30.0;
    const double CountdownSeconds = 5.0;
    const double XInputTargetHz = 8000.0;

    // Known MH devices (same identities as DeepPoll).
    static readonly Dictionary<string, string> KnownDevices = new()
    {
        ["39AE:400A"] = "MH4 Gamepad (Analog)",
        ["39AE:400D"] = "MH4 Gamepad (Digital)",
        ["39AE:500A"] = "MH5 Gamepad (Analog)",
        ["39AE:500D"] = "MH5 Gamepad (Digital)",
        ["054C:05C4"] = "MH Gamepad (PS4 Mode)",
        ["1A86:1235"] = "MH-XSX / XInput v1.0",
        ["39AE:4000"] = "MH4 Gamepad (Setup Mode)",
        ["39AE:5000"] = "MH5 Gamepad (Setup Mode)",
        ["1209:0001"] = "WebUSB Setup Device",
    };

    static readonly string[] SetupModeVidPids = { "39AE:4000", "39AE:5000", "1209:0001" };
    const string Ps4VidPid = "054C:05C4";

    static string DeviceDisplayName(string vidPid) =>
        KnownDevices.TryGetValue(vidPid, out var name) ? name : vidPid;

    static void Main(string[] args)
    {
        if (args.Contains("--snapshot"))
        {
            Console.WriteLine(JsonSerializer.Serialize(CollectSnapshot(),
                new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        Console.WriteLine();
        Console.WriteLine("  D E E P L O G   v" + VERSION);
        Console.WriteLine("  Controller recorder for MH gamepads");
        Console.WriteLine();
        Console.WriteLine("  Record 30 seconds of controller data and send it to Marius.");
        Console.WriteLine("  It reads your controller only -- no keyboard, no files, nothing else.");
        Console.WriteLine();

        // ---- device selection ------------------------------------------------
        var device = SelectDevice();
        if (device == null) return;
        var (vidPid, name) = device.Value;

        bool ps4 = vidPid == Ps4VidPid;
        int xinputSlot = -1;
        if (!ps4)
        {
            xinputSlot = ResolveXInputSlot();
            if (xinputSlot < 0) return;
        }

        // ---- instruction + countdown ----------------------------------------
        Console.WriteLine();
        Console.WriteLine("  When recording starts:");
        Console.WriteLine("    Move the joysticks in circles around the edge. Click buttons.");
        Console.WriteLine("    Whatever is relevant for your case.");
        Console.WriteLine();
        Console.Write("  Starting in ");
        for (int i = (int)CountdownSeconds; i >= 1; i--)
        {
            Console.Write($"{i}...  ");
            Thread.Sleep(1000);
        }
        Console.WriteLine();
        Console.WriteLine();

        // ---- recording -------------------------------------------------------
        CaptureResult cap;
        try
        {
            cap = ps4 ? HidCapture(0x054C, 0x05C4, RecordSeconds)
                      : XInputCapture(xinputSlot, RecordSeconds);
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"  Recording failed: {ex.Message}");
            return;
        }

        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine("  Recording complete.");
        Console.WriteLine();

        // ---- note + contact ----------------------------------------------------
        Console.WriteLine("  Useful note to Marius");
        Console.WriteLine("    example:  Log of normal joystick feeling");
        Console.WriteLine("    example:  Log of joystick feeling weird");
        Console.Write("  > ");
        string note = (Console.ReadLine() ?? "").Trim();
        if (note.Length == 0) note = "(no note)";

        Console.Write("  Your nickname (Discord/name, ENTER for anonymous): ");
        string nickname = (Console.ReadLine() ?? "").Trim();
        if (nickname.Length == 0) nickname = "anonymous";

        Console.Write("  Your email (optional, for follow-up -- ENTER to skip): ");
        string email = (Console.ReadLine() ?? "").Trim();

        // ---- bundle ------------------------------------------------------------
        string workDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepLog", $"deeplog_{DateTime.Now:yyyy-MM-dd_HHmm}");
        Directory.CreateDirectory(workDir);

        string mhcPath = Path.Combine(workDir, "data.mhc");
        WriteMhc(mhcPath, cap);

        var meta = new Dictionary<string, object?>
        {
            ["tool"] = "deeplog",
            ["toolVersion"] = VERSION,
            ["createdUtc"] = DateTime.UtcNow.ToString("o"),
            ["note"] = note,
            ["nickname"] = nickname,
            ["email"] = email,
            ["device"] = new Dictionary<string, object?>
            {
                ["vidPid"] = vidPid,
                ["name"] = name,
                ["backend"] = ps4 ? "hid-raw" : "xinput",
                ["xinputSlot"] = ps4 ? null : xinputSlot,
            },
            ["recordSeconds"] = RecordSeconds,
            ["samples"] = cap.Count,
            ["achievedHz"] = Math.Round(cap.AchievedHz, 1),
            ["events"] = cap.Events,
            ["recordFormat"] = ps4
                ? "u32 t_us, u32 seq, byte[64] raw input report (proto 1, 72 B/record)"
                : "u32 t_us, u32 packet, i16 lx, i16 ly, i16 rx, i16 ry, u8 lt, u8 rt, u16 buttons, u8 phase, u8 connected, u16 pad (proto 0, 24 B/record)",
        };
        var jsonOpts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(workDir, "meta.json"), JsonSerializer.Serialize(meta, jsonOpts));
        File.WriteAllText(Path.Combine(workDir, "note.txt"), note + Environment.NewLine);
        File.WriteAllText(Path.Combine(workDir, "snapshot.json"),
            JsonSerializer.Serialize(CollectSnapshot(), jsonOpts));

        string zipPath = Path.Combine(workDir, "bundle.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(mhcPath, "data.mhc", CompressionLevel.Optimal);
            zip.CreateEntryFromFile(Path.Combine(workDir, "meta.json"), "meta.json");
            zip.CreateEntryFromFile(Path.Combine(workDir, "note.txt"), "note.txt");
            zip.CreateEntryFromFile(Path.Combine(workDir, "snapshot.json"), "snapshot.json");
        }
        double zipMb = new FileInfo(zipPath).Length / 1024.0 / 1024.0;

        // ---- review + consent ----------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("  Ready to send:");
        Console.WriteLine($"    30 second controller recording   ({zipMb:F1} MB)");
        Console.WriteLine("    your note");
        Console.WriteLine("    your contact (nickname / email, if you gave them)");
        Console.WriteLine("    system info (Windows version, USB + power settings)");
        Console.WriteLine();
        Console.WriteLine("  Nothing else is included.");
        Console.WriteLine();
        Console.Write("  Send to Marius now? [Y/n] ");
        string yn = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();

        if (yn == "" || yn == "y" || yn == "yes")
        {
            string? logId = Upload(zipPath, nickname);
            if (logId != null)
            {
                Console.WriteLine();
                Console.WriteLine($"  Sent!  Log ID: {logId}");
                Console.WriteLine();
                Console.WriteLine("  Paste this in the Discord support channel:");
                Console.WriteLine();
                Console.WriteLine($"    DeepLog {logId} -- \"{note}\"");
                Console.WriteLine();
                Console.WriteLine("  Tip: if the controller starts feeling different later -- better or");
                Console.WriteLine("  worse -- run DeepLog again. Two recordings to compare beats one.");
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("  Upload didn't go through. Your recording is safe here:");
                Console.WriteLine($"  {zipPath}");
                Console.WriteLine("  Try again later, or send that file to Marius on Discord directly.");
            }
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("  Not sent. The recording is saved here if you change your mind:");
            Console.WriteLine($"  {zipPath}");
        }
        Console.WriteLine();
    }

    // ---- device selection -----------------------------------------------------

    static (string VidPid, string Name)? SelectDevice()
    {
        bool waitingShown = false;
        while (true)
        {
            var found = DetectMHDevices();
            var gaming = found.Where(d => d.Verified && !SetupModeVidPids.Contains(d.VidPid)).ToList();
            var setup = found.Where(d => SetupModeVidPids.Contains(d.VidPid) && d.Verified).ToList();
            bool anyXInputSlot = ConnectedXInputSlots().Count > 0;

            if (gaming.Count == 1)
            {
                Console.WriteLine($"  Found: {gaming[0].Name}");
                return (gaming[0].VidPid, gaming[0].Name);
            }
            if (gaming.Count >= 1)
            {
                Console.WriteLine("  Which controller?");
                for (int i = 0; i < gaming.Count; i++)
                    Console.WriteLine($"    [{i + 1}] {gaming[i].Name}");
                Console.WriteLine($"    [{gaming.Count + 1}] Other USB controller");
                while (true)
                {
                    Console.Write("  > ");
                    string? c = Console.ReadLine()?.Trim();
                    if (int.TryParse(c, out int idx) && idx >= 1 && idx <= gaming.Count + 1)
                    {
                        if (idx == gaming.Count + 1)
                            return ("OTHER", "Other USB controller");
                        return (gaming[idx - 1].VidPid, gaming[idx - 1].Name);
                    }
                }
            }
            if (setup.Count > 0)
            {
                Console.WriteLine($"  Your {setup[0].Name.Replace(" (Setup Mode)", "")} is in SETUP MODE.");
                Console.WriteLine("  Switch it to gaming mode, then run DeepLog again.");
                return null;
            }
            if (anyXInputSlot)
            {
                // Non-MH pad present; offer it directly.
                Console.WriteLine("  Found: USB controller (XInput)");
                return ("OTHER", "Other USB controller");
            }

            if (!waitingShown)
            {
                Console.WriteLine("  Waiting for a controller... plug it in with USB.");
                waitingShown = true;
            }
            Thread.Sleep(1000);
        }
    }

    static int ResolveXInputSlot()
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        bool warned = false;
        while (DateTime.UtcNow < deadline)
        {
            var slots = ConnectedXInputSlots();
            if (slots.Count == 1) return slots[0];
            if (slots.Count > 1)
            {
                if (!warned)
                {
                    Console.WriteLine("  More than one XInput controller is connected --");
                    Console.WriteLine("  unplug the ones you don't want to record.");
                    warned = true;
                    deadline = DateTime.UtcNow.AddSeconds(120);
                }
            }
            Thread.Sleep(500);
        }
        var final = ConnectedXInputSlots();
        if (final.Count >= 1) return final[0];
        Console.WriteLine("  No XInput controller responded. Unplug/replug it and run DeepLog again.");
        return -1;
    }

    static List<int> ConnectedXInputSlots()
    {
        var list = new List<int>();
        for (uint i = 0; i < 4; i++)
            if (XInputGetState(i, out _) == 0) list.Add((int)i);
        return list;
    }

    // ---- capture results ----------------------------------------------------

    class CaptureResult
    {
        public byte Proto;                 // 0 = xinput, 1 = ps4 raw hid
        public ushort RecordSize;          // 24 or 72
        public byte[] Data = Array.Empty<byte>();
        public int Count;
        public double AchievedHz;
        public long StartUnixUs;
        public List<Dictionary<string, object>> Events = new();
    }

    // ---- XInput backend -------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [DllImport("xinput1_4.dll")]
    static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

    static CaptureResult XInputCapture(int slot, double seconds)
    {
        int capacity = (int)(XInputTargetHz * seconds) + 4096;
        const int REC = 24;
        var res = new CaptureResult
        {
            Proto = 0,
            RecordSize = REC,
            Data = new byte[capacity * REC],
            StartUnixUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000,
        };

        timeBeginPeriod(1);
        try
        {
            var sw = Stopwatch.StartNew();
            long periodTicks = (long)(Stopwatch.Frequency / XInputTargetHz);
            long nextTick = sw.ElapsedTicks;
            long endTicks = (long)(seconds * Stopwatch.Frequency);
            bool disconnected = false;
            int n = 0;
            var data = res.Data;
            double lastUi = 0;

            while (sw.ElapsedTicks < endTicks && n < capacity)
            {
                // pace to target rate: sleep the bulk, spin the last stretch
                long remain = nextTick - sw.ElapsedTicks;
                if (remain > 0)
                {
                    double remainMs = remain * 1000.0 / Stopwatch.Frequency;
                    if (remainMs > 1.5) Thread.Sleep((int)(remainMs - 1.0));
                    continue;
                }
                nextTick += periodTicks;
                if (nextTick < sw.ElapsedTicks - Stopwatch.Frequency / 20)
                    nextTick = sw.ElapsedTicks + periodTicks;   // resync after a stall

                uint tUs = (uint)(sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency);
                bool ok = XInputGetState((uint)slot, out var st) == 0;
                if (ok)
                {
                    if (disconnected)
                    {
                        disconnected = false;
                        res.Events.Add(new() { ["type"] = "reconnect", ["t_us"] = tUs });
                        Console.WriteLine();
                        Console.WriteLine("  Reconnected -- still recording.");
                    }
                    int o = n * REC;
                    WriteU32(data, o, tUs);
                    WriteU32(data, o + 4, st.dwPacketNumber);
                    WriteI16(data, o + 8, st.Gamepad.sThumbLX);
                    WriteI16(data, o + 10, st.Gamepad.sThumbLY);
                    WriteI16(data, o + 12, st.Gamepad.sThumbRX);
                    WriteI16(data, o + 14, st.Gamepad.sThumbRY);
                    data[o + 16] = st.Gamepad.bLeftTrigger;
                    data[o + 17] = st.Gamepad.bRightTrigger;
                    WriteU16(data, o + 18, st.Gamepad.wButtons);
                    data[o + 20] = 0;   // phase (single free phase in v2)
                    data[o + 21] = 1;   // connected
                    WriteU16(data, o + 22, 0);
                    n++;
                }
                else if (!disconnected)
                {
                    disconnected = true;
                    res.Events.Add(new() { ["type"] = "disconnect", ["t_us"] = tUs });
                    Console.WriteLine();
                    Console.WriteLine("  Controller disconnected -- that got recorded too, it's useful.");
                }

                double el = sw.Elapsed.TotalSeconds;
                if (el - lastUi > 0.1)
                {
                    lastUi = el;
                    DrawBar(el, seconds);
                }
            }
            res.Count = n;
            res.AchievedHz = n / sw.Elapsed.TotalSeconds;
            DrawBar(seconds, seconds);
        }
        finally { timeEndPeriod(1); }
        return res;
    }

    // ---- PS4 raw HID backend ---------------------------------------------------

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid gid);

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid gid, IntPtr enumerator, IntPtr hwnd, int flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr devs, IntPtr devInfo, ref Guid gid,
        uint index, ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devs, ref SP_DEVICE_INTERFACE_DATA data,
        IntPtr detail, int detailSize, out int required, IntPtr devInfoData);

    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr devs);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr sec,
        uint disposition, uint flags, IntPtr template);

    [StructLayout(LayoutKind.Sequential)]
    struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }

    [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_SetNumInputBuffers(SafeFileHandle h, uint count);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage;
        public ushort InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    const int DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
    const uint GENERIC_READ = 0x80000000, FILE_SHARE_RW = 0x03, OPEN_EXISTING = 3;

    /// Find the HID interface path for vid:pid whose input reports are readable.
    static string? FindHidPath(ushort vid, ushort pid)
    {
        HidD_GetHidGuid(out Guid gid);
        IntPtr devs = SetupDiGetClassDevs(ref gid, IntPtr.Zero, IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devs == IntPtr.Zero || devs == new IntPtr(-1)) return null;
        try
        {
            var ifd = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(devs, IntPtr.Zero, ref gid, i, ref ifd); i++)
            {
                SetupDiGetDeviceInterfaceDetail(devs, ref ifd, IntPtr.Zero, 0, out int need, IntPtr.Zero);
                IntPtr detail = Marshal.AllocHGlobal(need);
                try
                {
                    // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 on x64, 6 on x86
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(devs, ref ifd, detail, need, out _, IntPtr.Zero))
                        continue;
                    string path = Marshal.PtrToStringUni(detail + 4) ?? "";
                    if (!path.Contains($"vid_{vid:x4}") || !path.Contains($"pid_{pid:x4}"))
                        continue;

                    using var h = CreateFile(path, GENERIC_READ, FILE_SHARE_RW, IntPtr.Zero,
                        OPEN_EXISTING, 0, IntPtr.Zero);
                    if (h.IsInvalid) continue;
                    var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                    if (!HidD_GetAttributes(h, ref attr)) continue;
                    if (attr.VendorID != vid || attr.ProductID != pid) continue;
                    if (!HidD_GetPreparsedData(h, out IntPtr pp)) continue;
                    HidP_GetCaps(pp, out var caps);
                    HidD_FreePreparsedData(pp);
                    if (caps.InputReportByteLength > 0) return path;   // readable input collection
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devs); }
        return null;
    }

    static CaptureResult HidCapture(ushort vid, ushort pid, double seconds)
    {
        string? path = FindHidPath(vid, pid) ??
            throw new Exception("PS4-mode controller not found on the HID bus.");

        var handle = CreateFile(path, GENERIC_READ, FILE_SHARE_RW, IntPtr.Zero,
            OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new Exception("Could not open the controller for reading.");

        HidD_GetPreparsedData(handle, out IntPtr pp2);
        HidP_GetCaps(pp2, out var caps);
        HidD_FreePreparsedData(pp2);
        int reportLen = caps.InputReportByteLength;         // includes report-id byte
        HidD_SetNumInputBuffers(handle, 512);

        const int RAW = 64, REC = 72;                        // u32 t_us + u32 seq + 64B raw
        int capacity = (int)(XInputTargetHz * seconds) + 8192;
        var res = new CaptureResult
        {
            Proto = 1,
            RecordSize = REC,
            Data = new byte[capacity * REC],
            StartUnixUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000,
        };

        var sw = Stopwatch.StartNew();
        int n = 0;
        uint seq = 0;
        object gate = new();
        bool done = false;
        var events = res.Events;

        // Blocking reads on a worker thread: each ReadFile returns exactly one
        // input report, so timestamps are device-paced. Main thread draws the
        // bar and closes the handle at the deadline, which unblocks the read.
        var worker = new Thread(() =>
        {
            var stream = new FileStream(handle, FileAccess.Read, reportLen, false);
            var buf = new byte[reportLen];
            var data = res.Data;
            while (!Volatile.Read(ref done) && n < capacity)
            {
                int got;
                try { got = stream.Read(buf, 0, reportLen); }
                catch { break; }                              // handle closed or device gone
                if (got <= 0) break;
                uint tUs = (uint)(sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency);
                int o = n * REC;
                WriteU32(data, o, tUs);
                WriteU32(data, o + 4, seq++);
                Array.Copy(buf, 0, data, o + 8, Math.Min(got, RAW));
                n++;
            }
        }) { IsBackground = true };
        worker.Start();

        double lastUi = 0;
        int lastN = 0;
        bool disconnected = false;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            Thread.Sleep(50);
            double el = sw.Elapsed.TotalSeconds;
            // stalled report stream = disconnect (device normally streams continuously)
            if (n == lastN && el - lastUi > 1.0 && !disconnected && n > 0)
            {
                disconnected = true;
                events.Add(new() { ["type"] = "stream-stall", ["t_us"] = (uint)(el * 1e6) });
                Console.WriteLine();
                Console.WriteLine("  Controller disconnected -- that got recorded too, it's useful.");
            }
            if (n != lastN && disconnected)
            {
                disconnected = false;
                events.Add(new() { ["type"] = "stream-resume", ["t_us"] = (uint)(el * 1e6) });
                Console.WriteLine();
                Console.WriteLine("  Reconnected -- still recording.");
            }
            if (el - lastUi > 0.1) { lastUi = el; lastN = n; DrawBar(el, seconds); }
        }
        Volatile.Write(ref done, true);
        handle.Close();                                       // unblocks the worker's Read
        worker.Join(2000);

        res.Count = n;
        res.AchievedHz = n / seconds;
        DrawBar(seconds, seconds);
        return res;
    }

    // ---- binary format ---------------------------------------------------------

    // Header (32 B): "MHC1", u8 version=1, u8 proto, u16 reserved, u32 count,
    // u64 unix_start_us, u16 record_size, 10 B zero. Little-endian throughout.
    static void WriteMhc(string path, CaptureResult cap)
    {
        using var f = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(f);
        w.Write(Encoding.ASCII.GetBytes("MHC1"));
        w.Write((byte)1);
        w.Write(cap.Proto);
        w.Write((ushort)0);
        w.Write((uint)cap.Count);
        w.Write((ulong)cap.StartUnixUs);
        w.Write(cap.RecordSize);
        w.Write(new byte[10]);
        w.Write(cap.Data, 0, cap.Count * cap.RecordSize);
    }

    static void WriteU32(byte[] b, int o, uint v)
    { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
    static void WriteU16(byte[] b, int o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    static void WriteI16(byte[] b, int o, short v) => WriteU16(b, o, (ushort)v);

    static void DrawBar(double elapsed, double total)
    {
        double frac = Math.Min(elapsed / total, 1.0);
        int filled = (int)(frac * 30);
        int left = (int)Math.Ceiling(Math.Max(total - elapsed, 0));
        Console.Write($"\r  RECORDING  [{new string('#', filled)}{new string(' ', 30 - filled)}]  {left,2}s left ");
    }

    // ---- upload (presigned S3; logType must stay "deeplog") ----------------------

    static string? Upload(string zipPath, string nickname)
    {
        long fileSize = new FileInfo(zipPath).Length;
        if (fileSize > 500L * 1024 * 1024)
        {
            Console.WriteLine("  Bundle exceeds the 500 MB upload limit.");
            return null;
        }
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(10);

            var body = JsonSerializer.Serialize(new
            { nickname, logType = "deeplog", fileSize });
            var urlResp = client.PostAsync(UploadUrlEndpoint,
                new StringContent(body, Encoding.UTF8, "application/json")).Result;
            if (!urlResp.IsSuccessStatusCode)
            {
                Console.WriteLine($"  Failed to get upload URL: {urlResp.StatusCode}");
                return null;
            }
            var urlData = JsonSerializer.Deserialize<JsonElement>(urlResp.Content.ReadAsStringAsync().Result);
            string url = urlData.GetProperty("url").GetString() ?? "";
            string id = urlData.GetProperty("id").GetString() ?? "";

            using var content = new ByteArrayContent(File.ReadAllBytes(zipPath));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
            var putResp = client.PutAsync(url, content).Result;
            if (!putResp.IsSuccessStatusCode)
            {
                Console.WriteLine($"  Upload failed: {putResp.StatusCode}");
                return null;
            }
            return id;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Upload failed: {ex.Message}");
            return null;
        }
    }

    // ---- device detection (WMI; same as v1 / DeepPoll) ---------------------------

    static List<(string VidPid, string Name, bool Verified)> DetectMHDevices()
    {
        var found = new List<(string VidPid, string Name, bool Verified)>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE " +
                "PNPDeviceID LIKE 'USB\\\\VID_39AE%' OR PNPDeviceID LIKE 'USB\\\\VID_054C&PID_05C4%' OR " +
                "PNPDeviceID LIKE 'USB\\\\VID_1A86&PID_1235%' OR PNPDeviceID LIKE 'USB\\\\VID_1209&PID_0001%'");
            foreach (var obj in searcher.Get())
            {
                string id = obj["PNPDeviceID"]?.ToString() ?? "";
                var m = System.Text.RegularExpressions.Regex.Match(id, @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})");
                if (!m.Success) continue;
                string vidPid = $"{m.Groups[1].Value.ToUpper()}:{m.Groups[2].Value.ToUpper()}";
                if (found.Any(f => f.VidPid == vidPid)) continue;

                if (vidPid == "1209:0001")
                {
                    string busName = obj["Name"]?.ToString() ?? "";
                    if (busName.StartsWith("MH", StringComparison.OrdinalIgnoreCase))
                        found.Add((vidPid, $"{busName} (Setup Mode)", true));
                    else
                        found.Add((vidPid, "WebUSB Setup Device", false));
                }
                else
                {
                    found.Add((vidPid, DeviceDisplayName(vidPid), true));
                }
            }
        }
        catch { }
        return found;
    }

    // ---- system snapshot (carried over from v1) -----------------------------------

    static Dictionary<string, object> CollectSnapshot()
    {
        var snap = new Dictionary<string, object>
        {
            ["collectedUtc"] = DateTime.UtcNow.ToString("o"),
            ["deeplogVersion"] = VERSION,
        };

        try
        {
            using var s = new ManagementObjectSearcher("SELECT Caption, BuildNumber FROM Win32_OperatingSystem");
            foreach (var o in s.Get()) { snap["windows"] = $"{o["Caption"]} (build {o["BuildNumber"]})"; break; }
        }
        catch { }
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (var o in s.Get()) { snap["cpu"] = o["Name"]?.ToString()?.Trim() ?? ""; break; }
        }
        catch { }
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            foreach (var o in s.Get()) { snap["motherboard"] = $"{o["Manufacturer"]} {o["Product"]}"; break; }
        }
        catch { }

        snap["powerPlan"] = ParseActivePowerPlan();
        snap["usbSelectiveSuspend"] = ParseSelectiveSuspend();
        snap["fastStartup"] = ParseFastStartup();

        var controllers = new List<string>();
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE Service='USBXHCI'");
            foreach (var o in s.Get())
                controllers.Add($"{o["Name"]} [{o["DeviceID"]}]");
        }
        catch { }
        snap["usbControllers"] = controllers;

        var drivers = new Dictionary<string, string>();
        string driverDir = Path.Combine(Environment.SystemDirectory, "drivers");
        foreach (var drv in new[] { "USBXHCI.SYS", "UsbHub3.SYS", "xusb22.sys" })
        {
            try
            {
                string p = Path.Combine(driverDir, drv);
                if (File.Exists(p))
                    drivers[drv] = FileVersionInfo.GetVersionInfo(p).FileVersion ?? "unknown";
            }
            catch { }
        }
        snap["driverVersions"] = drivers;

        var inputSoftware = new List<string>();
        var watchProcs = new[] { "steam", "DS4Windows", "reWASD", "reWASDEngine", "reWASDService",
            "HidHideClient", "x360ce", "DualSenseX", "JoyShockMapper" };
        try
        {
            var running = Process.GetProcesses().Select(p => p.ProcessName).Distinct().ToList();
            foreach (var w in watchProcs)
                if (running.Any(r => r.Equals(w, StringComparison.OrdinalIgnoreCase)))
                    inputSoftware.Add($"{w} (running)");
        }
        catch { }
        foreach (var drv in new[] { "ViGEmBus.sys", "HidHide.sys" })
        {
            try
            {
                if (File.Exists(Path.Combine(driverDir, drv)))
                    inputSoftware.Add($"{drv} (installed)");
            }
            catch { }
        }
        snap["inputSoftware"] = inputSoftware;

        snap["mhDevices"] = DetectMHDevices()
            .Select(d => $"{d.VidPid} {d.Name}{(d.Verified ? "" : " (unverified)")}").ToList();

        return snap;
    }

    static string ParseActivePowerPlan()
    {
        string output = RunCommandCapture("powercfg", "/getactivescheme");
        var m = System.Text.RegularExpressions.Regex.Match(output, @"\(([^)]+)\)");
        return m.Success ? m.Groups[1].Value : "unknown";
    }

    static string ParseSelectiveSuspend()
    {
        string output = RunCommandCapture("powercfg",
            "/q SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
        string ac = "unknown", dc = "unknown";
        var mAc = System.Text.RegularExpressions.Regex.Match(output, @"AC Power Setting Index: 0x0000000(\d)");
        var mDc = System.Text.RegularExpressions.Regex.Match(output, @"DC Power Setting Index: 0x0000000(\d)");
        if (mAc.Success) ac = mAc.Groups[1].Value == "1" ? "enabled" : "disabled";
        if (mDc.Success) dc = mDc.Groups[1].Value == "1" ? "enabled" : "disabled";
        return $"AC: {ac}, DC: {dc}";
    }

    static string ParseFastStartup()
    {
        string output = RunCommandCapture("reg",
            "query \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Power\" /v HiberbootEnabled");
        if (output.Contains("0x1")) return "enabled";
        if (output.Contains("0x0")) return "disabled";
        return "unknown";
    }

    static string RunCommandCapture(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return output;
        }
        catch { return ""; }
    }
}
