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
//
// v2.1 adds (all additive, older readers ignore them): a random per-user
// installId, USB placement + the other USB devices + live driver stacks +
// selective-suspend/D-state (UsbProbe.cs), driver service state, the pad UID
// (PadUid.cs: PS4 feature report or XInput serial string), and machine load sampled during the recording
// (LoadSampler.cs -> load.json). WMI is gone: everything is CfgMgr32, SCM,
// registry and ntdll. --headless drives a real capture without prompts.
//
// v2.2 adds snapshot.padDriverPath: every driver on the pad's USB path and whose
// it is, from the file's signature (DriverIdentity.cs). padFiltersInStack keeps
// its shape but is no longer a fixed list of names: it is everything in the pad's
// stack not proved Microsoft's, and is left out when no list could be made.
// Also snapshot.usbDrivers (the same for every USB device and controller on the
// PC), windowsFit on each Windows driver (is it this Windows' own copy),
// snapshot.windowsVersion (update level) and snapshot.windowsChanges (signs of a
// modified Windows) -- WindowsInfo.cs.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32.SafeHandles;

partial class Program
{
    const string VERSION = "2.4.0";

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

    // --headless: no prompts, for bench testing. Records a real capture from the
    // pad that is plugged in; never uploads unless --upload is given too.
    static bool Headless;

