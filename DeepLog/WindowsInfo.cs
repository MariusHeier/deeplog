// The Windows a bundle came from, down to its update level, and signs of a modified
// Windows (Tiny11, AtlasOS, ReviOS, Ghost Spectre and the like) or a tweak tool.
//
// Registry and file reads only: no WMI, no admin, no network, no programs started.
// Every check says what it saw, and a check that could not run says so: nothing
// unknown is reported as clean. None of it says anything about the controller.
//
// Names: the registered owner is often a person's real name, so it is never sent;
// only a match against a known modified-Windows name is. Everything else that is
// sent passes through Scrub (Windows username and PC name masked).

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

static class WindowsInfo
{
    const string NtKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    static string WindowsDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    static string KernelPath => Path.Combine(WindowsDir, "System32", "ntoskrnl.exe");

    /// A file's version as Windows numbers it. Windows stamps many of its files 6.2.x in the binary field, so
    /// only build and revision are compared.
    public record FileVer(int Major, int Minor, int Build, int Revision, string Text)
    {
        public bool WindowsNumbered => (Major, Minor) is (10, 0) or (6, 2) or (6, 3) && Build >= 10000;   // 10240 was Windows 10's first build
        public override string ToString() => Text;
    }

    /// The binary version (VS_FIXEDFILEINFO). The text version can be stale: for a file in System32 Windows reads
    /// it from the language (.mui) file, which updates do not always touch (UsbHub3.sys: text 10.0.26100.1, binary
    /// 26100.7705). A 6.2/6.3 stamp on a Windows build is shown as 10.0, which is what Windows calls it.
    public static FileVer? VersionOf(FileVersionInfo fv)
    {
        if (fv.FileMajorPart == 0 && fv.FileMinorPart == 0 && fv.FileBuildPart == 0 && fv.FilePrivatePart == 0) return null;
        bool compatStamp = (fv.FileMajorPart, fv.FileMinorPart) is (6, 2) or (6, 3) && fv.FileBuildPart >= 10000;
        string text = compatStamp ? $"10.0.{fv.FileBuildPart}.{fv.FilePrivatePart}"
            : $"{fv.FileMajorPart}.{fv.FileMinorPart}.{fv.FileBuildPart}.{fv.FilePrivatePart}";
        return new FileVer(fv.FileMajorPart, fv.FileMinorPart, fv.FileBuildPart, fv.FilePrivatePart, text);
    }

    /// The version of Windows' own files, from the kernel's file version. 25H2 (build 26200) ships 26100
    /// files, 23H2 ships 22621 files, Windows 10 22H2 ships 19041 files: this is what a Windows driver's
    /// version is compared with, not the build number in the registry.
    public static readonly Lazy<FileVer?> FilesVersion = new(() =>
    {
        try { return VersionOf(FileVersionInfo.GetVersionInfo(KernelPath)); }
        catch { return null; }
    });

    /// UBR, the update revision Windows says it is at (the 7840 in 26200.7840).
    public static readonly Lazy<int?> Ubr = new(() =>
    {
        try { using var k = Registry.LocalMachine.OpenSubKey(NtKey); return k?.GetValue("UBR") is int u ? u : null; }
        catch { return null; }
    });

    // ---- snapshot.windowsVersion ------------------------------------------------------

    public static Dictionary<string, object?> Version()
    {
        var res = new Dictionary<string, object?>();
        var problems = new List<string>();
        string? build = null, display = null, edition = null;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(NtKey);
            if (k == null) problems.Add("Windows' version key could not be opened");
            else
            {
                build = k.GetValue("CurrentBuildNumber") as string ?? k.GetValue("CurrentBuild") as string;
                display = k.GetValue("DisplayVersion") as string ?? k.GetValue("ReleaseId") as string;
                edition = k.GetValue("EditionID") as string;
                res["productName"] = Scrub(k.GetValue("ProductName") as string);
                res["editionId"] = Scrub(edition);
                res["installationType"] = Scrub(k.GetValue("InstallationType") as string);
                res["displayVersion"] = Scrub(display);
                res["build"] = build;
                res["ubr"] = Ubr.Value;
                res["version"] = build != null && Ubr.Value is int u ? $"{build}.{u}" : null;
                res["buildLabEx"] = Scrub(k.GetValue("BuildLabEx") as string);
                if (Ubr.Value == null) problems.Add("Windows did not say its update revision (UBR)");
            }
        }
        catch (Exception ex) { problems.Add($"reading Windows' version failed: {Scrub(ex.Message)}"); }

