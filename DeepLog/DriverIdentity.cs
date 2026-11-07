// Every driver on the pad's USB path, and whose it is as far as Windows can say.
//
// Identity comes from the signature on the driver file, checked by WinVerifyTrust
// (catalog and embedded). No WMI, no admin, no network: revocation is off and URL
// retrieval is cache-only. Read-only, and run after the recording.
//
// The rule: a driver is called Microsoft's only on positive proof. A hand-written
// list of names can be wrong in both directions, so there is none here, and anything
// that can't be proved is listed as could-not-tell, never dropped.
//
// The trap: "signed by Microsoft" is not "made by Microsoft". Microsoft's hardware
// programme ("Microsoft Windows Hardware Compatibility Publisher") signs other
// companies' drivers. Only the signers Microsoft uses for its own files count, and
// only with a valid signature whose chain Windows says ends at a Microsoft root.

using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

static class DriverIdentity
{
    public const string IsMicrosoft = "microsoft", NotMicrosoft = "not-microsoft", CouldNotTell = "could-not-tell";
    const int CheckBudgetMs = 20000;

    public class Signature
    {
        public string Source = "";          // catalog | embedded
        public string? Signer, SignerOrg, Issuer;
        public bool Valid, MicrosoftRoot;
        public string Status = "";
    }

    // ---- the decision -----------------------------------------------------------

    static bool MicrosoftNamed(Signature s) =>
        (s.Signer ?? "").Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
        (s.SignerOrg ?? "").Contains("Microsoft", StringComparison.OrdinalIgnoreCase);

    static bool OwnMicrosoftSigner(Signature s) =>
        s.Valid && s.MicrosoftRoot && s.SignerOrg == "Microsoft Corporation" &&
        s.Signer is "Microsoft Windows" or "Microsoft Windows Publisher" or "Microsoft Corporation";

    static bool HardwareProgramme(Signature s) =>
        s.Valid && s.MicrosoftRoot &&
        ((s.Signer ?? "").Contains("Hardware Compatibility", StringComparison.OrdinalIgnoreCase) ||
         (s.Issuer ?? "").Contains("Third Party", StringComparison.OrdinalIgnoreCase));

    static string Who(Signature s) =>
        s.SignerOrg != null && s.SignerOrg != s.Signer ? $"{s.Signer} ({s.SignerOrg})" : s.Signer ?? "?";

    static (string Verdict, string Why) Decide(List<Signature> sigs, string? company)
    {
        bool named = !string.IsNullOrWhiteSpace(company);
        bool claimsMicrosoft = named && company!.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);

        if (sigs.FirstOrDefault(OwnMicrosoftSigner) is Signature own)
            return named && !claimsMicrosoft
                ? (NotMicrosoft, $"Microsoft signed this file as part of Windows ({own.Source} signature), but the file names {company} as its maker: it ships with Windows but by its own account is not Microsoft's.")
                : (IsMicrosoft, $"Signed by {own.Signer} ({own.Source} signature). Windows accepted it and its chain ends at a Microsoft root.");

        if (sigs.FirstOrDefault(s => s.Signer != null && !MicrosoftNamed(s)) is Signature vendor)
            return (NotMicrosoft, vendor.Valid
                ? $"Signed by {Who(vendor)} ({vendor.Source} signature). Windows accepted it."
                : $"Carries a signature from {Who(vendor)} ({vendor.Source}), which Windows did not accept ({vendor.Status}).");

        if (sigs.FirstOrDefault(HardwareProgramme) is Signature hw)
            return named && !claimsMicrosoft
                ? (NotMicrosoft, $"Signed through Microsoft's hardware programme ({hw.Signer}, {hw.Source} signature), which signs other companies' drivers. The file names {company} as its maker; that part is the file's own claim.")
                : (CouldNotTell, $"Signed through Microsoft's hardware programme ({hw.Signer}, {hw.Source} signature), which signs other companies' drivers too, and the file {(named ? "claims Microsoft" : "names no maker")}. That proves nothing either way.");