    // Relaxed escaping keeps '&' in instance IDs readable; the JSON is equivalent.
    static readonly JsonSerializerOptions JsonOut = new()
    { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly JsonSerializerOptions JsonCompact = new()
    { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static string? ArgValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static void Main(string[] args)
    {
        if (args.Contains("--snapshot"))
        {
            string? pad = DetectMHDevices().FirstOrDefault(d => d.Verified).VidPid;
            Console.WriteLine(JsonSerializer.Serialize(CollectSnapshot(UsbProbe.Collect(pad ?? "OTHER")),
                JsonOut));
            return;
        }
        if (ArgValue(args, "--probe-usb") is string probeVidPid)
        {
            // Bench check of the placement code against any present device (read-only).
            var probe = UsbProbe.Collect(probeVidPid.ToUpperInvariant());
            var report = UsbProbe.DriverReport(probe);
            Console.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["padMatch"] = probe["padMatch"],
                ["padPlacement"] = probe.GetValueOrDefault("padPlacement"),
                ["padFiltersInStack"] = DriverIdentity.OldFilterField(report),
                ["padDriverPath"] = report,
            }, JsonOut));
            return;
        }
        if (ArgValue(args, "--identify-driver") is string _)
        {
            // Bench check of the verdict: service names or file paths, or "all" for every kernel driver service.
            var names = args.SkipWhile(a => a != "--identify-driver").Skip(1).ToList();
            if (names.SequenceEqual(new[] { "all" }))
                using (var services = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services")!)
                    names = services.GetSubKeyNames().Where(n =>
                    {
                        using var k = services.OpenSubKey(n);
                        return k?.GetValue("Type") is int t && (t & 0x3) != 0;
                    }).ToList();
            Console.WriteLine(JsonSerializer.Serialize(names.Select(DriverIdentity.Identify).ToList(), JsonOut));
            return;
        }
        if (ArgValue(args, "--replay-stacks") is string replayFile)
        {
            // Bench check of the report on stored stacks: [{"id": "...", "stacks": [["\\Driver\\x", ...], ...]}, ...].
            // Files are looked up on THIS PC, so a driver that is not installed here comes out could-not-tell.
            var outRows = new List<Dictionary<string, object?>>();
            foreach (var row in JsonSerializer.Deserialize<List<JsonElement>>(File.ReadAllText(replayFile))!)
            {
                var nodes = row.GetProperty("stacks").EnumerateArray().Select((st, i) => new Dictionary<string, object?>
                {
                    ["where"] = i == 0 ? "pad" : "below pad",
                    ["instanceId"] = $"stored node {i}",
                    ["stack"] = st.EnumerateArray().Select(x => x.GetString()!).ToList(),
                }).ToList();
                var report = DriverIdentity.PathReport(nodes);
                outRows.Add(new Dictionary<string, object?>
                {
                    ["id"] = row.GetProperty("id").GetString(),
                    ["status"] = report["status"],
                    ["summary"] = report["summary"],
                    ["notMicrosoft"] = report["notMicrosoft"],
                    ["couldNotTell"] = report["couldNotTell"],
                    ["microsoft"] = report["microsoft"],
                    ["padFiltersInStack"] = DriverIdentity.OldFilterField(report),
                });
            }
            Console.WriteLine(JsonSerializer.Serialize(outRows, JsonOut));
            return;
        }
        Headless = args.Contains("--headless");
        // --long (bench): the "while I play" session; --seconds then says when it stops by itself,
        // and --mark-at 5,12.5 drops marks at those seconds.
        bool longMode = args.Contains("--long");
        double recordSeconds = double.TryParse(ArgValue(args, "--seconds"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double sArg) && sArg > 0 && (sArg <= 120 || longMode) ? sArg : RecordSeconds;
        var benchMarks = (ArgValue(args, "--mark-at") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => (Kind: x.EndsWith("L", StringComparison.OrdinalIgnoreCase) ? "lost" : "late", Text: x.TrimEnd('L', 'l')))
            .Select(x => (AtS: double.TryParse(x.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : -1, x.Kind))
            .Where(m => m.AtS >= 0).ToList();          // 12 = an F9 mark at 12 s, 20L = an F8 "lost" mark at 20 s

        Console.WriteLine();
        Console.WriteLine("  D E E P L O G   v" + VERSION);
        Console.WriteLine("  Controller recorder for MH gamepads");
        Console.WriteLine();
        Console.WriteLine("  Record your controller and send the recording to Marius.");
        Console.WriteLine("  It reads your controller only -- no keyboard, no files, nothing else.");
        Console.WriteLine();

        // ---- device selection ------------------------------------------------
        var device = Headless ? SelectDeviceHeadless(args.Contains("--allow-no-pad")) : SelectDevice();
        if (device == null) return;
        var (vidPid, name) = device.Value;

        bool ps4 = vidPid == Ps4VidPid;

        // Where the pad sits, while it is surely plugged in. Read-only, a few ms.
        Dictionary<string, object?> usb;
        try { usb = UsbProbe.Collect(vidPid); }
        catch (Exception ex) { usb = new() { ["error"] = ex.Message }; }

        // ---- quick test, a whole play session, or the check -------------------
        bool checkMode = args.Contains("--check");
        if (!Headless)
        {
            Console.WriteLine();
            Console.WriteLine("  What do you want to do?");
            Console.WriteLine("    [1] Quick test   -- 30 seconds, you move the sticks and click buttons");
            Console.WriteLine("    [2] While I play -- records until you stop it");
            Console.WriteLine("    [3] Check        -- a game or Windows does not see my controller (1 minute)");
            while (true)
            {
                Console.Write("  > ");
                string c = (Console.ReadLine() ?? "").Trim();
                if (c == "" || c == "1") break;
                if (c == "2") { longMode = true; break; }
                if (c == "3") { checkMode = true; break; }
            }
        }
        if (checkMode)
        {
            // Before the XInput slot is looked for: a pad with no slot is what the check is for.
            RunCheckFlow(args, vidPid, name, ps4, usb);
            return;
        }

        int xinputSlot = -1;
        if (!ps4)
        {
            xinputSlot = vidPid == "NONE" ? 0 : ResolveXInputSlot();
            if (xinputSlot < 0)
            {
                Console.WriteLine("  Run DeepLog again and pick [3] Check to see why.");
                return;
            }
        }

        string workDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepLog", $"deeplog_{DateTime.Now:yyyy-MM-dd_HHmmss}");
        Directory.CreateDirectory(workDir);

        // The pad's own settings, before the recording so the capture is never disturbed.
        var padSettings = PadSettings.Read(vidPid, ps4 ? FindHidPath(0x054C, 0x05C4) : null, PadInstanceId(usb));
        long settingsQpc = (long)padSettings["readAtQpc"]!;
        padSettings.Remove("readAtQpc");

        // ---- instruction + countdown ----------------------------------------
        Console.WriteLine();
        if (longMode)
        {
            Console.WriteLine("  When recording starts, go and play as you normally do.");
            Console.WriteLine();
            Console.WriteLine("    F9   press it right after something felt late or wrong. You hear a beep.");
            Console.WriteLine("    F8   press it when the game lost your controller. A lower beep.");
            Console.WriteLine("    F10  stop recording.");
            Console.WriteLine();
            Console.WriteLine("  You can leave this window in the background. There is no time limit.");
            Console.WriteLine("  Marius gets the 45 seconds before and 15 seconds after each press,");
            Console.WriteLine("  so press every time it happens. Without any press he gets the last 5 minutes.");
            Console.WriteLine();
            if (!ps4)
            {
                Console.WriteLine("  While it records, DeepLog keeps one CPU core busy. That is how it");
                Console.WriteLine("  catches every sample.");
                Console.WriteLine();
            }
            Console.WriteLine("  (Windows tells DeepLog about F8, F9 and F10 only. No other key is read.)");
        }
        else
        {
            Console.WriteLine("  When recording starts:");
            Console.WriteLine("    Move the joysticks in circles around the edge. Click buttons.");
            Console.WriteLine("    Whatever is relevant for your case.");
        }
        Console.WriteLine();
        string powerPlanBefore = ParseActivePowerPlan();
        var programsAtStart = ProgramsNow("start", 0);
        var load = new LoadSampler();
        Console.Write("  Starting in ");
        for (int i = (int)CountdownSeconds; i >= 1; i--)
        {
            if (i == 1) load.Start();          // one second of pre-roll as a baseline
            Console.Write($"{i}...  ");
            Thread.Sleep(1000);
        }
        Console.WriteLine();
        Console.WriteLine();

        // ---- recording -------------------------------------------------------
        CaptureResult cap;
        LongCapture? lcap = null;
        try
        {
            if (longMode)
            {
                lcap = RecordLong(ps4, xinputSlot, vidPid, workDir, Headless ? recordSeconds : 0, benchMarks);
                cap = new CaptureResult
                {
                    Proto = lcap.Proto, RecordSize = lcap.RecordSize, AchievedHz = lcap.AchievedHz,
                    StartUnixUs = lcap.StartUnixUs, T0Ticks = lcap.T0Ticks, Events = lcap.Events,
                };
                recordSeconds = lcap.DurationUs / 1e6;
            }
            else
                cap = ps4 ? HidCapture(0x054C, 0x05C4, recordSeconds)
                          : XInputCapture(xinputSlot, recordSeconds);
        }
        catch (Exception ex)
        {
            load.Stop();
            Console.WriteLine();
            Console.WriteLine($"  Recording failed: {ex.Message}");
            return;
        }
        load.Stop();
        string powerPlanAfter = ParseActivePowerPlan();

        // UID after the recording, so the capture itself is never disturbed.
        var uid = PadUid.Read(vidPid, ps4 ? FindHidPath(0x054C, 0x05C4) : null, PadInstanceId(usb));

        // The settings once more, now that the recording is over: a setting that changed, or a
        // pad counter that restarted, shows something happened to the pad in between.
        var padSettingsEnd = PadSettings.Read(vidPid, ps4 ? FindHidPath(0x054C, 0x05C4) : null, PadInstanceId(usb));
        padSettingsEnd["readSecondsAfterFirstSample"] =
            Math.Round(((long)padSettingsEnd["readAtQpc"]! - cap.T0Ticks) / (double)Stopwatch.Frequency, 3);
        padSettingsEnd.Remove("readAtQpc");

        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine("  Recording complete.");
        if (lcap != null)
        {
            Console.WriteLine($"    {Clock(recordSeconds)} recorded, {lcap.MarksUs.Count} mark(s). It ended because {lcap.StopReason}.");
            if (lcap.Count == 0)
            {
                Console.WriteLine("  Nothing came from the controller, so there is nothing to send.");
                try { File.Delete(lcap.RawPath); } catch { }
                return;
            }
        }
        Console.WriteLine();

        // ---- note + contact ----------------------------------------------------
        string note, nickname, email;
        string? whatHappened = null;
        string MarkList() => string.Join(", ", lcap!.Marks().Select(m =>
            $"{Clock(m.T / 1e6)} ({(m.Kind == "lost" ? "F8, lost controller" : "F9")})"));
        if (Headless)
        {
            note = ArgValue(args, "--note") ?? "(headless test capture)";
            nickname = ArgValue(args, "--nickname") ?? "anonymous";
            email = "";
        }
        else
        {
            if (lcap != null)
            {
                // Long session: ask what happened, in their own words, while it is fresh.
                if (lcap.MarksUs.Count > 0)
                    Console.WriteLine("  You pressed: " + MarkList() + ".");
                Console.WriteLine("  What happened?");
                Console.WriteLine("    [1] A slide, jump or button came late");
                Console.WriteLine("    [2] The stick felt off");
                Console.WriteLine("    [3] The game lost my controller");
                Console.WriteLine("    [ENTER] something else, or nothing");
                Console.Write("  > ");
                whatHappened = (Console.ReadLine() ?? "").Trim() switch
                {
                    "1" => "input came late", "2" => "stick felt off", "3" => "game lost the controller", _ => null,
                };
                Console.WriteLine();
                Console.WriteLine("  Now in your own words (ENTER to skip).");
                Console.WriteLine("    example:  aim slid to the left by itself after I let go of the stick");
                Console.WriteLine("    example:  second F9 was the bad one, the first was a mistake");
            }
            else
            {
                Console.WriteLine("  Useful note to Marius");
                Console.WriteLine("    example:  Log of normal joystick feeling");
                Console.WriteLine("    example:  Log of joystick feeling weird");
            }
            Console.Write("  > ");
            note = (Console.ReadLine() ?? "").Trim();
            if (whatHappened != null) note = note.Length == 0 ? whatHappened : $"{whatHappened}: {note}";
            if (note.Length == 0) note = "(no note)";

            Console.Write("  Your nickname (Discord/name, ENTER for anonymous): ");
            nickname = (Console.ReadLine() ?? "").Trim();
            if (nickname.Length == 0) nickname = "anonymous";

            Console.Write("  Your email (optional, for follow-up -- ENTER to skip): ");
            email = (Console.ReadLine() ?? "").Trim();
        }

        // ---- bundle ------------------------------------------------------------
        string mhcPath = Path.Combine(workDir, "data.mhc");
        if (lcap == null) WriteMhc(mhcPath, cap);
        padSettings["readSecondsBeforeFirstSample"] =
            Math.Round((cap.T0Ticks - settingsQpc) / (double)Stopwatch.Frequency, 3);

        string installId = InstallId();
        var meta = new Dictionary<string, object?>
        {
            ["tool"] = "deeplog",
            ["toolVersion"] = VERSION,
            ["createdUtc"] = DateTime.UtcNow.ToString("o"),
            ["installId"] = installId,
            ["note"] = note,
            ["nickname"] = nickname,
            ["email"] = email,
            ["device"] = new Dictionary<string, object?>
            {
                ["vidPid"] = vidPid,
                ["name"] = name,
                ["backend"] = ps4 ? "hid-raw" : "xinput",
                ["xinputSlot"] = ps4 ? null : xinputSlot,
                ["uid"] = uid["uid"],
            },
            ["padUid"] = uid,
            ["recordSeconds"] = recordSeconds,
            ["samples"] = lcap?.Count ?? cap.Count,
            ["padSettings"] = padSettings,
            ["padSettingsEnd"] = padSettingsEnd,
            ["programs"] = lcap?.Programs.OrderBy(p => (double)p["atS"]!).ToList()
                           ?? new List<Dictionary<string, object?>> { programsAtStart },
            ["achievedHz"] = Math.Round(cap.AchievedHz, 1),
            ["events"] = cap.Events,
            ["headless"] = Headless ? true : null,
            ["recordFormat"] = ps4
                ? "u32 t_us, u32 seq, byte[64] raw input report (proto 1, 72 B/record)"
                : "u32 t_us, u32 packet, i16 lx, i16 ly, i16 rx, i16 ry, u8 lt, u8 rt, u16 buttons, u8 phase, u8 connected, u16 pad (proto 0, 24 B/record)",
        };
        var loadJson = load.ToJson(cap.T0Ticks);
        loadJson["powerPlanBefore"] = powerPlanBefore;
        loadJson["powerPlanAfter"] = powerPlanAfter;

        var jsonOpts = JsonOut;
        File.WriteAllText(Path.Combine(workDir, "meta.json"), JsonSerializer.Serialize(meta, jsonOpts));
        File.WriteAllText(Path.Combine(workDir, "note.txt"), note + Environment.NewLine);
        File.WriteAllText(Path.Combine(workDir, "snapshot.json"),
            JsonSerializer.Serialize(CollectSnapshot(usb, installId), jsonOpts));
        File.WriteAllText(Path.Combine(workDir, "load.json"), JsonSerializer.Serialize(loadJson, JsonCompact));

        string zipPath = Path.Combine(workDir, "bundle.zip");
        string sentWhat = lcap == null ? "quick" : lcap.MarksUs.Count > 0 ? "marks" : "tail";

        // Long session: cut the parts to send out of session.raw. The first part is
        // data.mhc (so every reader of the 30-second test opens it), then data_2.mhc ...
        void BuildZip()
        {
            var entries = new List<string> { "data.mhc" };
            if (lcap != null)
            {
                foreach (var old in Directory.GetFiles(workDir, "data*.mhc")) File.Delete(old);
                var parts = PlanParts(lcap, sentWhat);
                var rows = new List<Dictionary<string, object?>>();
                for (int i = 0; i < parts.Count; i++)
                {
                    string fileName = i == 0 ? "data.mhc" : $"data_{i + 1}.mhc";
                    var (n, gaps) = WritePart(lcap, parts[i], Path.Combine(workDir, fileName));
                    if (i > 0) entries.Add(fileName);
                    rows.Add(new()
                    {
                        ["file"] = fileName,
                        ["startS"] = Math.Round(parts[i].StartUs / 1e6, 3),
                        ["endS"] = Math.Round(parts[i].EndUs / 1e6, 3),
                        ["samples"] = n,
                        ["marksS"] = parts[i].Marks.Select(m => Math.Round(m.T / 1e6, 3)).ToList(),
                        ["gaps"] = gaps,
                    });
                }
                meta["session"] = new Dictionary<string, object?>
                {
                    ["mode"] = "while-playing",
                    ["durationSeconds"] = Math.Round(lcap.DurationUs / 1e6, 3),
                    ["stopReason"] = lcap.StopReason,
                    ["marksS"] = lcap.MarksUs.OrderBy(m => m).Select(m => Math.Round(m / 1e6, 3)).ToList(),
                    ["marks"] = lcap.Marks().Select(m => new Dictionary<string, object?>
                    {
                        ["tS"] = Math.Round(m.T / 1e6, 3), ["kind"] = m.Kind,
                        ["key"] = m.Kind == "lost" ? "F8" : "F9",
                        ["meaning"] = m.Kind == "lost" ? "the game lost my controller" : "that felt late or wrong",
                    }).ToList(),
                    ["whatHappened"] = whatHappened,
                    ["sent"] = sentWhat,
                    ["sentRule"] = sentWhat == "marks" ? $"{MarkBeforeSec:0} s ({LostBeforeSec:0} s for an F8 mark) before to {MarkAfterSec:0} s after each mark, overlapping ones joined"
                                 : sentWhat == "tail" ? $"no marks: the last {NoMarkTailSec:0} s" : "the whole session",
                    ["fullRecordingBytes"] = new FileInfo(lcap.RawPath).Length,
                    ["timeNote"] = "t_us in each part counts from that part's startS; startS, endS and marksS count from the start of the session",
                    ["parts"] = rows,
                };
                File.WriteAllText(Path.Combine(workDir, "meta.json"), JsonSerializer.Serialize(meta, jsonOpts));
            }
            if (File.Exists(zipPath)) File.Delete(zipPath);
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            foreach (string e in entries)
                zip.CreateEntryFromFile(Path.Combine(workDir, e), e, CompressionLevel.Optimal);
            zip.CreateEntryFromFile(Path.Combine(workDir, "meta.json"), "meta.json");
            zip.CreateEntryFromFile(Path.Combine(workDir, "note.txt"), "note.txt");
            zip.CreateEntryFromFile(Path.Combine(workDir, "snapshot.json"), "snapshot.json");
            zip.CreateEntryFromFile(Path.Combine(workDir, "load.json"), "load.json", CompressionLevel.Optimal);
        }
        BuildZip();
        double zipMb = new FileInfo(zipPath).Length / 1024.0 / 1024.0;
        bool canSendAll = lcap != null && sentWhat != "everything" && new FileInfo(lcap.RawPath).Length <= SendEverythingMaxRaw
            && lcap.DurationUs > (sentWhat == "tail" ? NoMarkTailSec * 1e6 : 0);
        string WhatLine() => lcap == null ? $"{recordSeconds:0} second controller recording"
            : sentWhat == "marks" ? $"{MarkBeforeSec:0} s before to {MarkAfterSec:0} s after each of your {lcap.MarksUs.Count} mark(s), out of {Clock(recordSeconds)} recorded"
            : sentWhat == "tail" ? $"the last {Clock(Math.Min(NoMarkTailSec, recordSeconds))} of {Clock(recordSeconds)} recorded (no F9 marks)"
            : $"the whole {Clock(recordSeconds)} recording";

        // ---- review + consent ----------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("  Ready to send:");
        Console.WriteLine($"    {WhatLine()}   ({zipMb:F1} MB)");
        Console.WriteLine("    the controller's own settings (firmware version, mode, rate, button remap, click triggers)");
        Console.WriteLine("    your note");
        Console.WriteLine("    your contact (nickname / email, if you gave them)");
        Console.WriteLine("    system info (Windows version and update level, USB + power settings)");
        Console.WriteLine("    signs of a modified Windows (removed or disabled Windows parts, update settings,");
        Console.WriteLine("      the PC maker name Windows records)");
        Console.WriteLine("    which USB port/hub the controller is on, and the names of the other USB devices");
        Console.WriteLine("    every driver on your USB devices and controllers: names, versions, who made and signed them");
        Console.WriteLine("    PC load during the recording (CPU, memory, top 5 programs by CPU)");
        Console.WriteLine("    which launchers, overlays, controller tools and browsers were running, and the");
        Console.WriteLine("      name of the program in front, at the start and at each mark (names only)");
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
            Console.WriteLine();
            return;
        }

        Console.Write(canSendAll ? "  Send to Marius now? [Y/n]  (a = send the whole recording instead) "
                                 : "  Send to Marius now? [Y/n] ");
        string yn = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        if (canSendAll && (yn == "a" || yn == "all"))
        {
            string before = sentWhat;
            sentWhat = "everything";
            Console.WriteLine("  Packing the whole recording...");
            BuildZip();
            zipMb = new FileInfo(zipPath).Length / 1024.0 / 1024.0;
            if (zipMb > 500)
            {
                Console.WriteLine($"  The whole recording is {zipMb:F0} MB, over the 500 MB limit. Sending the shorter one.");
                sentWhat = before;
                BuildZip();
            }
            else Console.WriteLine($"  {WhatLine()}   ({zipMb:F1} MB)");
            yn = "y";
        }

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
                if (lcap != null)
                {
                    try { File.Delete(lcap.RawPath); Console.WriteLine(); Console.WriteLine("  The full recording on this PC has been deleted."); }
                    catch { }
                }
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

    /// No prompts: a verified gaming-mode MH pad, else any XInput pad. With
    /// --allow-no-pad and nothing plugged in, records slot 0 anyway ("NONE") so
    /// the rest of the pipeline can be exercised; the bundle says so.
    static (string VidPid, string Name)? SelectDeviceHeadless(bool allowNoPad)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var gaming = DetectMHDevices().Where(d => d.Verified && !SetupModeVidPids.Contains(d.VidPid)).ToList();
            if (gaming.Count > 0)
            {
                Console.WriteLine($"  Found: {gaming[0].Name}");
                return (gaming[0].VidPid, gaming[0].Name);
            }
            if (ConnectedXInputSlots().Count > 0)
            {
                Console.WriteLine("  Found: USB controller (XInput)");
                return ("OTHER", "Other USB controller");
            }
            if (DateTime.UtcNow > deadline) break;
            Thread.Sleep(500);
        }
        if (!allowNoPad)
        {
            Console.WriteLine("  No controller found (headless). Plug one in, or pass --allow-no-pad.");
            return null;
        }
        Console.WriteLine("  No controller connected -- recording XInput slot 0 anyway (--allow-no-pad).");
        return ("NONE", "No controller connected (headless --allow-no-pad)");
    }

