// DeepLog v2.3 -- "record while I play": a session with no time limit.
//
// Full rate, every sample (XInput polled at 8 kHz, PS4 mode every report), written
// to disk as it goes so nothing is lost if the pad is pulled or the PC is short of
// memory. F9 marks "that felt late", F8 marks "the game lost my pad", F10 stops.
// What is SENT is small: 45 s before (60 s for F8) and 15 s after each mark, or the
// last 5 minutes when there are no marks.
//
// On-disk session file (session.raw, never uploaded as is): fixed records of
//   u64 t_us (since session start), then the body of the normal record:
//   XInput: u32 packet, i16 lx ly rx ry, u8 lt rt, u16 buttons, u8 phase, u8 connected, u16 pad  (28 B)
//   PS4:    u32 seq, byte[64] raw input report                                                    (76 B)
// The 64-bit time is why this file exists: the 32-bit t_us of an .mhc wraps after
// 71.6 minutes. Each sent part is cut out as an ordinary .mhc (same records as the
// 30-second test) with t_us counted from the start of that part.
//
// F8/F9/F10 are RegisterHotKey hotkeys: Windows tells DeepLog when one of those
// keys is pressed. No other key is seen.
//
// v2.3.2: device events with times (pad removed/arrived, read errors; PS4 mode now
// keeps recording across an unplug), the programs that talk to gamepads at the start
// and at each mark, and a gap summary per sent part.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

partial class Program
{
    const double MarkBeforeSec = 45, LostBeforeSec = 60, MarkAfterSec = 15, NoMarkTailSec = 300;
    const double MaxPartSec = 3600;                    // one .mhc part never reaches the 32-bit wrap
    const long DiskGuardBytes = 2L * 1024 * 1024 * 1024;
    const double DisconnectStopSec = 30;
    const long SendEverythingMaxRaw = 1024L * 1024 * 1024;

    class LongCapture
    {
        public byte Proto;
        public ushort RecordSize;          // .mhc record size (24 or 72); the raw file has 4 more
        public string RawPath = "";
        public long Count;
        public long DurationUs;
        public long StartUnixUs;
        public long T0Ticks;
        public double AchievedHz;
        public string StopReason = "";
        public List<long> MarksUs = new();
        public List<string> MarkKinds = new();         // parallel to MarksUs: "late" (F9) or "lost" (F8)
        public List<Dictionary<string, object?>> Programs = new();
        public List<Dictionary<string, object>> Events = new();
        public int RawRecordSize => RecordSize + 4;
        public List<(long T, string Kind)> Marks() =>
            MarksUs.Zip(MarkKinds, (t, k) => (t, k)).OrderBy(m => m.t).ToList();
    }

    // ---- programs that talk to gamepads ------------------------------------------