        res["windowsFilesVersion"] = FilesVersion.Value?.Text;
        if (FilesVersion.Value == null) problems.Add("the version of Windows' kernel file could not be read");

        // Cumulative updates as Windows' servicing records them (CBS RollupFix packages).
        Dictionary<string, object?>? latest = null;
        try
        {
            var lcus = CumulativeUpdates();
            res["cumulativeUpdates"] = lcus;
            latest = lcus.Where(p => (string?)p["state"] == "installed")
                         .OrderByDescending(p => System.Version.TryParse((string?)p["version"], out var v) ? v : new System.Version())
                         .FirstOrDefault();
            res["latestInstalledCumulativeUpdate"] = latest;
        }
        catch (Exception ex) { problems.Add($"reading the update records failed: {Scrub(ex.Message)}"); }

        var parts = new List<string>();
        string name = $"{res.GetValueOrDefault("productName") ?? "Windows"}{(display != null ? " " + display : "")}";
        if (int.TryParse(build, out int b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
        parts.Add(res["version"] is string ver ? $"{name}, build {ver}." : $"{name}, build {build ?? "unknown"} (update revision unknown).");
        if (latest != null)
        {
            string when = latest["installedUtc"] is string iso && DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
                ? $" on {t:yyyy-MM-dd} ({(int)(DateTime.UtcNow - t).TotalDays} days before this snapshot)" : " (install date not recorded)";
            parts.Add($"Last cumulative update installed: {latest["version"]}{when}.");
        }
        else if (res.ContainsKey("cumulativeUpdates"))
            parts.Add("Windows has no record of an installed cumulative update. That is normal only for a Windows never updated since it was installed, and is also what a stripped Windows can look like.");
        if (problems.Count > 0) parts.Add("Could not tell: " + string.Join("; ", problems) + ".");
        res["status"] = problems.Count == 0 ? "complete" : "incomplete";
        res["summary"] = string.Join(" ", parts);
        return res;
    }

    static readonly Dictionary<int, string> CbsStates = new()
    {
        [0x00] = "absent", [0x05] = "uninstall-pending", [0x10] = "resolving", [0x20] = "resolved", [0x30] = "staging",
        [0x40] = "staged", [0x50] = "superseded", [0x60] = "install-pending", [0x65] = "partially-installed",
        [0x70] = "installed", [0x80] = "permanent",
    };

    static List<Dictionary<string, object?>> CumulativeUpdates()
    {
        var list = new List<Dictionary<string, object?>>();
        using var pk = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\Packages");
        if (pk == null) throw new InvalidOperationException("Windows' servicing package list could not be opened");
        foreach (var n in pk.GetSubKeyNames())
        {
            if (!n.StartsWith("Package_for_RollupFix~", StringComparison.OrdinalIgnoreCase)) continue;
            using var k = pk.OpenSubKey(n);
            int? state = k?.GetValue("CurrentState") is int s ? s : null;
            string? installed = null;
            if (k?.GetValue("InstallTimeHigh") is int hi && k.GetValue("InstallTimeLow") is int lo)
            {
                long ft = ((long)(uint)hi << 32) | (uint)lo;
                try { installed = DateTime.FromFileTimeUtc(ft).ToString("yyyy-MM-ddTHH:mm:ssZ"); } catch { }
            }
            list.Add(new Dictionary<string, object?>
            {
                ["version"] = n[(n.LastIndexOf('~') + 1)..],
                ["state"] = state is int st ? CbsStates.GetValueOrDefault(st, $"0x{st:X}") : null,
                ["installedUtc"] = installed,
            });
        }
        // Newest first by version ("26100.10000" sorts after "26100.9711"), at most 10.
        return list.OrderByDescending(p => System.Version.TryParse((string?)p["version"], out var v) ? v : new System.Version())
                   .Take(10).ToList();
    }

    // ---- snapshot.windowsChanges ------------------------------------------------------

    // Names modified Windows builds and their tools leave in the places checked. Matched as words.
    static readonly Regex KnownName = new(
        @"\b(tiny ?1[01]|atlas ?os|atlas|revi ?os|revision|ghost ?spectre|x-?lite|ameliorated|ame wizard|" +
        @"nexus ?lite|kernel ?os|superlite|windows lite|phoenix ?lite|gg ?os|spectre ?os)\b",
        RegexOptions.IgnoreCase);

    // Parts every stock Windows 10/11 client has. Missing means the service is gone from the registry.
    static readonly (string Service, string What)[] StockParts =
    {
        ("wuauserv", "Windows Update"), ("UsoSvc", "Update Orchestrator"), ("WaaSMedicSvc", "Windows Update Medic"),
        ("BITS", "Background Intelligent Transfer"), ("DoSvc", "Delivery Optimization"),
        ("TrustedInstaller", "Windows Modules Installer"), ("WinDefend", "Microsoft Defender Antivirus"),
        ("WdFilter", "Microsoft Defender's file filter driver"), ("SecurityHealthService", "Windows Security"),
        ("wscsvc", "Security Center"), ("mpssvc", "Windows Firewall"), ("WSearch", "Windows Search"),
        ("DiagTrack", "Connected User Experiences and Telemetry"), ("WerSvc", "Windows Error Reporting"),
        ("SysMain", "SysMain"), ("AppXSvc", "AppX Deployment"), ("InstallService", "Microsoft Store Install"),
        ("Spooler", "Print Spooler"),
    };

    static readonly string[] UpdatePolicyValues =
    {
        "NoAutoUpdate", "AUOptions", "DisableWindowsUpdateAccess", "SetDisableUXWUAccess",
        "DoNotConnectToWindowsUpdateInternetLocations", "UseWUServer", "WUServer",
        "TargetReleaseVersion", "ProductVersion", "TargetReleaseVersionInfo",
        "DeferQualityUpdates", "DeferQualityUpdatesPeriodInDays", "DeferFeatureUpdates", "DeferFeatureUpdatesPeriodInDays",
        "ExcludeWUDriversInQualityUpdate", "DisableOSUpgrade",
    };

    public static Dictionary<string, object?> Changes()
    {
        var res = new Dictionary<string, object?>
        {
            ["method"] = "Registry and file reads: the maker name Windows records, update settings, services every stock Windows has, " +
                         "setup bypass values, tweak-tool records, and whether Windows' own files are backed by its component store.",
            ["note"] = "Each item is something a modified Windows (Tiny11, AtlasOS, ReviOS, Ghost Spectre and the like) or a tweak tool commonly leaves. " +
                       "One alone does not prove a modified Windows: an owner or an IT department can set the same things. None of it says anything about the controller.",
        };
        var checks = new List<Dictionary<string, object?>>();
        var signs = new List<string>();
        var knownNames = new List<string>();

        // nameCheck: what it saw is told once, in the known-names line, not again as its own sign.
        void Check(string name, Func<(bool Seen, string Detail)> run, bool nameCheck = false)
        {
            try
            {
                var (seen, detail) = run();
                checks.Add(new() { ["check"] = name, ["result"] = seen ? "seen" : "not-seen", ["detail"] = detail });
                if (seen && !nameCheck) signs.Add(detail);
            }
            catch (Exception ex)
            {
                checks.Add(new() { ["check"] = name, ["result"] = "could-not-check", ["detail"] = Scrub(ex.Message) });
            }
        }

        bool MatchName(string where, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var m = KnownName.Match(value);
            if (m.Success) knownNames.Add($"{where} contains \"{m.Value}\"");
            return m.Success;
        }

        // The maker name Windows records. Normally the PC maker (or empty on a self-built PC with retail Windows).
        Check("maker name Windows records", () =>
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation");
            var oem = new Dictionary<string, object?>();
            bool known = false;
            foreach (var v in new[] { "Manufacturer", "Model", "SupportURL" })
            {
                string? s = Cap(Scrub(k?.GetValue(v) as string));
                if (!string.IsNullOrEmpty(s)) { oem[v] = s; known |= MatchName($"OEMInformation {v}", s); }
            }
            res["oemInformation"] = oem;
            return (known, (oem.Count == 0 ? "No maker name recorded." : "Recorded: " + string.Join(", ", oem.Select(p => $"{p.Key} \"{p.Value}\"")) + ".") +
                           (known ? " It names a known modified Windows." : ""));
        }, nameCheck: true);

        // Registered owner and organisation: only a known-name match leaves, never the value (often a real name).
        Check("registered owner and organisation", () =>
        {
            using var k = Registry.LocalMachine.OpenSubKey(NtKey);
            bool known = MatchName("RegisteredOrganization", k?.GetValue("RegisteredOrganization") as string);
            known |= MatchName("RegisteredOwner", k?.GetValue("RegisteredOwner") as string);
            return (known, "The values are not sent (the owner is often a real name), only a match with a known modified-Windows name" +
                           (known ? ", and there is one." : "; there is none."));
        }, nameCheck: true);

        // AME Wizard (used by AtlasOS, ReviOS and other playbooks) records what it applied.
        Check("tweak playbooks applied (AME Wizard)", () =>
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\AME\Playbooks\Applied");
            if (k == null) return (false, "No AME Wizard playbook record.");
            var books = new List<string>();
            foreach (var id in k.GetSubKeyNames().Take(10))
            {
                using var pb = k.OpenSubKey(id);
                string label = Cap(Scrub(pb?.GetValue("Name") as string ?? pb?.GetValue("Title") as string)) ?? id;
                string? ver = Cap(Scrub(pb?.GetValue("Version") as string));
                MatchName("AME playbook", label);
                books.Add(ver != null ? $"{label} {ver}" : label);
            }
            res["appliedPlaybooks"] = books;
            return (true, books.Count == 0 ? "AME Wizard's playbook record exists, empty." : $"AME Wizard playbook applied: {string.Join(", ", books)}.");
        });

