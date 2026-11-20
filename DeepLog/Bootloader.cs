// DeepLog v2.5 -- a controller that sits in firmware update mode (its bootloader).
//
// No controller firmware is running then, so there is nothing to record and no XInput
// slot or PS4 stream to check. Before 2.5 DeepLog did not know this identity: it offered
// "any XInput pad" instead and recorded some other device (a keyboard that acts as a
// controller) without a word. Now it says what state the pad is in and offers a report:
// where the pad is plugged in, how Windows has it installed, and Windows' device history.
//
// Read-only: the update channel is opened to see that it opens, nothing is sent to it.

using System.IO.Compression;
using System.Text.Json;

partial class Program
{
    const string BootloaderVidPid = "1209:D035";

    static bool BootloaderPresent()
    {
        try { return DevNode.PresentUsb().Any(id => id.StartsWith(@"USB\VID_1209&PID_D035", StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }

    static void RunBootloaderFlow(string[] args, string name, Dictionary<string, object?> usb)
    {
        var data = new Dictionary<string, object?> { ["startedUtc"] = DateTime.UtcNow.ToString("o") };
        var tree = DeviceTree(PadInstanceId(usb));
        data["deviceTree"] = tree;
        bool channelOpens = false;
        try { channelOpens = FindHidPath(0x1209, 0xD035, needInput: false) != null; } catch { }
        data["updateChannelOpens"] = channelOpens;
        int otherSlots = ConnectedXInputSlots().Count;
        data["xinputSlotsFromOtherDevices"] = otherSlots;
        data["programs"] = ProgramsNow("bootloader", 0);

        Console.WriteLine();
        Console.Write("  Reading Windows' device history");
        data["pnpEvents"] = PnpEvents(); Console.Write(".");
        data["setupApi"] = SetupApiTail(); Console.Write(".");
        data["leftovers"] = Leftovers(); Console.WriteLine(".");

        var v = new List<string>
        {
            "Your controller is in firmware update mode. No controller firmware is running on it,",
            "so games cannot see it and there is nothing to record.",
        };
        if (otherSlots > 0)
        {
            v.Add("The controller Windows shows to games right now is another device");
            v.Add("(for example a keyboard that acts as a controller), not this one.");
        }

        var place = usb.GetValueOrDefault("padPlacement") as Dictionary<string, object?>;
        if (place != null)
        {
            int hubs = place.GetValueOrDefault("hubLevels") is int h ? h : 0;
            string where = place.GetValueOrDefault("controllerPlacement") as string ?? "unknown";
            string whereWords = where switch
            {
                "cpu" => "on the CPU's own USB controller",
                "chipset" => "on the motherboard chipset's USB controller",
                "add-in" => "on an extra USB controller chip (often the USB-C / USB4 ports)",
                _ => "on a USB controller DeepLog could not place",
            };
            v.Add($"It is plugged in {whereWords}" + (hubs > 0 ? $", through {hubs} hub(s)." : ", with no hub in between."));
            if (where != "cpu" || hubs > 0)
            {
                v.Add("If the firmware install keeps failing, move the cable to another USB port");
                v.Add("on the back of the PC (no hub, no front port) and install again.");
            }
        }
        foreach (var p in tree.Where(n => (int)n["depth"]! >= 0).Where(n => n["hasProblem"] is true || (n["problemCode"] is uint pc && pc != 0)))
            v.Add($"Windows reports a problem (code {p["problemCode"]}) on \"{p["name"]}\".");
        if (!channelOpens)
            v.Add("Windows lists the controller, but its update channel could not be opened from here.");
        v.Add("Finish the firmware install on the setup site, then run DeepLog again.");
        v.Add("If the install keeps failing, send this report to Marius.");
        data["state"] = "bootloader";
        data["verdict"] = v;

        Console.WriteLine();
        Console.WriteLine("  RESULT");
        foreach (string line in v) Console.WriteLine("    " + line);
        Console.WriteLine();

        string note, nickname, email;
        if (Headless)
        {
            note = ArgValue(args, "--note") ?? "(headless, pad in update mode)";
            nickname = ArgValue(args, "--nickname") ?? "anonymous";
            email = "";
        }
        else
        {
            Console.Write("  Make a report for Marius about this? [Y/n] ");
            string go = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            if (!(go == "" || go == "y" || go == "yes")) { Console.WriteLine(); return; }
            Console.WriteLine();
            Console.WriteLine("  What happened with the firmware install? Where did it stop, which browser? (ENTER to skip)");
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
        var meta = new Dictionary<string, object?>
        {
            ["tool"] = "deeplog", ["toolVersion"] = VERSION, ["mode"] = "bootloader",
            ["createdUtc"] = DateTime.UtcNow.ToString("o"), ["installId"] = installId,
            ["note"] = note, ["nickname"] = nickname, ["email"] = email,
            ["device"] = new Dictionary<string, object?>
            { ["vidPid"] = BootloaderVidPid, ["name"] = name, ["backend"] = "none", ["uid"] = null },
            ["headless"] = Headless ? true : null,
            ["bootloader"] = data,
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
        Console.WriteLine("    the result above");
        Console.WriteLine("    how Windows has the controller installed: its USB parts, their drivers and any problem code");
        Console.WriteLine("    Windows' own log lines about this controller for the last day (plugged in, started, failed, removed)");
        Console.WriteLine("    leftover Windows entries for this controller model");
        Console.WriteLine("    your note and contact (nickname / email, if you gave them)");
        Console.WriteLine("    system info (Windows version and update level, USB + power settings)");
        Console.WriteLine("    signs of a modified Windows, which USB port/hub the controller is on, the names of the");
        Console.WriteLine("      other USB devices, and every driver on your USB devices and controllers");
        Console.WriteLine("    which launchers, overlays, controller tools and browsers are running (names only)");
        Console.WriteLine("    a random ID for this PC made by DeepLog");
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
                Console.WriteLine($"    DeepLog update-mode report {logId} -- \"{note}\"");
            }
            else
            {
                Console.WriteLine("  Upload didn't go through. The report is saved here:");
                Console.WriteLine($"  {zipPath}");
            }
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("  Not sent. The report is saved here if you change your mind:");
            Console.WriteLine($"  {zipPath}");
        }
        Console.WriteLine();
    }
}