    static readonly string[] PadProgramPrefixes =
    {
        "steam", "GameOverlayUI", "EpicGamesLauncher", "EOSOverlayRenderer", "EpicWebHelper",
        "DS4Windows", "HidHideClient", "reWASD", "x360ce", "DualSenseX", "JoyShockMapper",
        "Battle.net", "EADesktop", "EABackgroundService", "UbisoftConnect", "upc", "GalaxyClient",
        "RiotClientServices", "NVIDIA Overlay", "NVIDIA Share", "NVIDIA app", "RTSS", "MSIAfterburner",
        "Discord", "GameBar", "XboxPcApp", "XboxGameBar", "obs64",
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi",
    };

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    /// Names only: which of the known launcher / overlay / controller / browser programs
    /// are running, and the program in front. No window titles, no paths.
    static Dictionary<string, object?> ProgramsNow(string at, double atS)
    {
        var row = new Dictionary<string, object?> { ["at"] = at, ["atS"] = Math.Round(atS, 3) };
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            row["foreground"] = pid == 0 ? null : Process.GetProcessById((int)pid).ProcessName;
        }
        catch { row["foreground"] = null; }
        try
        {
            row["running"] = Process.GetProcesses().Select(p => p.ProcessName).Distinct()
                .Where(n => PadProgramPrefixes.Any(w => n.StartsWith(w, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) { row["error"] = ex.Message; }
        return row;
    }

    /// Hands full buffers to a writer thread so the sampling thread never waits on the disk.
    sealed class ChunkWriter
    {
        readonly FileStream file;
        readonly BlockingCollection<(byte[] Buf, int Len)> queue = new();
        readonly ConcurrentBag<byte[]> pool = new();
        readonly Thread thread;
        readonly int chunkBytes;
        byte[] cur;
        int pos;
        long written;
        public volatile string? Error;
        public long BytesQueued => Interlocked.Read(ref written);

        public ChunkWriter(string path, int recordSize)
        {
            chunkBytes = recordSize * (1024 * 1024 / recordSize);
            cur = new byte[chunkBytes];
            file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            thread = new Thread(() =>
            {
                foreach (var (buf, len) in queue.GetConsumingEnumerable())
                {
                    try { if (Error == null) file.Write(buf, 0, len); }
                    catch (Exception ex) { Error = ex.Message; }
                    pool.Add(buf);
                }
            }) { IsBackground = true };
            thread.Start();
        }

        /// Buffer and offset for the next record. Single producer only.
        public byte[] Next(int recordSize, out int offset)
        {
            if (pos + recordSize > chunkBytes) Hand();
            offset = pos;
            pos += recordSize;
            Interlocked.Add(ref written, recordSize);
            return cur;
        }

        void Hand()
        {
            queue.Add((cur, pos));
            cur = pool.TryTake(out var b) ? b : new byte[chunkBytes];
            pos = 0;
        }

        public void Close()
        {
            if (pos > 0) Hand();
            queue.CompleteAdding();
            thread.Join();
            file.Dispose();
        }
    }

    // ---- F8 / F9 / F10 -----------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    sealed class Hotkeys
    {
        const uint WM_HOTKEY = 0x0312, WM_QUIT = 0x0012, MOD_NOREPEAT = 0x4000, VK_F8 = 0x77, VK_F9 = 0x78, VK_F10 = 0x79;
        readonly Thread thread;
        readonly ManualResetEventSlim ready = new();
        uint threadId;
        public bool MarkKey, StopKey, LostKey;

        public Hotkeys(Action onMark, Action onLost, Action onStop)
        {
            thread = new Thread(() =>
            {
                threadId = GetCurrentThreadId();
                MarkKey = RegisterHotKey(IntPtr.Zero, 1, MOD_NOREPEAT, VK_F9);
                StopKey = RegisterHotKey(IntPtr.Zero, 2, MOD_NOREPEAT, VK_F10);
                LostKey = RegisterHotKey(IntPtr.Zero, 3, MOD_NOREPEAT, VK_F8);
                ready.Set();
                while (GetMessage(out MSG m, IntPtr.Zero, 0, 0) > 0)
                {
                    if (m.message != WM_HOTKEY) continue;
                    switch ((int)m.wParam) { case 1: onMark(); break; case 2: onStop(); break; case 3: onLost(); break; }
                }
                if (MarkKey) UnregisterHotKey(IntPtr.Zero, 1);
                if (StopKey) UnregisterHotKey(IntPtr.Zero, 2);
                if (LostKey) UnregisterHotKey(IntPtr.Zero, 3);
            }) { IsBackground = true };
            thread.Start();
            ready.Wait(2000);
        }

        public void Close()
        {
            if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            thread.Join(1000);
        }
    }

    // ---- the session ----------------------------------------------------------------

    static long SessionUs(long t0Ticks) =>
        (Stopwatch.GetTimestamp() - t0Ticks) * 1_000_000 / Stopwatch.Frequency;

    static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// Records until F10 / Ctrl+C / the pad stays away / the disk runs low.
    /// headlessSeconds > 0 stops by itself (bench); headlessMarks drops marks at those seconds.
    static LongCapture RecordLong(bool ps4, int xinputSlot, string vidPid, string workDir,
        double headlessSeconds, List<(double AtS, string Kind)> headlessMarks)
    {
        var cap = new LongCapture
        {
            Proto = (byte)(ps4 ? 1 : 0),
            RecordSize = (ushort)(ps4 ? 72 : 24),
            RawPath = Path.Combine(workDir, "session.raw"),
            StartUnixUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000,
        };
        cap.Programs.Add(ProgramsNow("start", 0));
        var writer = new ChunkWriter(cap.RawPath, cap.RawRecordSize);
        cap.T0Ticks = Stopwatch.GetTimestamp();
        long t0 = cap.T0Ticks;

        string? stop = null;
        object gate = new();
        var events = cap.Events;
        void Stop(string why) { lock (gate) stop ??= why; }
        void Event(string type, long tUs, string? detail = null)
        {
            var e = new Dictionary<string, object> { ["type"] = type, ["t_us"] = tUs };
            if (detail != null) e["detail"] = detail;
            lock (gate) events.Add(e);
        }
        void Mark(string kind)
        {
            long t = SessionUs(t0);
            lock (gate) { cap.MarksUs.Add(t); cap.MarkKinds.Add(kind); }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Console.Beep(kind == "lost" ? 700 : 1200, 90); } catch { }
                var row = ProgramsNow("mark-" + kind, t / 1e6);
                lock (gate) cap.Programs.Add(row);
            });
        }