        Check("Windows parts missing", () =>
        {
            using var svcs = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services")
                ?? throw new InvalidOperationException("the service list could not be opened");
            var missing = new List<string>();
            var disabled = new List<string>();
            foreach (var (svc, what) in StockParts)
            {
                using var k = svcs.OpenSubKey(svc);
                if (k == null) missing.Add($"{what} ({svc})");
                else if (k.GetValue("Start") is int start && start == 4) disabled.Add($"{what} ({svc})");
            }
            res["missingParts"] = missing;
            res["disabledParts"] = disabled;
            if (disabled.Count > 0) signs.Add($"Disabled: {string.Join(", ", disabled)}.");
            checks.Add(new() { ["check"] = "Windows parts disabled", ["result"] = disabled.Count > 0 ? "seen" : "not-seen",
                ["detail"] = disabled.Count > 0 ? string.Join(", ", disabled) : $"None of {StockParts.Length} checked is disabled." });
            return (missing.Count > 0, missing.Count > 0
                ? $"Gone from Windows: {string.Join(", ", missing)}."
                : $"All {StockParts.Length} checked are present.");
        });

        Check("Windows Update policies", () =>
        {
            var set = new Dictionary<string, object?>();
            foreach (var path in new[] { @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU" })
            {
                using var k = Registry.LocalMachine.OpenSubKey(path);
                if (k == null) continue;
                foreach (var v in UpdatePolicyValues)
                {
                    object? val = k.GetValue(v);
                    if (val == null) continue;
                    // A WSUS address names a server on someone's network: only that it is set leaves.
                    set[v] = v == "WUServer" ? "(set)" : val is string s ? Cap(Scrub(s)) : val;
                }
            }
            res["updatePolicies"] = set;
            bool blocks = set.GetValueOrDefault("NoAutoUpdate") is 1 || set.GetValueOrDefault("DisableWindowsUpdateAccess") is 1 ||
                          set.GetValueOrDefault("SetDisableUXWUAccess") is 1 || set.GetValueOrDefault("DoNotConnectToWindowsUpdateInternetLocations") is 1;
            return (blocks, set.Count == 0 ? "No Windows Update policy set."
                : $"{(blocks ? "Policies that stop or hide Windows Update: " : "Update policies set: ")}{string.Join(", ", set.Select(p => $"{p.Key}={p.Value}"))}.");
        });

        Check("Windows Update paused", () =>
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings");
            string? until = k?.GetValue("PauseUpdatesExpiryTime") as string ?? k?.GetValue("PauseQualityUpdatesEndTime") as string;
            if (until != null && DateTime.TryParse(until, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var t) && t > DateTime.UtcNow)
                return (true, $"Windows Update is paused until {t:yyyy-MM-dd}.");
            return (false, until == null ? "No pause set." : $"A pause was set, ended {Cap(until)}.");
        });

        // Setup's requirement bypass is read by Windows Setup; on an installed Windows it is left over from a
        // modified install image (Tiny11 writes it) or from someone upgrading by hand.
        Check("setup requirement bypass left in Windows", () =>
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\Setup\LabConfig");
            var vals = k?.GetValueNames().Where(n => n.StartsWith("Bypass", StringComparison.OrdinalIgnoreCase)).ToList() ?? new();
            res["setupBypass"] = vals;
            return (vals.Count > 0, vals.Count > 0 ? $"Setup bypass values in the installed Windows: {string.Join(", ", vals)}." : "None.");
        });

