// DeepLog v2.3 -- "record while I play": a session with no time limit.
//
// Full rate, every sample (XInput polled at 8 kHz, PS4 mode every report), written
// to disk as it goes so nothing is lost if the pad is pulled or the PC is short of
// memory. F9 drops a mark, F10 stops. What is SENT is small: 45 s before and 15 s
// after each mark, or the last 5 minutes when there are no marks.
//
// On-disk session file (session.raw, never uploaded as is): fixed records of
//   u64 t_us (since session start), then the body of the normal record:
//   XInput: u32 packet, i16 lx ly rx ry, u8 lt rt, u16 buttons, u8 phase, u8 connected, u16 pad  (28 B)
//   PS4:    u32 seq, byte[64] raw input report                                                    (76 B)
// The 64-bit time is why this file exists: the 32-bit t_us of an .mhc wraps after
// 71.6 minutes. Each sent part is cut out as an ordinary .mhc (same records as the
// 30-second test) with t_us counted from the start of that part.
//
// F9/F10 are RegisterHotKey hotkeys: Windows tells DeepLog when one of those two
// keys is pressed. No other key is seen.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

partial class Program
{
    const double MarkBeforeSec = 45, MarkAfterSec = 15, NoMarkTailSec = 300;
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
        public List<Dictionary<string, object>> Events = new();
        public int RawRecordSize => RecordSize + 4;
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

