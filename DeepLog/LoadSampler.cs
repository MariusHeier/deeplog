// Machine load sampled DURING the recording, on its own below-normal thread at
// a modest rate so it never competes with the 8 kHz XInput loop. Ported from
// deepscan experiments/deepexp/Monitor.cs (tested; no admin, no WMI):
//   system CPU      GetSystemTimes
//   per-core CPU,   NtQuerySystemInformation(SystemProcessorPerformanceInformation)
//   DPC, interrupt
//   RAM             GlobalMemoryStatusEx
//   top processes   NtQuerySystemInformation(SystemProcessInformation), CPU delta
//                   between two censuses (a census costs a few ms, so it runs 1/s)
// GPU is not sampled: deepscan logs it through nvidia-smi, which is neither
// cheap nor present on every machine.
//
// Timestamps are raw QPC ticks, converted afterwards to t_us relative to the
// capture's own start tick -- the same clock as the t_us in data.mhc.

using System.Diagnostics;
using System.Runtime.InteropServices;

sealed class LoadSampler
{
    [DllImport("ntdll.dll")]
    static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int need);
    [DllImport("kernel32.dll")]
    static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                     ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    // 48 bytes per logical CPU on x64
    [StructLayout(LayoutKind.Sequential)]
    struct SPPI
    {
        public long IdleTime, KernelTime, UserTime, DpcTime, InterruptTime;
        public uint InterruptCount, Pad;
    }

    const int SystemProcessInformation = 5, SystemProcessorPerformanceInformation = 8;
    // x64 offsets into SYSTEM_PROCESS_INFORMATION (Monitor.cs)
    const int OFF_NEXT = 0x00, OFF_USER = 0x28, OFF_KERNEL = 0x30,
              OFF_NAME_LEN = 0x38, OFF_NAME_PTR = 0x40, OFF_PID = 0x50, OFF_WS = 0x90;

    const double Hz = 4.0;                 // system + per-core rows
    const double CensusEverySec = 1.0;     // top-process table
    const int TopN = 5;

    readonly Thread thread;
    volatile bool stop;
    readonly int nCpu = Environment.ProcessorCount;
    readonly List<(long tick, double cpu, double maxCore, double dpc, double isr, double intPerSec,
                   double ramUsedMb, uint ramLoad)> sys = new();
    readonly List<(long tick, double[] busy, double[] dpc, double[] isr)> cores = new();
    readonly List<(long tick, List<(string name, double cpu, double wsMb)> top)> procs = new();
    double costSumUs, costMaxUs;
    int ticks, failures;
    string? error;

    public LoadSampler()
    {
        thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "load-sampler" };
    }

    public void Start() => thread.Start();

    public void Stop()
    {
        stop = true;
        thread.Join(3000);
    }

    void Run()
    {
        try { Loop(); }
        catch (Exception ex) { error = ex.Message; }         // never takes the recording down
    }

    void Loop()
    {
        IntPtr procBuf = Marshal.AllocHGlobal(1 << 20);
        int procBufLen = 1 << 20;
        int sppiSize = Marshal.SizeOf<SPPI>();
        IntPtr coreBuf = Marshal.AllocHGlobal(nCpu * sppiSize);
        try
        {
            var prevCores = ReadCores(coreBuf, sppiSize);
            long prevCoreTick = Stopwatch.GetTimestamp();
            bool sysOk = GetSystemTimes(out long pIdle, out long pKern, out long pUser);
            var prevProcs = Census(ref procBuf, ref procBufLen);
            long prevCensusTick = Stopwatch.GetTimestamp();
            long period = (long)(Stopwatch.Frequency / Hz);
            long next = Stopwatch.GetTimestamp() + period;

            while (!stop)
            {
                long wait = next - Stopwatch.GetTimestamp();
                if (wait > 0) { Thread.Sleep((int)Math.Max(1, wait * 1000 / Stopwatch.Frequency)); continue; }
                next += period;
                if (next < Stopwatch.GetTimestamp()) next = Stopwatch.GetTimestamp() + period;

                long c0 = Stopwatch.GetTimestamp();
                ticks++;
                double cpu = double.NaN;
                if (GetSystemTimes(out long idle, out long kern, out long user))
                {
                    double dI = idle - pIdle, dK = kern - pKern, dU = user - pUser, tot = dK + dU;
                    if (sysOk && tot > 0) cpu = Math.Clamp((tot - dI) / tot * 100.0, 0, 100);
                    pIdle = idle; pKern = kern; pUser = user; sysOk = true;
                }
                else failures++;

                var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                bool memOk = GlobalMemoryStatusEx(ref mem);
                double usedMb = memOk ? (mem.ullTotalPhys - mem.ullAvailPhys) / 1048576.0 : double.NaN;

                double maxCore = double.NaN, dpcAll = double.NaN, isrAll = double.NaN, intPerSec = double.NaN;
                var cur = ReadCores(coreBuf, sppiSize);
                long coreTick = Stopwatch.GetTimestamp();
                if (cur != null && prevCores != null)
                {
                    var busy = new double[nCpu]; var dpc = new double[nCpu]; var isr = new double[nCpu];
                    maxCore = dpcAll = isrAll = 0;
                    double ints = 0;
                    for (int c = 0; c < nCpu; c++)
                    {
                        double dI = cur[c].IdleTime - prevCores[c].IdleTime;
                        double dK = cur[c].KernelTime - prevCores[c].KernelTime;   // includes idle + dpc
                        double dU = cur[c].UserTime - prevCores[c].UserTime;
                        double tot = dK + dU;
                        if (tot <= 0) continue;
                        busy[c] = Math.Clamp((tot - dI) / tot * 100.0, 0, 100);
                        dpc[c] = Math.Clamp((cur[c].DpcTime - prevCores[c].DpcTime) / tot * 100.0, 0, 100);
                        isr[c] = Math.Clamp((cur[c].InterruptTime - prevCores[c].InterruptTime) / tot * 100.0, 0, 100);
                        maxCore = Math.Max(maxCore, busy[c]);
                        dpcAll += dpc[c] / nCpu; isrAll += isr[c] / nCpu;
                        ints += unchecked(cur[c].InterruptCount - prevCores[c].InterruptCount);
                    }
                    intPerSec = ints * Stopwatch.Frequency / Math.Max(1, coreTick - prevCoreTick);
                    cores.Add((c0, busy, dpc, isr));
                }
                else failures++;
                if (cur != null) { prevCores = cur; prevCoreTick = coreTick; }

                sys.Add((c0, cpu, maxCore, dpcAll, isrAll, intPerSec, usedMb, memOk ? mem.dwMemoryLoad : 0));

                if ((c0 - prevCensusTick) >= CensusEverySec * Stopwatch.Frequency)
                {
                    var now = Census(ref procBuf, ref procBufLen);
                    long censusTick = Stopwatch.GetTimestamp();
                    if (now != null && prevProcs != null)
                    {
                        double sec = (censusTick - prevCensusTick) / (double)Stopwatch.Frequency;
                        var top = now.Where(kv => kv.Key > 0 && prevProcs.ContainsKey(kv.Key) &&
                                                  prevProcs[kv.Key].name == kv.Value.name)
                            .Select(kv => (name: kv.Value.name,
                                cpu: (kv.Value.cpu100ns - prevProcs[kv.Key].cpu100ns) / 1e7 / sec / nCpu * 100.0,
                                wsMb: kv.Value.ws / 1048576.0))
                            .OrderByDescending(p => p.cpu).Take(TopN).ToList();
                        procs.Add((censusTick, top));
                    }
                    else failures++;
                    if (now != null) { prevProcs = now; prevCensusTick = censusTick; }
                }

                double cost = (Stopwatch.GetTimestamp() - c0) * 1e6 / Stopwatch.Frequency;
                costSumUs += cost; costMaxUs = Math.Max(costMaxUs, cost);
            }
        }
        finally { Marshal.FreeHGlobal(procBuf); Marshal.FreeHGlobal(coreBuf); }
    }

    SPPI[]? ReadCores(IntPtr buf, int size)
    {
        if (NtQuerySystemInformation(SystemProcessorPerformanceInformation, buf, nCpu * size, out _) != 0) return null;
        var arr = new SPPI[nCpu];
        for (int i = 0; i < nCpu; i++) arr[i] = Marshal.PtrToStructure<SPPI>(buf + i * size);
        return arr;
    }

    Dictionary<int, (string name, long cpu100ns, long ws)>? Census(ref IntPtr buf, ref int len)
    {
        for (int attempt = 0; ; attempt++)
        {
            int st = NtQuerySystemInformation(SystemProcessInformation, buf, len, out int need);
            if (st == 0) break;
            if ((uint)st != 0xC0000004 || attempt > 4) return null;   // not STATUS_INFO_LENGTH_MISMATCH
            Marshal.FreeHGlobal(buf);
            len = Math.Max(need + 65536, len * 2);
            buf = Marshal.AllocHGlobal(len);
        }
        var res = new Dictionary<int, (string, long, long)>(512);
        IntPtr p = buf;
        while (true)
        {
            int next = Marshal.ReadInt32(p, OFF_NEXT);
            long cpu = Marshal.ReadInt64(p, OFF_USER) + Marshal.ReadInt64(p, OFF_KERNEL);
            long ws = Marshal.ReadInt64(p, OFF_WS);
            int pid = (int)Marshal.ReadIntPtr(p, OFF_PID);
            short nlen = Marshal.ReadInt16(p, OFF_NAME_LEN);
            IntPtr nptr = Marshal.ReadIntPtr(p, OFF_NAME_PTR);
            string name = nptr != IntPtr.Zero && nlen > 0 ? Marshal.PtrToStringUni(nptr, nlen / 2) : "(idle/system)";
            res[pid] = (name, cpu, ws);
            if (next == 0) break;
            p += next;
        }
        return res;
    }

    static double R(double v, int d) => double.IsFinite(v) ? Math.Round(v, d) : -1;

    /// load.json content. t0Ticks = the capture's Stopwatch start (QPC).
    public Dictionary<string, object?> ToJson(long t0Ticks)
    {
        long Us(long tick) => (long)((decimal)(tick - t0Ticks) * 1_000_000 / Stopwatch.Frequency);
        return new Dictionary<string, object?>
        {
            ["schema"] = 1,
            ["source"] = "deepscan deepexp Monitor.cs port (GetSystemTimes, NtQuerySystemInformation 5+8, GlobalMemoryStatusEx)",
            ["clock"] = "t_us = microseconds since recording start, same clock as t_us in data.mhc; negative = before recording",
            ["sampleHz"] = Hz,
            ["logicalCpus"] = nCpu,
            ["samples"] = sys.Count,
            ["failures"] = failures,
            ["error"] = error,
            ["samplerCostMeanUs"] = Math.Round(costSumUs / Math.Max(1, ticks), 1),
            ["samplerCostMaxUs"] = Math.Round(costMaxUs, 1),
            ["system"] = new Dictionary<string, object?>
            {
                ["columns"] = new[] { "t_us", "cpu_pct", "max_core_pct", "dpc_pct", "int_pct", "int_per_sec", "ram_used_mb", "ram_load_pct" },
                ["rows"] = sys.Select(s => new object[] { Us(s.tick), R(s.cpu, 2), R(s.maxCore, 1), R(s.dpc, 3),
                    R(s.isr, 3), R(s.intPerSec, 0), R(s.ramUsedMb, 0), s.ramLoad }).ToList(),
            },
            ["cores"] = new Dictionary<string, object?>
            {
                ["columns"] = new[] { "t_us", "busy_pct[core]", "dpc_pct[core]", "int_pct[core]" },
                ["rows"] = cores.Select(c => new object[] { Us(c.tick),
                    c.busy.Select(v => R(v, 1)).ToArray(), c.dpc.Select(v => R(v, 2)).ToArray(),
                    c.isr.Select(v => R(v, 2)).ToArray() }).ToList(),
            },
            ["topProcesses"] = new Dictionary<string, object?>
            {
                ["note"] = $"top {TopN} by CPU % of all logical cores since the previous census (~{CensusEverySec:0.#} s)",
                ["rows"] = procs.Select(p => new Dictionary<string, object?>
                {
                    ["t_us"] = Us(p.tick),
                    ["procs"] = p.top.Select(x => new object[] { x.name, R(x.cpu, 2), R(x.wsMb, 0) }).ToList(),
                }).ToList(),
            },
        };
    }
}