        // Windows' own files are hard links into its component store (WinSxS); a stripped store breaks that.
        Check("Windows' files backed by its component store", () =>
        {
            var links = HardLinks(KernelPath, out string? err);
            if (links == null) throw new InvalidOperationException($"the kernel file's links could not be read ({err})");
            bool inStore = links.Any(l => StoreFolder(l) != null);
            return (!inStore, inStore ? "Windows' kernel file is linked into its component store (WinSxS), as on a normal install."
                : "Windows' kernel file is not linked into its component store (WinSxS). On a normal install it is; a store that was removed or rebuilt would explain it.");
        });

        Check("Microsoft Edge", () =>
        {
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            bool there = File.Exists(Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe")) ||
                         File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"));
            return (!there, there ? "Installed." : "Not installed. Light Windows builds remove it; in the EU an owner can also uninstall it.");
        });

        res["knownNames"] = knownNames;
        if (knownNames.Count > 0) signs.Insert(0, $"Names of a known modified Windows: {string.Join("; ", knownNames)}.");
        res["checks"] = checks;
        var failed = checks.Where(c => (string?)c["result"] == "could-not-check").Select(c => (string)c["check"]!).ToList();
        res["status"] = failed.Count == 0 ? "complete" : "incomplete";
        res["signs"] = signs;
        res["summary"] = (signs.Count > 0
                ? $"Seen: {string.Join(" ", signs)}"
                : $"None of the {checks.Count - failed.Count} checks that ran saw a sign of a modified Windows. That is not proof that it is unmodified.")
            + (failed.Count > 0 ? $" Could not check: {string.Join(", ", failed)}." : "");
        return res;
    }

