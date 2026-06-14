using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;

class Program
{
    const string VERSION = "1.0.1";

    const string UploadUrlEndpoint = "https://tools.mariusheier.com/deeppoll/upload-url";
    const string DiscordUrl = "https://discord.gg/4Q9SRUt85j";

    // Ring buffer cap. ~16k events/s for one 8 kHz board is ~150 MB/min of
    // trace, so 1 GB holds roughly the last 6-8 minutes - plenty for incident
    // analysis while keeping the zipped upload under the 500 MB server limit.
    const int RingMaxMb = 1024;

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

    static string DeviceDisplayName(string vidPid) =>
        KnownDevices.TryGetValue(vidPid, out var name) ? name : vidPid;

    static void Main(string[] args)
    {
        if (args.Contains("--snapshot"))
        {
            // Support/debug: print the system snapshot and exit.
            Console.WriteLine(JsonSerializer.Serialize(CollectSnapshot(),
                new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        bool isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);
        if (!isAdmin)
        {
            Console.WriteLine();
            Console.WriteLine("  ╔════════════════════════════════════════════════════════╗");
            Console.WriteLine("  ║                                                        ║");
            Console.WriteLine("  ║   Right-click the exe and select 'Run as administrator' ║");
            Console.WriteLine("  ║                                                        ║");
            Console.WriteLine("  ╚════════════════════════════════════════════════════════╝");
            Console.WriteLine();
            Console.WriteLine("  Press any key to exit...");
            if (!Console.IsInputRedirected) Console.ReadKey();
            return;
        }

        RunLogger();
    }

    static void RunLogger()
    {
        Console.WriteLine();
        PrintDoubleLine(60);
        PrintCentered("D E E P L O G", 60);
        PrintCentered($"USB Incident Logger  v{VERSION}", 60);
        PrintDoubleLine(60);
        Console.WriteLine();
        Console.WriteLine("  Records USB activity to a debug log so it can be");
        Console.WriteLine("  analyzed for problems.");
        Console.WriteLine();

        // Disk check: ring file + zip staging.
        var drive = new DriveInfo(Path.GetTempPath());
        long freeMb = drive.AvailableFreeSpace / 1024 / 1024;
        if (freeMb < RingMaxMb * 2 + 512)
        {
            Console.WriteLine($"  Not enough disk space: need ~{(RingMaxMb * 2 + 512) / 1024.0:F1} GB free, have {freeMb / 1024.0:F1} GB.");
            return;
        }

        string workDir = Path.Combine(Path.GetTempPath(), $"deeplog_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(workDir);
        string enumEtl = Path.Combine(workDir, "enum.etl");
        string ringEtl = Path.Combine(workDir, "ring.etl");

        // ── Phase 1: start BEFORE plug-in so enumeration is always captured ──
        // Baseline of matching devices already present; anything in it is NOT
        // treated as the controller being plugged in (a bench full of WebUSB
        // dev hardware must not trigger false detections).
        var baseline = DetectMHDevices();
        var verifiedPresent = baseline.Where(d => d.Verified).ToList();
        if (verifiedPresent.Count > 0)
        {
            Console.WriteLine($"  {verifiedPresent[0].Name} is connected.");
            Console.WriteLine();
            Console.WriteLine("  UNPLUG it now to begin...");
            // The connection itself has to land inside the trace, so wait for the
            // device to actually leave the bus rather than trusting a keypress -
            // pressing ENTER while it is still plugged in would miss the
            // enumeration and silently record nothing useful.
            var presentIds = new HashSet<string>(verifiedPresent.Select(d => d.VidPid));
            while (DetectMHDevices().Any(d => presentIds.Contains(d.VidPid)))
                Thread.Sleep(1000);
            Console.WriteLine("  Unplugged.");
            // Re-baseline after the unplug so the replug registers as a new arrival.
            baseline = DetectMHDevices();
        }
        else
        {
            Console.WriteLine("  Keep your controller UNPLUGGED.");
            Console.WriteLine("  Press ENTER to start logging, then plug it in.");
            Console.ReadLine();
        }

        StartSession("deeplog_enum", enumEtl, circularMaxMb: 0);

        Console.WriteLine();
        Console.WriteLine("  Plug in your controller NOW. Waiting for it...");

        // The controller is whatever shows up that wasn't there before -
        // a newly arrived 1209:0001 counts even with a generic product
        // string (older setup firmware), because it arrived on cue.
        var baselineIds = new HashSet<string>(baseline.Select(d => d.VidPid));
        (string VidPid, string Name, bool Verified) detected = default;
        for (int i = 0; i < 60; i++)
        {
            Thread.Sleep(1000);
            var now = DetectMHDevices();
            var hit = now.FirstOrDefault(d => !baselineIds.Contains(d.VidPid));
            if (hit.VidPid != null) { detected = hit; break; }
        }

        if (detected.VidPid != null)
        {
            Console.WriteLine($"  Detected: {detected.Name}");
            if (SetupModeVidPids.Contains(detected.VidPid))
            {
                Console.WriteLine();
                Console.WriteLine("  Note: the board is in setup mode. If your problem is");
                Console.WriteLine("  about gaming, calibrate at setup.mariusheier.com first.");
            }
        }
        else
        {
            Console.WriteLine("  No MH controller detected - logging continues anyway.");
        }

        // Let enumeration and early streaming settle, refresh the device
        // rundown so descriptors of the now-present device are in the log.
        Thread.Sleep(10000);
        RunCommand("logman", "update deeplog_enum -p Microsoft-Windows-USB-USBHUB3 -ets");
        Thread.Sleep(2000);
        RunCommand("logman", "stop deeplog_enum -ets");

        // ── Phase 2: ring buffer until something happens ──
        StartSession("deeplog_ring", ringEtl, circularMaxMb: RingMaxMb);

        Console.WriteLine();
        Console.WriteLine($"  Recording to ring buffer (keeps the last ~6-8 minutes).");
        Console.WriteLine("  Play normally. This can run for hours.");
        Console.WriteLine();
        Console.WriteLine("  Press ENTER when the problem happens (or to stop).");
        Console.WriteLine();

        var knownPresent = new HashSet<string>(DetectMHDevices().Select(d => d.VidPid));
        string trigger = "manual";
        DateTime? triggerTimeUtc = null;
        var sw = Stopwatch.StartNew();
        var lastPresenceCheck = TimeSpan.Zero;
        var lastRundownRefresh = TimeSpan.Zero;

        // Reader thread instead of Console.KeyAvailable: works the same when
        // stdin is a console or a pipe.
        bool stopRequested = false;
        var reader = new Thread(() => { try { Console.ReadLine(); } catch { } stopRequested = true; })
        { IsBackground = true };
        reader.Start();

        while (!stopRequested && sw.Elapsed.TotalHours < 6)
        {
            Console.Write($"\r  Recording: {sw.Elapsed:hh\\:mm\\:ss}  ");
            Thread.Sleep(1000);
            if (stopRequested) break;

            // Watch for the controller dropping off the bus (OS-level disconnect)
            if (sw.Elapsed - lastPresenceCheck > TimeSpan.FromSeconds(2))
            {
                lastPresenceCheck = sw.Elapsed;
                var now = new HashSet<string>(DetectMHDevices().Select(d => d.VidPid));
                var vanished = knownPresent.Where(d => !now.Contains(d)).ToList();
                if (vanished.Count > 0)
                {
                    trigger = "auto-disconnect";
                    triggerTimeUtc = DateTime.UtcNow;
                    Console.WriteLine();
                    Console.WriteLine($"  Disconnect detected: {DeviceDisplayName(vanished[0])}");
                    Console.WriteLine("  Capturing 5 more seconds of aftermath...");
                    Thread.Sleep(5000);
                    break;
                }
                foreach (var d in now) knownPresent.Add(d);
            }

            // Periodic rundown refresh so device identity stays inside the ring
            // even after old events get overwritten.
            if (sw.Elapsed - lastRundownRefresh > TimeSpan.FromMinutes(5))
            {
                lastRundownRefresh = sw.Elapsed;
                RunCommand("logman", "update deeplog_ring -p Microsoft-Windows-USB-USBHUB3 -ets");
            }
        }

        RunCommand("logman", "stop deeplog_ring -ets");
        Console.WriteLine();
        Console.WriteLine();

        // ── Phase 3: survey ──
        var survey = new Dictionary<string, object>
        {
            ["trigger"] = trigger,
            ["triggerTimeUtc"] = triggerTimeUtc?.ToString("o") ?? "",
            ["captureStartUtc"] = DateTime.UtcNow.AddSeconds(-sw.Elapsed.TotalSeconds).ToString("o"),
            ["ringMaxMb"] = RingMaxMb,
            ["deeplogVersion"] = VERSION,
        };

        PrintSingleLine(60);
        Console.WriteLine();
        survey["whatHappened"] = AskChoice("What happened?", new[]
        {
            "Controller disconnected in game only",
            "Windows disconnect sound / dead everywhere",
            "Both / not sure",
            "Nothing - just stopping the logger",
        });
        survey["recovered"] = AskChoice("Did it recover?", new[]
        {
            "Came back by itself",
            "Had to replug",
            "Still dead",
            "Nothing happened",
        });
        string when = AskChoice("How long ago did it happen?", new[]
        {
            "Just now",
            "A few minutes ago",
            "Longer ago",
            "Nothing happened",
        });
        survey["howLongAgo"] = when;

        if (when == "Longer ago")
        {
            Console.WriteLine();
            Console.WriteLine("  Heads up: the ring buffer keeps roughly the last 6-8");
            Console.WriteLine("  minutes, so the incident may not be in this log.");
            Console.WriteLine("  Next time, stop the logger soon after it happens.");
        }

        Console.WriteLine();
        Console.Write("  Which game were you playing? (ENTER to skip): ");
        survey["game"] = Console.ReadLine()?.Trim() ?? "";

        // ── Phase 4: system snapshot ──
        Console.WriteLine();
        Console.WriteLine("  Collecting system snapshot (power settings, USB info)...");
        var snapshot = CollectSnapshot();

        var jsonOpts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(workDir, "survey.json"), JsonSerializer.Serialize(survey, jsonOpts));
        File.WriteAllText(Path.Combine(workDir, "snapshot.json"), JsonSerializer.Serialize(snapshot, jsonOpts));

        // ── Phase 5: consent + upload ──
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("  What this sends to Marius:");
            Console.WriteLine();
            Console.WriteLine("    1. USB timing log (ring buffer + connection log)");
            Console.WriteLine("       When USB transfers happened and how long they took");
            Console.WriteLine("       - timing only. No data contents, no keystrokes,");
            Console.WriteLine("       no files, no screen.");
            Console.WriteLine();
            Console.WriteLine("    2. Your answers from the questions above");
            Console.WriteLine();
            Console.WriteLine("    3. System snapshot: power settings, Windows version,");
            Console.WriteLine("       USB controller + connected USB device models,");
            Console.WriteLine("       driver versions, input software running");
            Console.WriteLine();
            Console.WriteLine("  Not included: your name, Windows username, files,");
            Console.WriteLine("  browsing, or anything you typed.");
            Console.WriteLine();
            Console.WriteLine("    [1] Send to Marius");
            Console.WriteLine("    [2] Show me the full system snapshot first");
            Console.WriteLine("    [3] Don't send (keep the log on this PC)");
            Console.WriteLine();
            Console.Write("  Select: ");
            string? choice = Console.ReadLine()?.Trim();

            if (choice == "2")
            {
                Console.WriteLine();
                Console.WriteLine(JsonSerializer.Serialize(snapshot, jsonOpts));
                continue;
            }
            if (choice == "1")
            {
                string? logId = UploadBundle(workDir, enumEtl, ringEtl);
                if (logId != null)
                {
                    Console.WriteLine();
                    Console.WriteLine("  Upload complete!");
                    Console.WriteLine();
                    Console.WriteLine($"  Log ID: {logId}");
                    Console.WriteLine();
                    Console.WriteLine("  Send this ID to Marius on Discord:");
                    Console.WriteLine($"  {DiscordUrl}");
                    try { Directory.Delete(workDir, true); } catch { }
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine($"  The log is kept at: {workDir}");
                }
                return;
            }

            // Keep locally
            Console.WriteLine();
            Console.WriteLine($"  Log kept at: {workDir}");
            Console.WriteLine("  You can inspect it, delete it, or send it later.");
            return;
        }
    }

    static void StartSession(string name, string etlPath, int circularMaxMb)
    {
        RunCommand("logman", $"stop {name} -ets", silent: true);
        string circular = circularMaxMb > 0 ? $"-f bincirc -max {circularMaxMb} " : "";
        RunCommand("logman",
            $"start {name} -p Microsoft-Windows-USB-UCX -o \"{etlPath}\" {circular}-nb 64 256 -bs 512 -ct perf -ets");
        RunCommand("logman", $"update {name} -p Microsoft-Windows-USB-USBHUB3 -ets");
    }

    static string AskChoice(string question, string[] options)
    {
        Console.WriteLine($"  {question}");
        Console.WriteLine();
        for (int i = 0; i < options.Length; i++)
            Console.WriteLine($"    [{i + 1}] {options[i]}");
        Console.WriteLine();
        while (true)
        {
            Console.Write("  Select: ");
            string? c = Console.ReadLine()?.Trim();
            if (int.TryParse(c, out int idx) && idx >= 1 && idx <= options.Length)
            {
                Console.WriteLine();
                return options[idx - 1];
            }
        }
    }

    static string? UploadBundle(string workDir, string enumEtl, string ringEtl)
    {
        Console.WriteLine();
        Console.Write("  Your nickname (Discord/name, ENTER for anonymous): ");
        string? nickname = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(nickname)) nickname = "anonymous";

        Console.WriteLine();
        Console.WriteLine("  Compressing bundle...");

        string zipPath = Path.Combine(Path.GetTempPath(), $"deeplog_bundle_{DateTime.Now:HHmmss}.zip");
        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                if (File.Exists(enumEtl)) zip.CreateEntryFromFile(enumEtl, "enum.etl", CompressionLevel.Optimal);
                if (File.Exists(ringEtl)) zip.CreateEntryFromFile(ringEtl, "ring.etl", CompressionLevel.Optimal);
                zip.CreateEntryFromFile(Path.Combine(workDir, "survey.json"), "survey.json");
                zip.CreateEntryFromFile(Path.Combine(workDir, "snapshot.json"), "snapshot.json");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Compression failed: {ex.Message}");
            return null;
        }