    static string? PadInstanceId(Dictionary<string, object?> usb) => UsbProbe.LastPadInstanceId;

    /// Random per-user id, made on first run and kept in %LOCALAPPDATA%\DeepLog,
    /// so repeat bundles from one PC can be linked. Not derived from MachineGuid
    /// or any hardware serial.
    static string InstallId()
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepLog");
            string file = Path.Combine(dir, "install_id.txt");
            if (File.Exists(file))
            {
                string existing = File.ReadAllText(file).Trim();
                if (System.Text.RegularExpressions.Regex.IsMatch(existing, "^[0-9a-f]{16}$")) return existing;
            }
            Directory.CreateDirectory(dir);
            string id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            File.WriteAllText(file, id);
            return id;
        }
        catch { return "unavailable"; }
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
        public long T0Ticks;               // QPC at t_us = 0, for aligning load.json
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
            res.T0Ticks = Stopwatch.GetTimestamp() - sw.ElapsedTicks;
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
        res.T0Ticks = Stopwatch.GetTimestamp() - sw.ElapsedTicks;
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

    // ---- device detection (CfgMgr32; v2.0 used WMI Win32_PnPEntity) ------------

    static readonly string[] MhIdPrefixes = { @"USB\VID_39AE", @"USB\VID_054C&PID_05C4",
        @"USB\VID_1A86&PID_1235", @"USB\VID_1209&PID_0001" };

    static List<(string VidPid, string Name, bool Verified)> DetectMHDevices()
    {
        var found = new List<(string VidPid, string Name, bool Verified)>();
        try
        {
            foreach (var id in DevNode.PresentUsb())
            {
                if (!MhIdPrefixes.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
                string? vidPid = DevNode.VidPid(id);
                if (vidPid == null || found.Any(f => f.VidPid == vidPid)) continue;

                if (vidPid == "1209:0001")
                {
                    // Win32_PnPEntity.Name was FriendlyName, else DeviceDesc.
                    string busName = DevNode.Name(DevNode.Locate(id));
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

    // ---- system snapshot -----------------------------------------------------------

    static string? RegStr(string key, string value)
    {
        try { return Microsoft.Win32.Registry.GetValue(key, value, null)?.ToString()?.Trim(); }
        catch { return null; }
    }

    static Dictionary<string, object> CollectSnapshot(Dictionary<string, object?>? usb = null, string? installId = null)
    {
        var snap = new Dictionary<string, object>
        {
            ["collectedUtc"] = DateTime.UtcNow.ToString("o"),
            ["deeplogVersion"] = VERSION,
        };
        if (installId != null) snap["installId"] = installId;

        // Same shapes as the v2.0 WMI fields, from the registry. ProductName stays
        // "Windows 10" on Windows 11, so the build number decides.
        const string nt = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        string? product = RegStr(nt, "ProductName"), build = RegStr(nt, "CurrentBuildNumber") ?? RegStr(nt, "CurrentBuild");
        if (product != null)
        {
            if (int.TryParse(build, out int b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
            snap["windows"] = $"Microsoft {product} (build {build})";
        }
        if (RegStr(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString") is string cpu)
            snap["cpu"] = cpu;
        const string bios = @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS";
        if (RegStr(bios, "BaseBoardManufacturer") is string mbm)
            snap["motherboard"] = $"{mbm} {RegStr(bios, "BaseBoardProduct")}";

        snap["powerPlan"] = ParseActivePowerPlan();
        snap["usbSelectiveSuspend"] = ParseSelectiveSuspend();
        snap["fastStartup"] = ParseFastStartup();

        var controllers = new List<string>();
        try
        {
            foreach (var id in DevNode.List("USBXHCI", DevNode.CM_GETIDLIST_FILTER_SERVICE | DevNode.CM_GETIDLIST_FILTER_PRESENT))
                controllers.Add($"{DevNode.Name(DevNode.Locate(id))} [{id}]");
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
        snap["inputSoftware"] = inputSoftware;   // v2.0 field, kept as-is for older readers

        // v2.1: installed is not running. Live service state from the SCM...
        try { snap["driverServices"] = UsbProbe.Services("ViGEmBus", "HidHide", "USBPcap", "xusb22"); }
        catch (Exception ex) { snap["driverServices"] = ex.Message; }

        snap["mhDevices"] = DetectMHDevices()
            .Select(d => $"{d.VidPid} {d.Name}{(d.Verified ? "" : " (unverified)")}").ToList();

        // v2.2: the Windows update level, and signs of a modified Windows (WindowsInfo.cs).
        snap["windowsVersion"] = Guarded("Reading the Windows version", WindowsInfo.Version);
        snap["windowsChanges"] = Guarded("The check for a modified Windows", WindowsInfo.Changes);

        // ...and every driver on the pad's own USB path that is not proved Microsoft's (asked first, so it
        // gets the signature-check budget first)...
        if (usb != null)
        {
            try
            {
                var report = UsbProbe.DriverReport(usb);
                snap["padDriverPath"] = report;
                if (DriverIdentity.OldFilterField(report) is List<string> filters) snap["padFiltersInStack"] = filters;
            }
            catch (Exception ex)
            {
                snap["padDriverPath"] = new Dictionary<string, object?>
                {
                    ["status"] = "error",
                    ["summary"] = $"The driver check failed, so nothing is known about the drivers on the controller's path: {WindowsInfo.Scrub(ex.Message)}",
                };
            }
            snap["usb"] = usb;
        }

        // ...and every driver on every USB device and controller on the PC, Microsoft's included.
        snap["usbDrivers"] = Guarded("The check of the drivers on the USB tree", () =>
        {
            var (nodes, truncated) = UsbProbe.AllUsbNodes();
            return DriverIdentity.PcReport(nodes, truncated);
        });
        return snap;
    }

    /// A new read never takes the bundle down with it: a failure becomes a stated error field.
    static Dictionary<string, object?> Guarded(string what, Func<Dictionary<string, object?>> read)
    {
        try { return read(); }
        catch (Exception ex)
        {
            return new Dictionary<string, object?>
            {
                ["status"] = "error",
                ["summary"] = $"{what} failed, so nothing is known from it: {WindowsInfo.Scrub(ex.Message)}",
            };
        }
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