        if (sigs.FirstOrDefault(s => s.Signer != null) is Signature other)
            return (CouldNotTell, other.Valid
                ? $"Signed by {Who(other)} ({other.Source}), which is not a signer Microsoft uses for its own files, or its chain does not end at a Microsoft root."
                : $"Carries a signature naming {Who(other)} ({other.Source}), which Windows did not accept ({other.Status}).");

        return (CouldNotTell, $"Windows found no signature on this file{(named ? $". The file says it is from {company}, which is only its own claim" : "")}.");
    }

    // ---- one driver -------------------------------------------------------------

    /// stackEntry is what DEVPKEY_Device_Stack holds ("\Driver\xusb22"), a service name, or a file path.
    public static Dictionary<string, object?> Identify(string stackEntry)
    {
        var res = new Dictionary<string, object?> { ["driver"] = stackEntry };
        string name = stackEntry[(stackEntry.LastIndexOf('\\') + 1)..];
        try
        {
            string? path;
            if (Path.IsPathFullyQualified(stackEntry)) path = stackEntry;   // bench: a file, no service
            else
            {
                var svc = UsbProbe.Services(name);
                if (svc.GetValueOrDefault(name) is not Dictionary<string, object?> info)
                    return Fail(res, $"Could not ask Windows about its service: {svc.GetValueOrDefault("error")}.");
                string state = info["state"]?.ToString() ?? "";
                res["serviceState"] = state;
                if (state == "not-installed")
                    return Fail(res, $"Windows has no service called {name}, so there is no file to check.");
                string? bin = info.GetValueOrDefault("binary") as string;
                bool kernel = info.GetValueOrDefault("kernelDriver") is true;
                if (state.StartsWith("unreadable") || !info.ContainsKey("kernelDriver"))     // the SCM refused; a normal user can read the same two values here
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
                    if (key == null) return Fail(res, $"Windows would not let a normal user read the service {name} {state}.");
                    bin = key.GetValue("ImagePath", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    kernel = key.GetValue("Type") is int t && (t & 0x3) != 0;
                }
                if (!kernel)
                    return Fail(res, $"A service called {name} exists but it is not a kernel driver, so it may not be this one.");
                path = UsbProbe.BinaryFilePath(bin, name);
            }
            if (path == null || !File.Exists(path))
                return Fail(res, $"The file its service points to is not there{(path == null ? "" : $" ({MaskFile(path)})")}.");
            res["file"] = MaskFile(path);

            var fv = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            string? company = Scrub(fv.CompanyName);
            res["fileSays"] = new Dictionary<string, object?>
            {
                ["company"] = company, ["product"] = Scrub(fv.ProductName),
                ["description"] = Scrub(fv.FileDescription), ["version"] = Scrub(fv.FileVersion),
            };

            var problems = new List<string>();
            var sigs = ReadSignatures(path, problems);
            res["signatures"] = sigs.Select(s => new Dictionary<string, object?>
            {
                ["source"] = s.Source, ["signer"] = s.Signer, ["signerOrg"] = s.SignerOrg, ["issuer"] = s.Issuer,
                ["windowsAccepted"] = s.Valid, ["status"] = s.Status, ["chainEndsAtMicrosoftRoot"] = s.MicrosoftRoot,
            }).ToList();

            var (verdict, why) = Decide(sigs, company);
            if (problems.Count > 0 && verdict == CouldNotTell) why += " The check also hit: " + string.Join("; ", problems) + ".";
            res["verdict"] = verdict;
            res["why"] = why;

            // A Windows driver can be Microsoft's and still not belong to this Windows (a light Windows build
            // carrying drivers from an older one, or a file swapped by hand). Only facts, compared with Windows' own.
            if (verdict == IsMicrosoft || (company ?? "").Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
                res["windowsFit"] = WindowsInfo.DriverFitsWindows(path, fv);
        }
        catch (Exception ex) { return Fail(res, $"The check failed: {Scrub(ex.Message)}"); }
        return res;
    }

    static Dictionary<string, object?> Fail(Dictionary<string, object?> res, string why)
    {
        res["verdict"] = CouldNotTell;
        res["why"] = why;
        return res;
    }

    // Nothing from outside the Windows folder keeps its folder names, and the user and
    // PC name never leave in a string a driver or its certificate happens to carry.
    static string MaskFile(string path)
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        return path.StartsWith(win, StringComparison.OrdinalIgnoreCase)
            ? @"<Windows>\" + path[win.Length..]
            : @"<outside the Windows folder>\" + Scrub(Path.GetFileName(path));
    }

    static string? Scrub(string? s) => WindowsInfo.Scrub(s);

    // ---- the whole path ---------------------------------------------------------

    /// What goes in snapshot.padDriverPath. nodes are UsbProbe.DriverPathNodes, or stored stacks in a replay.
    public static Dictionary<string, object?> PathReport(List<Dictionary<string, object?>>? nodes)
    {
        var res = new Dictionary<string, object?>
        {
            ["method"] = "Signature on each driver's file as checked by Windows (WinVerifyTrust, catalog and embedded, no network). " +
                         "The file comes from the driver's service registration. fileSays is what the file says about itself, not proof. " +
                         "The stack of each device on the path is in usb.padPlacement.driverPath.",
            ["note"] = "On this list means Windows had the driver loaded on the controller's USB path when DeepLog looked. It does not say the driver causes a problem.",
        };
        if (nodes == null || nodes.Count == 0)
        {
            res["status"] = "no-pad";
            res["summary"] = "No controller was found on the USB tree, so its drivers were not checked. This does not mean there are none.";
            return res;
        }

        var seenOn = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var noStack = new List<string>();
        foreach (var n in nodes)
        {
            string where = n["where"]?.ToString() ?? "?";
            var stack = n["stack"] as List<string> ?? new();
            if (stack.Count == 0) noStack.Add($"{where}: {n.GetValueOrDefault("instanceId")}");
            foreach (var entry in stack)
            {
                if (!seenOn.ContainsKey(entry)) { seenOn[entry] = new(); order.Add(entry); }
                seenOn[entry].Add(where);
            }
        }

        var drivers = IdentifyAll(order);
        foreach (var d in drivers) d["seenOn"] = seenOn[(string)d["driver"]!].ToList();
        List<string> Named(string verdict) => drivers.Where(d => (string?)d["verdict"] == verdict).Select(d => (string)d["driver"]!).ToList();
        var foreign = Named(NotMicrosoft);
        var unsure = Named(CouldNotTell);

        // A driver package from elsewhere doesn't always show in the stack: Zadig's WinUSB binds Microsoft's own
        // file, and a user-mode driver hides behind Microsoft's WUDFRd. Windows knows which package it installed.
        var packages = nodes
            .Where(n => n.GetValueOrDefault("driverInf") is string inf &&
                        (inf.StartsWith("oem", StringComparison.OrdinalIgnoreCase) ||
                         !string.Equals(n.GetValueOrDefault("driverProvider") as string, "Microsoft", StringComparison.OrdinalIgnoreCase)))
            .Select(n => new Dictionary<string, object?>
            {
                ["where"] = n["where"], ["service"] = n.GetValueOrDefault("service"),
                ["provider"] = n.GetValueOrDefault("driverProvider"), ["inf"] = n["driverInf"],
            }).ToList();

        res["status"] = unsure.Count == 0 && noStack.Count == 0 ? "complete" : "incomplete";
        res["notMicrosoft"] = foreign;
        res["couldNotTell"] = unsure;
        res["microsoft"] = Named(IsMicrosoft);
        res["versionNotThisWindows"] = NotThisWindows(drivers);
        res["otherDriverPackages"] = packages;
        res["nodesWithNoStack"] = noStack;
        res["summary"] = Summary("on the controller's USB path", drivers, foreign, unsure,
            packages.Select(p => $"{p["provider"] ?? "no provider named"} ({p["inf"]}, on {p["where"]})").ToList(),
            noStack.Count == 0 ? null : $"Windows gave no driver list for {noStack.Count} device(s) on the path, so their drivers are unknown.");
        res["drivers"] = drivers;
        return res;
    }

    static string Short(string d) => d[(d.LastIndexOf('\\') + 1)..];

    /// Drivers proved (or claiming to be) Microsoft's whose file does not fit this Windows. A fact, not a fault.
    static List<string> NotThisWindows(List<Dictionary<string, object?>> drivers) => drivers
        .Where(d => WindowsInfo.OutOfPlace(d.GetValueOrDefault("windowsFit")))
        .Select(d => (string)d["driver"]!).ToList();

    static string Summary(string scope, List<Dictionary<string, object?>> drivers, List<string> foreign, List<string> unsure,
        List<string> packages, string? gap)
    {
        var parts = new List<string>();
        if (foreign.Count > 0)
            parts.Add($"Not Microsoft's, {scope}: {string.Join(", ", foreign.Select(Short))}.");
        if (unsure.Count > 0)
            parts.Add($"Could not tell whether Microsoft's: {string.Join(", ", unsure.Select(Short))}. See why on each.");
        var odd = drivers.Where(d => WindowsInfo.OutOfPlace(d.GetValueOrDefault("windowsFit"))).ToList();
        if (odd.Count > 0)
            parts.Add("Windows drivers whose version does not fit this Windows: " + string.Join("; ", odd.Select(d =>
                $"{Short((string)d["driver"]!)} {((Dictionary<string, object?>)d["windowsFit"]!)["fileVersion"]} ({((Dictionary<string, object?>)d["windowsFit"]!)["result"]})")) +
                ". See windowsFit on each.");
        var unchecked_ = drivers.Where(d => d.GetValueOrDefault("windowsFit") is Dictionary<string, object?> f && f.GetValueOrDefault("result") is "could-not-tell")
                                .Select(d => Short((string)d["driver"]!)).ToList();
        if (packages.Count > 0)
            parts.Add("Driver packages not from Windows' own set: " + string.Join(", ", packages) + ".");
        if (gap != null) parts.Add(gap);
        if (parts.Count == 0)
            return $"All {drivers.Count} drivers {scope} are proved Microsoft's by their signatures, and no Windows driver's version is from another Windows build or newer than this Windows" +
                   (unchecked_.Count > 0 ? $" (the version of {string.Join(", ", unchecked_)} could not be compared)." : ".");
        if (unchecked_.Count > 0) parts.Add($"The version of {string.Join(", ", unchecked_)} could not be compared with Windows'.");
        return string.Join(" ", parts);
    }

    /// What goes in snapshot.usbDrivers: every driver on every USB device and controller on the PC, identified
    /// the same way as the pad's path. nodes are UsbProbe.AllUsbNodes. Where each driver was seen is told by
    /// role, device class and VID:PID only: device names can carry a person's name, instance IDs a serial.
    public static Dictionary<string, object?> PcReport(List<Dictionary<string, object?>> nodes, bool truncated)
    {
        var res = new Dictionary<string, object?>
        {
            ["method"] = "Every device under every USB host controller (the controllers, hubs, USB devices and the functions Windows " +
                         "made for them), and the live driver stack of each. Each driver is identified as in padDriverPath. " +
                         "Device names and instance IDs are not recorded here.",
            ["note"] = "On this list means Windows had the driver loaded on a USB device or controller when DeepLog looked. It does not say the driver causes a problem.",
        };
        var seen = new Dictionary<string, (int Devices, HashSet<string> Roles, HashSet<string> Classes, HashSet<string> VidPids, bool OnPad)>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var noStack = new List<Dictionary<string, object?>>();
        var packages = new Dictionary<string, (string? Provider, string Inf, int Devices, HashSet<string> Classes)>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in nodes)
        {
            string role = n["where"]?.ToString() ?? "?";
            string cls = n.GetValueOrDefault("class") as string ?? "(no class)";
            string? vp = n.GetValueOrDefault("vidPid") as string;
            bool onPad = n.GetValueOrDefault("onPadPath") is true;
            var stack = n["stack"] as List<string> ?? new();
            if (stack.Count == 0)
                noStack.Add(new() { ["where"] = role, ["class"] = cls, ["vidPid"] = vp, ["problem"] = n.GetValueOrDefault("problem") });
            foreach (var entry in stack)
            {
                if (!seen.TryGetValue(entry, out var s)) { s = (0, new(), new(), new(), false); order.Add(entry); }
                s.Roles.Add(role); s.Classes.Add(cls);
                if (vp != null && s.VidPids.Count < 12) s.VidPids.Add(vp);
                seen[entry] = (s.Devices + 1, s.Roles, s.Classes, s.VidPids, s.OnPad || onPad);
            }
            if (n.GetValueOrDefault("driverInf") is string inf &&
                (inf.StartsWith("oem", StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(n.GetValueOrDefault("driverProvider") as string, "Microsoft", StringComparison.OrdinalIgnoreCase)))
            {
                if (!packages.TryGetValue(inf, out var p)) p = (Scrub(n.GetValueOrDefault("driverProvider") as string), inf, 0, new HashSet<string>());
                p.Classes.Add(cls);
                packages[inf] = (p.Provider, p.Inf, p.Devices + 1, p.Classes);
            }
        }

        var drivers = IdentifyAll(order);
        foreach (var d in drivers)
        {
            var s = seen[(string)d["driver"]!];
            d["seenOn"] = new Dictionary<string, object?>
            {
                ["devices"] = s.Devices, ["roles"] = s.Roles.ToList(), ["classes"] = s.Classes.ToList(),
                ["vidPids"] = s.VidPids.ToList(), ["onPadPath"] = s.OnPad,
            };
        }
        List<string> Named(string verdict) => drivers.Where(d => (string?)d["verdict"] == verdict).Select(d => (string)d["driver"]!).ToList();
        var foreign = Named(NotMicrosoft);
        var unsure = Named(CouldNotTell);
        // A device Windows has not started (a problem code) has no stack: nothing is loaded on it, which is
        // known. A started device with no stack is a gap.
        int unknownStacks = noStack.Count(x => x["problem"] is 0 or null);

        res["status"] = unsure.Count == 0 && unknownStacks == 0 && !truncated ? "complete" : "incomplete";
        res["devicesChecked"] = nodes.Count;
        res["notMicrosoft"] = foreign;
        res["couldNotTell"] = unsure;
        res["microsoft"] = Named(IsMicrosoft);
        res["versionNotThisWindows"] = NotThisWindows(drivers);
        res["otherDriverPackages"] = packages.Values.Select(p => new Dictionary<string, object?>
        {
            ["provider"] = p.Provider, ["inf"] = p.Inf, ["devices"] = p.Devices, ["classes"] = p.Classes.ToList(),
        }).ToList();
        res["devicesWithNoDriverList"] = noStack;
        var gaps = new List<string>();
        if (noStack.Count > unknownStacks)
            gaps.Add($"{noStack.Count - unknownStacks} device(s) Windows has not started (a problem code), so no driver is loaded on them.");
        if (unknownStacks > 0)
            gaps.Add($"Windows gave no driver list for {unknownStacks} running device(s), so their drivers are unknown.");
        if (truncated)
            gaps.Add($"The list stopped after {nodes.Count} devices, so devices past that are not covered.");
        res["summary"] = $"{nodes.Count} devices on the USB tree, {drivers.Count} different drivers. " + Summary("on a USB device or controller", drivers, foreign, unsure,
            packages.Values.Select(p => $"{p.Provider ?? "no provider named"} ({p.Inf}, {p.Devices} device(s): {string.Join("/", p.Classes)})").ToList(),
            gaps.Count == 0 ? null : string.Join(" ", gaps));
        res["drivers"] = drivers;
        return res;
    }

    /// padFiltersInStack, same shape as v2.1 (strings like "\Driver\HidHide"), same scope (the pad and what
    /// hangs off it), but no fixed list: everything there not proved Microsoft's. Null means no list could be
    /// made, and then the field is left out as in a v2.0 bundle rather than written as an empty, clean-looking [].
    public static List<string>? OldFilterField(Dictionary<string, object?> report)
    {
        if (report.GetValueOrDefault("drivers") is not List<Dictionary<string, object?>> drivers) return null;
        return drivers
            .Where(d => (string?)d["verdict"] != IsMicrosoft &&
                        d["seenOn"] is List<string> on && on.Any(w => w is "pad" or "below pad"))
            .Select(d => (string)d["driver"]!)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Bounded, so a stuck Cryptographic Services can't hold up the owner: whatever has not finished at the
    // deadline is reported as not known. One budget for the whole run, shared by the pad's path (asked first)
    // and the whole USB tree, and a driver checked once is not checked again.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, object?>> Checked =
        new(StringComparer.OrdinalIgnoreCase);
    static readonly System.Diagnostics.Stopwatch Waited = new();

    public static List<Dictionary<string, object?>> IdentifyAll(List<string> entries)
    {
        var todo = entries.Where(e => !Checked.ContainsKey(e)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int left = CheckBudgetMs - (int)Waited.ElapsedMilliseconds;
        if (todo.Count > 0 && left > 0)
        {
            Waited.Start();
            try { Task.Run(() => { foreach (var e in todo) Checked.TryAdd(e, Identify(e)); }).Wait(left); }
            catch (AggregateException) { }
            finally { Waited.Stop(); }
        }
        // Copies: each report adds its own seenOn.
        return entries.Select(e => Checked.TryGetValue(e, out var r)
            ? new Dictionary<string, object?>(r) { ["driver"] = e }
            : new Dictionary<string, object?>
            {
                ["driver"] = e, ["verdict"] = CouldNotTell,
                ["why"] = $"The signature check did not finish within {CheckBudgetMs / 1000} s.",
            }).ToList();
    }

    // ---- signatures -------------------------------------------------------------

    static List<Signature> ReadSignatures(string path, List<string> problems)
    {
        var sigs = new List<Signature>();
        // A catalog may hold only one of the two hashes. Stop at the first catalog entry that verifies.
        foreach (var alg in new[] { "SHA256", "SHA1" })
        {
            var s = CatalogSignature(path, alg, problems);
            if (s != null) sigs.Add(s);
            if (s is { Valid: true }) break;
        }
        // Also the embedded one: a hardware-programme catalog only says Microsoft, the maker's own
        // signature on the same file is what names them.
        var e = EmbeddedSignature(path);
        if (e != null) sigs.Add(e);
        return sigs;
    }

    static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    static Signature? CatalogSignature(string path, string alg, List<string> problems)
    {
        if (!CryptCATAdminAcquireContext2(out IntPtr admin, IntPtr.Zero, alg, IntPtr.Zero, 0))
        {
            problems.Add($"catalog lookup unavailable ({alg}, error {Marshal.GetLastWin32Error()})");
            return null;
        }
        IntPtr cat = IntPtr.Zero;
        try
        {
            using var fs = OpenShared(path);
            uint size = 0;
            CryptCATAdminCalcHashFromFileHandle2(admin, fs.SafeFileHandle, ref size, null, 0);
            if (size == 0) { problems.Add($"could not hash the file ({alg})"); return null; }
            var hash = new byte[size];
            fs.Position = 0;
            if (!CryptCATAdminCalcHashFromFileHandle2(admin, fs.SafeFileHandle, ref size, hash, 0))
            { problems.Add($"could not hash the file ({alg}, error {Marshal.GetLastWin32Error()})"); return null; }

            string tag = Convert.ToHexString(hash);
            Signature? best = null;
            IntPtr prev = IntPtr.Zero;
            // A file can be in several catalogs. Keep the best: one that verifies, Microsoft's own first.
            while ((cat = CryptCATAdminEnumCatalogFromHash(admin, hash, size, 0, ref prev)) != IntPtr.Zero)
            {
                prev = cat;
                var info = new CATALOG_INFO { cbStruct = (uint)Marshal.SizeOf<CATALOG_INFO>() };
                if (!CryptCATCatalogInfoFromContext(cat, ref info, 0)) continue;
                var s = VerifyCatalogMember(info.wszCatalogFile, tag, path, fs.SafeFileHandle, hash, admin);
                if (best == null || (s.Valid && !best.Valid) || (OwnMicrosoftSigner(s) && !OwnMicrosoftSigner(best))) best = s;
                if (OwnMicrosoftSigner(s)) break;
            }
            return best;
        }
        finally
        {
            if (cat != IntPtr.Zero) CryptCATAdminReleaseCatalogContext(admin, cat, 0);
            CryptCATAdminReleaseContext(admin, 0);
        }
    }

    static Signature VerifyCatalogMember(string catalog, string tag, string path, SafeFileHandle file, byte[] hash, IntPtr admin)
    {
        var pin = GCHandle.Alloc(hash, GCHandleType.Pinned);
        var info = new WINTRUST_CATALOG_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_CATALOG_INFO>(),
            pcwszCatalogFilePath = catalog, pcwszMemberTag = tag, pcwszMemberFilePath = path,
            hMemberFile = file.DangerousGetHandle(), pbCalculatedFileHash = pin.AddrOfPinnedObject(),
            cbCalculatedFileHash = (uint)hash.Length, hCatAdmin = admin,
        };
        IntPtr mem = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_CATALOG_INFO>());
        try
        {
            Marshal.StructureToPtr(info, mem, false);
            try { return Verify("catalog", WTD_CHOICE_CATALOG, mem); }
            finally { Marshal.DestroyStructure<WINTRUST_CATALOG_INFO>(mem); }
        }
        finally { Marshal.FreeHGlobal(mem); pin.Free(); }
    }

    static Signature? EmbeddedSignature(string path)
    {
        using var fs = OpenShared(path);
        var info = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path, hFile = fs.SafeFileHandle.DangerousGetHandle(),
        };
        IntPtr mem = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(info, mem, false);
            try
            {
                var s = Verify("embedded", WTD_CHOICE_FILE, mem);
                return s.Signer == null && s.Status is "0x800B0100" ? null : s;     // TRUST_E_NOSIGNATURE: there is none
            }
            finally { Marshal.DestroyStructure<WINTRUST_FILE_INFO>(mem); }
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    static Signature Verify(string source, uint choice, IntPtr unionInfo)
    {
        var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");   // WINTRUST_ACTION_GENERIC_VERIFY_V2
        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice = WTD_UI_NONE, fdwRevocationChecks = WTD_REVOKE_NONE,
            dwUnionChoice = choice, pInfo = unionInfo, dwStateAction = WTD_STATEACTION_VERIFY,
            dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL,   // never the network
        };
        int hr = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
        var sig = new Signature { Source = source, Valid = hr == 0, Status = hr == 0 ? "valid" : $"0x{hr:X8}" };
        try
        {
            var chain = SignerChain(data.hWVTStateData);
            if (chain.Count > 0)
            {
                sig.Signer = Scrub(chain[0].GetNameInfo(X509NameType.SimpleName, false));
                sig.Issuer = Scrub(chain[0].GetNameInfo(X509NameType.SimpleName, true));
                var org = Regex.Match(chain[0].Subject, @"(?:^|,\s*)O=(?:""([^""]*)""|([^,]*))");
                if (org.Success) sig.SignerOrg = Scrub(org.Groups[1].Success ? org.Groups[1].Value : org.Groups[2].Value);
                sig.MicrosoftRoot = EndsAtMicrosoftRoot(chain);
            }
            foreach (var c in chain) c.Dispose();
        }
        finally
        {
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
        }
        return sig;
    }

    static List<X509Certificate2> SignerChain(IntPtr state)
    {
        var certs = new List<X509Certificate2>();
        IntPtr prov = WTHelperProvDataFromStateData(state);
        IntPtr signer = prov == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(prov, 0, false, 0);
        if (signer == IntPtr.Zero) return certs;
        for (uint i = 0; ; i++)
        {
            IntPtr pc = WTHelperGetProvCertFromChain(signer, i);
            if (pc == IntPtr.Zero) return certs;
            IntPtr ctx = Marshal.ReadIntPtr(pc, IntPtr.Size);       // CRYPT_PROVIDER_CERT.pCert, after a DWORD + padding
            if (ctx == IntPtr.Zero) return certs;
            certs.Add(new X509Certificate2(ctx));
        }
    }

    /// Windows' own test (CERT_CHAIN_POLICY_MICROSOFT_ROOT), so a home-made root named "Microsoft" fails it.
    static bool EndsAtMicrosoftRoot(List<X509Certificate2> certs)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;
        chain.ChainPolicy.ExtraStore.AddRange(certs.ToArray());
        chain.Build(certs[0]);
        var para = new CERT_CHAIN_POLICY_PARA { cbSize = (uint)Marshal.SizeOf<CERT_CHAIN_POLICY_PARA>() };
        var status = new CERT_CHAIN_POLICY_STATUS { cbSize = (uint)Marshal.SizeOf<CERT_CHAIN_POLICY_STATUS>() };
        return CertVerifyCertificateChainPolicy(new IntPtr(7), chain.ChainContext, ref para, ref status) && status.dwError == 0;
    }

    // ---- interop ------------------------------------------------------------------

    const uint WTD_UI_NONE = 2, WTD_REVOKE_NONE = 0, WTD_CHOICE_FILE = 1, WTD_CHOICE_CATALOG = 2,
        WTD_STATEACTION_VERIFY = 1, WTD_STATEACTION_CLOSE = 2,
        WTD_REVOCATION_CHECK_NONE = 0x10, WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData, pSIPClientData;
        public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
        public IntPtr pInfo;
        public uint dwStateAction;
        public IntPtr hWVTStateData, pwszURLReference;
        public uint dwProvFlags, dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile, pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_CATALOG_INFO
    {
        public uint cbStruct, dwCatalogVersion;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszCatalogFilePath;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszMemberTag;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszMemberFilePath;
        public IntPtr hMemberFile, pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public IntPtr pcCatalogContext, hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CATALOG_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string wszCatalogFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CERT_CHAIN_POLICY_PARA { public uint cbSize, dwFlags; public IntPtr pvExtraPolicyPara; }
    [StructLayout(LayoutKind.Sequential)]
    struct CERT_CHAIN_POLICY_STATUS { public uint cbSize, dwError; public int lChainIndex, lElementIndex; public IntPtr pvExtraPolicyStatus; }

    [DllImport("wintrust.dll")]
    static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
    [DllImport("wintrust.dll")] static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
    [DllImport("wintrust.dll")]
    static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr prov, uint signer, [MarshalAs(UnmanagedType.Bool)] bool counter, uint counterIdx);
    [DllImport("wintrust.dll")] static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint cert);

    [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptCATAdminAcquireContext2(out IntPtr admin, IntPtr subsystem, string alg, IntPtr strongPolicy, uint flags);
    [DllImport("wintrust.dll", SetLastError = true)]
    static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr admin, SafeFileHandle file, ref uint size, byte[]? hash, uint flags);
    [DllImport("wintrust.dll", SetLastError = true)]
    static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr admin, byte[] hash, uint size, uint flags, ref IntPtr prev);
    [DllImport("wintrust.dll", SetLastError = true)]
    static extern bool CryptCATCatalogInfoFromContext(IntPtr cat, ref CATALOG_INFO info, uint flags);
    [DllImport("wintrust.dll")] static extern bool CryptCATAdminReleaseCatalogContext(IntPtr admin, IntPtr cat, uint flags);
    [DllImport("wintrust.dll")] static extern bool CryptCATAdminReleaseContext(IntPtr admin, uint flags);

    [DllImport("crypt32.dll")]
    static extern bool CertVerifyCertificateChainPolicy(IntPtr policy, IntPtr chain, ref CERT_CHAIN_POLICY_PARA para, ref CERT_CHAIN_POLICY_STATUS status);
}