    // ---- F9 / F10 ---------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    sealed class Hotkeys
    {
        const uint WM_HOTKEY = 0x0312, WM_QUIT = 0x0012, MOD_NOREPEAT = 0x4000, VK_F9 = 0x78, VK_F10 = 0x79;
        readonly Thread thread;
        readonly ManualResetEventSlim ready = new();
        uint threadId;
        public bool MarkKey, StopKey;

        public Hotkeys(Action onMark, Action onStop)
        {
            thread = new Thread(() =>
            {
                threadId = GetCurrentThreadId();
                MarkKey = RegisterHotKey(IntPtr.Zero, 1, MOD_NOREPEAT, VK_F9);
                StopKey = RegisterHotKey(IntPtr.Zero, 2, MOD_NOREPEAT, VK_F10);
                ready.Set();
                while (GetMessage(out MSG m, IntPtr.Zero, 0, 0) > 0)
                {
                    if (m.message != WM_HOTKEY) continue;
                    if ((int)m.wParam == 1) onMark(); else if ((int)m.wParam == 2) onStop();
                }
                if (MarkKey) UnregisterHotKey(IntPtr.Zero, 1);
                if (StopKey) UnregisterHotKey(IntPtr.Zero, 2);
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

    /// Records until F10 / Ctrl+C / the pad goes away / the disk runs low.
    /// headlessSeconds > 0 stops by itself (bench); headlessMarks drops marks at those seconds.
    static LongCapture RecordLong(bool ps4, int xinputSlot, string workDir,
        double headlessSeconds, List<double> headlessMarks)
    {
        var cap = new LongCapture
        {
            Proto = (byte)(ps4 ? 1 : 0),
            RecordSize = (ushort)(ps4 ? 72 : 24),
            RawPath = Path.Combine(workDir, "session.raw"),
            StartUnixUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000,
        };
        var writer = new ChunkWriter(cap.RawPath, cap.RawRecordSize);
        cap.T0Ticks = Stopwatch.GetTimestamp();
        long t0 = cap.T0Ticks;

        string? stop = null;
        object gate = new();
        void Stop(string why) { lock (gate) stop ??= why; }
        void Mark()
        {
            long t = SessionUs(t0);
            lock (gate) cap.MarksUs.Add(t);
            ThreadPool.QueueUserWorkItem(_ => { try { Console.Beep(1200, 90); } catch { } });
        }

        var keys = new Hotkeys(Mark, () => Stop("stopped with F10"));
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; Stop("stopped with Ctrl+C"); };
        Console.CancelKeyPress += onCancel;
        if (!keys.MarkKey || !keys.StopKey)
        {
            Console.WriteLine("  Another program has taken F9 or F10, so that key will not work here.");
            Console.WriteLine("  Ctrl+C in this window stops the recording.");
            Console.WriteLine();
        }

        long count = 0;
        long lastSampleUs = 0;
        Thread worker;
        Microsoft.Win32.SafeHandles.SafeFileHandle? hid = null;
        var events = cap.Events;

        if (ps4)
        {
            string path = FindHidPath(0x054C, 0x05C4) ??
                throw new Exception("PS4-mode controller not found on the HID bus.");
            hid = CreateFile(path, GENERIC_READ, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (hid.IsInvalid) throw new Exception("Could not open the controller for reading.");
            HidD_GetPreparsedData(hid, out IntPtr pp);
            HidP_GetCaps(pp, out var caps);
            HidD_FreePreparsedData(pp);
            int reportLen = caps.InputReportByteLength;
            HidD_SetNumInputBuffers(hid, 512);
            var handle = hid;
            worker = new Thread(() =>
            {
                const int RAW = 64, REC = 76;
                var stream = new FileStream(handle, FileAccess.Read, reportLen, false);
                var buf = new byte[reportLen];
                uint seq = 0;
                while (Volatile.Read(ref stop) == null)
                {
                    int got;
                    try { got = stream.Read(buf, 0, reportLen); }
                    catch { break; }
                    if (got <= 0) break;
                    long tUs = SessionUs(t0);
                    var data = writer.Next(REC, out int o);
                    WriteU64(data, o, (ulong)tUs);
                    WriteU32(data, o + 8, seq++);
                    Array.Clear(data, o + 12, RAW);
                    Array.Copy(buf, 0, data, o + 12, Math.Min(got, RAW));
                    Volatile.Write(ref lastSampleUs, tUs);
                    Interlocked.Increment(ref count);
                }
                Stop("the controller was unplugged");
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
                        if (XInputGetState((uint)xinputSlot, out var st) == 0)
                        {
                            if (disconnected)
                            {
                                disconnected = false;
                                lock (gate) events.Add(new() { ["type"] = "reconnect", ["t_us"] = tUs });
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
                                lock (gate) events.Add(new() { ["type"] = "disconnect", ["t_us"] = tUs });
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

        // ---- status line, disk guard, bench marks --------------------------------
        string root = Path.GetPathRoot(Path.GetFullPath(workDir)) ?? "C:\\";
        var pendingMarks = new Queue<double>(headlessMarks.OrderBy(x => x));
        double lastLine = -1, lastDisk = -10;
        while (Volatile.Read(ref stop) == null)
        {
            Thread.Sleep(100);
            double el = SessionUs(t0) / 1e6;
            while (pendingMarks.Count > 0 && el >= pendingMarks.Peek()) { pendingMarks.Dequeue(); Mark(); }
            if (headlessSeconds > 0 && el >= headlessSeconds) Stop("stopped after the set time (bench)");
            if (writer.Error != null) Stop("the disk could not be written: " + writer.Error);
            if (ps4 && el > DisconnectStopSec && el - Volatile.Read(ref lastSampleUs) / 1e6 > DisconnectStopSec)
                Stop("the controller stopped sending");
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

        hid?.Close();                                                   // unblocks the PS4 read
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

    record Part(long StartUs, long EndUs, List<long> MarksUs);

    /// "marks": 45 s before to 15 s after each mark, overlapping ones joined.
    /// "tail": the last 5 minutes. "everything": the whole session.
    static List<Part> PlanParts(LongCapture cap, string what)
    {
        long dur = cap.DurationUs;
        var spans = new List<(long A, long B)>();
        if (what == "everything") spans.Add((0, dur));
        else if (what == "tail") spans.Add((Math.Max(0, dur - (long)(NoMarkTailSec * 1e6)), dur));
        else
            foreach (long m in cap.MarksUs.OrderBy(x => x))
            {
                long a = Math.Max(0, m - (long)(MarkBeforeSec * 1e6)), b = Math.Min(dur, m + (long)(MarkAfterSec * 1e6));
                if (spans.Count > 0 && a <= spans[^1].B) spans[^1] = (spans[^1].A, Math.Max(spans[^1].B, b));
                else spans.Add((a, b));
            }

        var parts = new List<Part>();
        long maxUs = (long)(MaxPartSec * 1e6);
        foreach (var (a, b) in spans)
            for (long s = a; s < b || (s == a && a == b); s += maxUs)
            {
                long e = Math.Min(b, s + maxUs);
                parts.Add(new Part(s, e, cap.MarksUs.Where(m => m >= s && m <= e).OrderBy(x => x).ToList()));
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
    static long WritePart(LongCapture cap, Part part, string outPath)
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
                long t = (long)BitConverter.ToUInt64(inBuf, i * rawRec) - part.StartUs;
                WriteU32(outBuf, i * rec, (uint)Math.Max(0, t));
                Buffer.BlockCopy(inBuf, i * rawRec + 8, outBuf, i * rec + 4, rec - 4);
            }
            w.Write(outBuf, 0, take * rec);
            left -= take;
        }
        return n;
    }

    static void WriteU64(byte[] b, int o, ulong v)
    { WriteU32(b, o, (uint)v); WriteU32(b, o + 4, (uint)(v >> 32)); }
}