    // ---- is a Windows driver the copy this Windows installed? ----------------------------

    /// For a driver file that is (or says it is) Microsoft's: does its version belong to this Windows, and is
    /// it the copy Windows' own setup or updates put there (a hard link into the component store)?
    public static Dictionary<string, object?> DriverFitsWindows(string path, FileVersionInfo fv)
    {
        var res = new Dictionary<string, object?>();
        try
        {
            var win = FilesVersion.Value;
            int? ubr = Ubr.Value;
            var ver = VersionOf(fv);
            res["fileVersion"] = ver?.Text;
            res["windowsFilesVersion"] = win?.Text;
            res["windowsUpdateRevision"] = ubr;

            // Recorded, not judged: Windows links most of its drivers into its component store (WinSxS), but it
            // copies some (measured: two inbox Bluetooth drivers on a stock 25H2), so a missing link proves nothing.
            var links = HardLinks(path, out string? linkErr);
            var store = links?.Select(StoreFolder).FirstOrDefault(c => c != null);
            res["componentStore"] = store != null ? $"{store.Value.Name} {store.Value.Version}"
                : links == null ? $"could not read the file's links ({linkErr})" : "not linked";

            string result, why;
            if (ver == null)
                (result, why) = ("could-not-tell", "The file carries no version.");
            else if (win == null || !win.WindowsNumbered)
                (result, why) = ("could-not-tell", "The version of Windows' own files could not be read, so there is nothing to compare with.");
            else if (!ver.WindowsNumbered)
                (result, why) = ("not-compared", $"Versioned apart from Windows ({ver}), so its build is not compared.");
            else if (ver.Build != win.Build)
                (result, why) = ("other-windows-build", $"Its version {ver} is from Windows build {ver.Build}; this PC's Windows files are build {win.Build} ({win}). " +
                                 "A driver that comes with Windows carries the build of the Windows it came with.");
            else if (ubr is int u && ver.Revision > u)
                (result, why) = ("newer-than-windows", $"Its revision ({ver}) is above the update revision Windows says it is at ({u}).");
            else
                (result, why) = ("matches-windows", $"Same Windows build as this PC ({ver}; Windows' files are {win})" +
                    (store != null ? $", and it is the copy in Windows' component {store.Value.Name} {store.Value.Version}." : "."));
            res["result"] = result;
            res["why"] = why;
        }
        catch (Exception ex) { res["result"] = "could-not-tell"; res["why"] = $"The check failed: {Scrub(ex.Message)}"; }
        return res;
    }