        var keys = new Hotkeys(() => Mark("late"), () => Mark("lost"), () => Stop("stopped with F10"));
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; Stop("stopped with Ctrl+C"); };
        Console.CancelKeyPress += onCancel;
        if (!keys.MarkKey || !keys.StopKey || !keys.LostKey)
        {
            Console.WriteLine("  Another program has taken F8, F9 or F10, so that key will not work here.");
            Console.WriteLine("  Ctrl+C in this window stops the recording.");
            Console.WriteLine();
        }

        long count = 0;
        long lastSampleUs = 0;
        Thread worker;
        Microsoft.Win32.SafeHandles.SafeFileHandle? hid = null;

        if (ps4)
        {
            // Reopens the pad after an unplug or a read error, so a disconnect in the middle
            // of a match is recorded with its time instead of ending the session.
            worker = new Thread(() =>
            {
                const int RAW = 64, REC = 76;
                uint seq = 0;
                long goneSinceUs = -1;
                bool everOpened = false;
                while (Volatile.Read(ref stop) == null)
                {
                    string? path = FindHidPath(0x054C, 0x05C4);
                    var h = path == null ? null
                        : CreateFile(path, GENERIC_READ, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (h == null || h.IsInvalid)
                    {
                        long now = SessionUs(t0);
                        if (goneSinceUs < 0) goneSinceUs = now;
                        if (now - goneSinceUs > DisconnectStopSec * 1e6)
                        {
                            Stop(everOpened ? "the controller was unplugged" : "the controller could not be opened");
                            break;
                        }
                        Thread.Sleep(200);
                        continue;
                    }
                    if (goneSinceUs >= 0 && everOpened) Event("reconnect", SessionUs(t0));
                    goneSinceUs = -1;
                    everOpened = true;

                    HidD_GetPreparsedData(h, out IntPtr pp);
                    HidP_GetCaps(pp, out var caps);
                    HidD_FreePreparsedData(pp);
                    int reportLen = Math.Max((int)caps.InputReportByteLength, 1);
                    HidD_SetNumInputBuffers(h, 512);
                    lock (gate) hid = h;
                    string? error = null;
                    try
                    {
                        var stream = new FileStream(h, FileAccess.Read, reportLen, false);
                        var buf = new byte[reportLen];
                        while (Volatile.Read(ref stop) == null)
                        {
                            int got = stream.Read(buf, 0, reportLen);
                            if (got <= 0) { error = "read returned nothing"; break; }
                            long tUs = SessionUs(t0);
                            var data = writer.Next(REC, out int o);
                            WriteU64(data, o, (ulong)tUs);
                            WriteU32(data, o + 8, seq++);
                            Array.Clear(data, o + 12, RAW);
                            Array.Copy(buf, 0, data, o + 12, Math.Min(got, RAW));
                            Volatile.Write(ref lastSampleUs, tUs);
                            Interlocked.Increment(ref count);
                        }
                    }
                    catch (Exception ex) { error = ex.Message; }
                    lock (gate) hid = null;
                    try { h.Dispose(); } catch { }
                    if (Volatile.Read(ref stop) == null)
                    {
                        goneSinceUs = SessionUs(t0);
                        Event("hid-read-error", goneSinceUs, error);
                    }
                }
            }) { IsBackground = true };
        }
        else
        {
            worker = new Thread(() =>
            {
                const int REC = 28;
                timeBeginPeriod(1);
                try
                {
                    long periodTicks = (long)(Stopwatch.Frequency / XInputTargetHz);
                    long nextTick = Stopwatch.GetTimestamp();
                    bool disconnected = false;
                    long disconnectedAtUs = 0;
                    while (Volatile.Read(ref stop) == null)
                    {
                        long now = Stopwatch.GetTimestamp();
                        if (nextTick - now > 0) continue;              // 125 us period: spin, as the 30 s test does
                        nextTick += periodTicks;
                        if (nextTick < now - Stopwatch.Frequency / 20) nextTick = now + periodTicks;

                        long tUs = (now - t0) * 1_000_000 / Stopwatch.Frequency;
                        uint rc = XInputGetState((uint)xinputSlot, out var st);
                        if (rc == 0)
                        {
                            if (disconnected)
                            {
                                disconnected = false;
                                Event("reconnect", tUs);
                            }
                            var data = writer.Next(REC, out int o);
                            WriteU64(data, o, (ulong)tUs);
                            WriteU32(data, o + 8, st.dwPacketNumber);
                            WriteI16(data, o + 12, st.Gamepad.sThumbLX);
                            WriteI16(data, o + 14, st.Gamepad.sThumbLY);
                            WriteI16(data, o + 16, st.Gamepad.sThumbRX);
                            WriteI16(data, o + 18, st.Gamepad.sThumbRY);
                            data[o + 20] = st.Gamepad.bLeftTrigger;
                            data[o + 21] = st.Gamepad.bRightTrigger;
                            WriteU16(data, o + 22, st.Gamepad.wButtons);
                            data[o + 24] = 0;
                            data[o + 25] = 1;
                            WriteU16(data, o + 26, 0);
                            Volatile.Write(ref lastSampleUs, tUs);
                            Interlocked.Increment(ref count);
                        }
                        else
                        {
                            if (!disconnected)
                            {
                                disconnected = true;
                                disconnectedAtUs = tUs;
                                Event("disconnect", tUs, $"XInputGetState returned {rc}");
                            }
                            else if (tUs - disconnectedAtUs > DisconnectStopSec * 1e6)
                                Stop("the controller was unplugged");
                            Thread.Sleep(1);                           // nothing to sample while it is gone
                        }
                    }
                }
                finally { timeEndPeriod(1); }
            }) { IsBackground = true };
        }
        worker.Start();

        // ---- status line, disk guard, device presence, bench marks ----------------
        string root = Path.GetPathRoot(Path.GetFullPath(workDir)) ?? "C:\\";
        var pendingMarks = new Queue<(double AtS, string Kind)>(headlessMarks.OrderBy(x => x.AtS));
        string? usbPrefix = vidPid.Length == 9 && vidPid[4] == ':'
            ? $@"USB\VID_{vidPid[..4]}&PID_{vidPid[5..]}" : null;
        int PadNodes()
        {
            try { return DevNode.PresentUsb().Count(id => id.StartsWith(usbPrefix!, StringComparison.OrdinalIgnoreCase)); }
            catch { return -1; }
        }
        int lastNodes = usbPrefix == null ? -1 : PadNodes();
        double lastLine = -1, lastDisk = -10, lastUsb = 0;
        while (Volatile.Read(ref stop) == null)
        {
            Thread.Sleep(100);
            double el = SessionUs(t0) / 1e6;
            while (pendingMarks.Count > 0 && el >= pendingMarks.Peek().AtS) Mark(pendingMarks.Dequeue().Kind);
            if (headlessSeconds > 0 && el >= headlessSeconds) Stop("stopped after the set time (bench)");
            if (writer.Error != null) Stop("the disk could not be written: " + writer.Error);
            if (ps4 && el > DisconnectStopSec && el - Volatile.Read(ref lastSampleUs) / 1e6 > DisconnectStopSec)
                Stop("the controller stopped sending");
            if (usbPrefix != null && el - lastUsb >= 0.5)
            {
                lastUsb = el;
                int nodes = PadNodes();
                if (nodes >= 0 && lastNodes >= 0 && nodes != lastNodes)
                    Event(nodes < lastNodes ? "device-removed" : "device-arrived", SessionUs(t0),
                        $"USB nodes for {vidPid}: {lastNodes} -> {nodes} (checked every 0.5 s)");
                if (nodes >= 0) lastNodes = nodes;
            }
            if (el - lastDisk >= 2)
            {
                lastDisk = el;
                try { if (new DriveInfo(root).AvailableFreeSpace < DiskGuardBytes) Stop("the disk is nearly full"); }
                catch { }
            }
            if (el - lastLine >= 1)
            {
                lastLine = el;
                int marks; lock (gate) marks = cap.MarksUs.Count;
                Console.Write($"\r  RECORDING  {Clock(el),8}   marks: {marks}   {writer.BytesQueued / 1048576.0,7:F0} MB on disk   ");
            }
        }

        lock (gate) { try { hid?.Close(); } catch { } }                 // unblocks the PS4 read
        worker.Join(3000);
        keys.Close();
        Console.CancelKeyPress -= onCancel;
        writer.Close();

        cap.Count = Interlocked.Read(ref count);
        cap.DurationUs = Math.Max(Volatile.Read(ref lastSampleUs), 1);
        cap.AchievedHz = cap.Count / (cap.DurationUs / 1e6);
        cap.StopReason = stop ?? "stopped";
        return cap;
    }

    // ---- what is sent ------------------------------------------------------------------

    record Part(long StartUs, long EndUs, List<(long T, string Kind)> Marks);

    /// "marks": 45 s (60 s for a "lost" mark) before to 15 s after each mark, overlapping
    /// ones joined. "tail": the last 5 minutes. "everything": the whole session.
    static List<Part> PlanParts(LongCapture cap, string what)
    {
        long dur = cap.DurationUs;
        var marks = cap.Marks();
        var spans = new List<(long A, long B)>();
        if (what == "everything") spans.Add((0, dur));
        else if (what == "tail") spans.Add((Math.Max(0, dur - (long)(NoMarkTailSec * 1e6)), dur));
        else
            foreach (var (m, kind) in marks)
            {
                double before = kind == "lost" ? LostBeforeSec : MarkBeforeSec;
                long a = Math.Max(0, m - (long)(before * 1e6)), b = Math.Min(dur, m + (long)(MarkAfterSec * 1e6));
                spans.Add((a, b));
            }
        // join overlapping spans (a "lost" mark can start before the span of an earlier mark)
        spans = spans.OrderBy(s => s.A).ToList();
        var joined = new List<(long A, long B)>();
        foreach (var s in spans)
            if (joined.Count > 0 && s.A <= joined[^1].B) joined[^1] = (joined[^1].A, Math.Max(joined[^1].B, s.B));
            else joined.Add(s);

        var parts = new List<Part>();
        long maxUs = (long)(MaxPartSec * 1e6);
        foreach (var (a, b) in joined)
            for (long s = a; s < b || (s == a && a == b); s += maxUs)
            {
                long e = Math.Min(b, s + maxUs);
                parts.Add(new Part(s, e, marks.Where(m => m.T >= s && m.T <= e).ToList()));
                if (a == b) break;
            }
        return parts;
    }

    /// First record index whose time is >= tUs (records are in time order).
    static long LowerBound(FileStream raw, int rawRec, long total, long tUs)
    {
        var b = new byte[8];
        long lo = 0, hi = total;
        while (lo < hi)
        {
            long mid = (lo + hi) / 2;
            raw.Position = mid * rawRec;
            raw.ReadExactly(b, 0, 8);
            if ((long)BitConverter.ToUInt64(b, 0) < tUs) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// Cuts one part out of session.raw as an ordinary .mhc. Header as WriteMhc, plus
    /// the part's offset into the session (u64 us) in the first 8 of the 10 spare bytes.
    /// Returns the sample count and a gap summary for meta.json.
    static (long Samples, Dictionary<string, object?> Gaps) WritePart(LongCapture cap, Part part, string outPath)
    {
        int rawRec = cap.RawRecordSize, rec = cap.RecordSize;
        using var raw = new FileStream(cap.RawPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        long total = raw.Length / rawRec;
        long first = LowerBound(raw, rawRec, total, part.StartUs);
        long end = part.EndUs >= cap.DurationUs ? total : LowerBound(raw, rawRec, total, part.EndUs + 1);
        long n = Math.Max(0, end - first);

        using var f = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var w = new BinaryWriter(f);
        w.Write(Encoding.ASCII.GetBytes("MHC1"));
        w.Write((byte)1);
        w.Write(cap.Proto);
        w.Write((ushort)0);
        w.Write((uint)n);
        w.Write((ulong)(cap.StartUnixUs + part.StartUs));
        w.Write(cap.RecordSize);
        w.Write((ulong)part.StartUs);
        w.Write((ushort)0);

        // gap summary: time between consecutive samples, in buckets
        long[] bucket = new long[5];                        // <150 us, 150-500 us, 0.5-2 ms, 2-10 ms, >10 ms
        long prevT = -1, maxGap = 0, maxGapAt = 0;
        long counterJumps = 0, counterSteps = 0;            // PS4: the report's own 6-bit counter (byte 7 >> 2)
        long pkt0 = 0, pkt1 = 0, pkt2 = 0, pktMore = 0;     // XInput: packet number advance per sample
        int prevCounter = -1;
        uint prevPacket = 0;

        raw.Position = first * rawRec;
        var inBuf = new byte[rawRec * 4096];
        var outBuf = new byte[rec * 4096];
        long left = n;
        while (left > 0)
        {
            int take = (int)Math.Min(left, 4096);
            raw.ReadExactly(inBuf, 0, take * rawRec);
            for (int i = 0; i < take; i++)
            {
                int b = i * rawRec;
                long tAbs = (long)BitConverter.ToUInt64(inBuf, b);
                long t = tAbs - part.StartUs;
                WriteU32(outBuf, i * rec, (uint)Math.Max(0, t));
                Buffer.BlockCopy(inBuf, b + 8, outBuf, i * rec + 4, rec - 4);

                if (prevT >= 0)
                {
                    long gap = tAbs - prevT;
                    bucket[gap < 150 ? 0 : gap < 500 ? 1 : gap < 2000 ? 2 : gap < 10000 ? 3 : 4]++;
                    if (gap > maxGap) { maxGap = gap; maxGapAt = prevT; }
                    if (cap.Proto == 1)
                    {
                        int c = inBuf[b + 12 + 7] >> 2;
                        counterSteps++;
                        if (((c - prevCounter) & 63) != 1) counterJumps++;
                    }
                    else
                    {
                        uint d = BitConverter.ToUInt32(inBuf, b + 8) - prevPacket;
                        if (d == 0) pkt0++; else if (d == 1) pkt1++; else if (d == 2) pkt2++; else pktMore++;
                    }
                }
                prevT = tAbs;
                if (cap.Proto == 1) prevCounter = inBuf[b + 12 + 7] >> 2;
                else prevPacket = BitConverter.ToUInt32(inBuf, b + 8);
            }
            w.Write(outBuf, 0, take * rec);
            left -= take;
        }

        var gaps = new Dictionary<string, object?>
        {
            ["under150us"] = bucket[0], ["us150to500"] = bucket[1], ["ms0_5to2"] = bucket[2],
            ["ms2to10"] = bucket[3], ["over10ms"] = bucket[4],
            ["largestGapUs"] = maxGap,
            ["largestGapAtS"] = Math.Round(maxGapAt / 1e6, 3),
            ["note"] = cap.Proto == 1
                ? "time between consecutive reports as DeepLog's read returned them"
                : "time between DeepLog's own XInput samples (its loop, not the pad's reports)",
        };
        if (cap.Proto == 1)
            gaps["reportCounter"] = new Dictionary<string, object?>
            {
                ["steps"] = counterSteps, ["notPlusOne"] = counterJumps,
                ["how"] = "byte 7 >> 2 of each report, expected to go up by 1 (mod 64)",
            };
        else
            gaps["packetAdvancePerSample"] = new Dictionary<string, object?>
            { ["0"] = pkt0, ["1"] = pkt1, ["2"] = pkt2, ["more"] = pktMore };
        return (n, gaps);
    }

    static void WriteU64(byte[] b, int o, ulong v)
    { WriteU32(b, o, (uint)v); WriteU32(b, o + 4, (uint)(v >> 32)); }
}