        long enumSize = File.Exists(enumEtl) ? new FileInfo(enumEtl).Length : 0;
        long ringSize = File.Exists(ringEtl) ? new FileInfo(ringEtl).Length : 0;
        long fileSize = new FileInfo(zipPath).Length;

        Console.WriteLine();
        Console.WriteLine("  Bundle contents:");
        Console.WriteLine($"    enum.etl  (connection log)  {HumanSize(enumSize)}");
        Console.WriteLine($"    ring.etl  (timing buffer)   {HumanSize(ringSize)}");
        Console.WriteLine("    survey.json, snapshot.json");
        Console.WriteLine($"    compressed to {HumanSize(fileSize)}");
        if (enumSize == 0 && ringSize == 0)
            Console.WriteLine("  Warning: no USB timing data was captured.");

        if (fileSize > 500L * 1024 * 1024)
        {
            Console.WriteLine("  Bundle exceeds the 500 MB upload limit.");
            Console.WriteLine($"  It is saved at: {zipPath}");
            return null;
        }

        Console.WriteLine();
        Console.WriteLine("  Uploading...");

        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(10);

            var requestBody = JsonSerializer.Serialize(new
            {
                nickname = nickname,
                logType = "deeplog",
                fileSize = fileSize
            });

            var urlResponse = client.PostAsync(UploadUrlEndpoint,
                new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json")).Result;

            if (!urlResponse.IsSuccessStatusCode)
            {
                Console.WriteLine($"  Failed to get upload URL: {urlResponse.StatusCode}");
                try { File.Delete(zipPath); } catch { }
                return null;
            }

            var urlData = JsonSerializer.Deserialize<JsonElement>(urlResponse.Content.ReadAsStringAsync().Result);
            string uploadUrl = urlData.GetProperty("url").GetString() ?? "";
            string logId = urlData.GetProperty("id").GetString() ?? "";

            using var fileContent = new ByteArrayContent(File.ReadAllBytes(zipPath));
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");

            var uploadResponse = client.PutAsync(uploadUrl, fileContent).Result;
            try { File.Delete(zipPath); } catch { }

            if (!uploadResponse.IsSuccessStatusCode)
            {
                Console.WriteLine($"  Upload failed: {uploadResponse.StatusCode}");
                return null;
            }

            return logId;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Upload failed: {ex.Message}");
            try { File.Delete(zipPath); } catch { }
            return null;
        }
    }

    // ── System snapshot: settings and hardware that make USB flaky ──

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
        snap["onBattery"] = IsOnBattery();

        // USB host controllers
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

        // Connected USB devices (models only)
        var devices = new List<string>();
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\\\VID%'");
            var seen = new HashSet<string>();
            foreach (var o in s.Get())
            {
                string id = o["PNPDeviceID"]?.ToString() ?? "";
                var m = System.Text.RegularExpressions.Regex.Match(id, @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})");
                if (!m.Success) continue;
                string vidPid = $"{m.Groups[1].Value.ToUpper()}:{m.Groups[2].Value.ToUpper()}";
                if (seen.Add(vidPid))
                    devices.Add($"{vidPid} {o["Name"]}");
            }
        }
        catch { }
        snap["usbDevices"] = devices;

        // Driver versions
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

        // Input-layer software that can steal/remap controllers
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
        // Output: "Power Scheme GUID: xxx  (Balanced)"
        string output = RunCommandCapture("powercfg", "/getactivescheme");
        var m = System.Text.RegularExpressions.Regex.Match(output, @"\(([^)]+)\)");
        return m.Success ? m.Groups[1].Value : "unknown";
    }

    static string ParseSelectiveSuspend()
    {
        // USB settings subgroup / USB selective suspend setting
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

    static bool IsOnBattery()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT BatteryStatus FROM Win32_Battery");
            foreach (var o in s.Get())
                return Convert.ToInt32(o["BatteryStatus"]) == 1;  // 1 = discharging
        }
        catch { }
        return false;
    }

    // 1209:0001 is the shared pid.codes test PID, so VID:PID alone cannot
    // prove it is an MH board. The product string can: newer MH setup
    // firmware reports "MH-..." names. Generic strings stay unverified so we
    // never claim a random WebUSB device is an MH gamepad.
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

    static void RunCommand(string exe, string args, bool silent = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        proc?.WaitForExit();
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
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            string output = proc?.StandardOutput.ReadToEnd() ?? "";
            proc?.WaitForExit();
            return output;
        }
        catch
        {
            return "";
        }
    }

    static string HumanSize(long bytes) =>
        bytes >= 1024L * 1024 ? $"{bytes / 1024.0 / 1024.0:F1} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:F0} KB"
        : $"{bytes} bytes";

    static void PrintDoubleLine(int width) => Console.WriteLine(new string('═', width));

    static void PrintSingleLine(int width) => Console.WriteLine(new string('─', width));

    static void PrintCentered(string text, int width)
    {
        int padding = Math.Max(0, (width - text.Length) / 2);
        Console.WriteLine(new string(' ', padding) + text);
    }
}