    public static bool OutOfPlace(object? fit) =>
        fit is Dictionary<string, object?> f && f.GetValueOrDefault("result") is "other-windows-build" or "newer-than-windows";

    /// "\Windows\WinSxS\amd64_dual_usbhub3.inf_31bf3856ad364e35_10.0.26100.7705_none_4b9d...\USBHUB3.SYS"
    /// -> ("dual_usbhub3.inf", "10.0.26100.7705").
    static (string Name, string Version)? StoreFolder(string link)
    {
        var m = Regex.Match(link, @"\\WinSxS\\[^_\\]+_(?<name>[^\\]+?)_[0-9a-f]{16}_(?<ver>\d+\.\d+\.\d+\.\d+)_[^\\]+\\[^\\]+$", RegexOptions.IgnoreCase);
        return m.Success ? (m.Groups["name"].Value, m.Groups["ver"].Value) : null;
    }

    /// Every name the file has on its volume (its hard links), without the drive letter. Null if they can't be read.
    static List<string>? HardLinks(string path, out string? error)
    {
        error = null;
        static string Text(char[] b) { int i = Array.IndexOf(b, '\0'); return new string(b, 0, i >= 0 ? i : b.Length); }
        var names = new List<string>();
        var buf = new char[1024];
        uint len = (uint)buf.Length;
        IntPtr h = FindFirstFileNameW(path, 0, ref len, buf);
        if (h == new IntPtr(-1) && Marshal.GetLastWin32Error() == 234 && len > buf.Length)     // ERROR_MORE_DATA
        {
            buf = new char[len + 1];
            len = (uint)buf.Length;
            h = FindFirstFileNameW(path, 0, ref len, buf);
        }
        if (h == new IntPtr(-1)) { error = $"error {Marshal.GetLastWin32Error()}"; return null; }
        try
        {
            names.Add(Text(buf));
            while (names.Count < 64)
            {
                Array.Clear(buf);
                len = (uint)buf.Length;
                if (!FindNextFileNameW(h, ref len, buf))
                {
                    if (Marshal.GetLastWin32Error() != 234 || len <= buf.Length) break;   // 38 = no more names
                    buf = new char[len + 1];
                    len = (uint)buf.Length;
                    if (!FindNextFileNameW(h, ref len, buf)) break;
                }
                names.Add(Text(buf));
            }
        }
        finally { FindClose(h); }
        return names;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr FindFirstFileNameW(string fileName, uint flags, ref uint len, [Out] char[] linkName);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool FindNextFileNameW(IntPtr find, ref uint len, [Out] char[] linkName);
    [DllImport("kernel32.dll")] static extern bool FindClose(IntPtr find);

    // ---- helpers -------------------------------------------------------------------------

    static string? Cap(string? s) => s == null ? null : s.Length <= 120 ? s : s[..120] + "...";

    /// The Windows username and PC name never leave in a string Windows or a driver happens to carry.
    public static string? Scrub(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        foreach (var (who, mask) in new[] { (Environment.UserName, "<user>"), (Environment.MachineName, "<pc>") })
            if (who.Length >= 3) s = Regex.Replace(s, Regex.Escape(who), mask, RegexOptions.IgnoreCase);
        return s.Trim();
    }
}
