// Pad UID (CH32 ESIG, 12 bytes) so the board's factory calibration can be
// looked up. Only PS4 mode (054C:05C4) exposes it in the mode DeepLog records
// in: HID feature report 0x90 = [0x90, GET_UID=0x03, 0...] then GET feature
// 0x91 -> [0x91, 0xAA ok, uid[12], ...] (setupmariusheiercomclaudecode
// tools/read_app_info.py, firmware gamepad_commands.c). hid.dll, no admin.
//
// XInput gaming mode has no vendor channel for it (GET_UID lives on the
// setup-mode WebUSB bulk pipe), so it is skipped with the reason written into
// the bundle. Never throws, never blocks the log for more than ~2 s.

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

static class PadUid
{
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr sec,
        uint disposition, uint flags, IntPtr template);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr preparsed, byte[] caps);

    static class Hid
    {
        public static SafeFileHandle Open(string path, uint access) =>
            CreateFile(path, access, 0x03, IntPtr.Zero, 3, 0, IntPtr.Zero);

        /// HIDP_CAPS.FeatureReportByteLength (offset 8), including the report-id byte.
        public static int FeatureLength(SafeFileHandle h)
        {
            if (!HidD_GetPreparsedData(h, out IntPtr pp)) return 0;
            try
            {
                var caps = new byte[64];
                HidP_GetCaps(pp, caps);
                return BitConverter.ToUInt16(caps, 8);
            }
            finally { HidD_FreePreparsedData(pp); }
        }
    }

    public static Dictionary<string, object?> Read(string vidPid, string? hidPath, string? padInstanceId)
    {
        var res = new Dictionary<string, object?> { ["uid"] = null };
        if (vidPid != "054C:05C4")
        {
            res["method"] = "none";
            res["skipped"] = "XInput gaming mode exposes no UID command (GET_UID is only on the setup-mode WebUSB pipe, "
                           + "and switching mode would interrupt the owner); not attempted";
            // Some firmware may put a UID in the USB serial string; record it only if it has the UID's shape.
            string tail = padInstanceId?[(padInstanceId.LastIndexOf('\\') + 1)..] ?? "";
            if (System.Text.RegularExpressions.Regex.IsMatch(tail, "^[0-9A-Fa-f]{24}$"))
            {
                res["uid"] = tail.ToUpperInvariant();
                res["method"] = "usb-serial-string (24 hex, UID-shaped)";
            }
            return res;
        }
        if (hidPath == null) { res["method"] = "hid-feature 0x90/0x91"; res["error"] = "PS4 HID collection not found"; return res; }

        res["method"] = "hid-feature 0x90/0x91 GET_UID (no admin)";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? uid = null, err = null;
        var t = new Thread(() =>
        {
            try { uid = ReadPs4(hidPath, out err); }
            catch (Exception ex) { err = ex.Message; }
        }) { IsBackground = true };
        t.Start();
        if (!t.Join(2000)) err = "timed out after 2 s";
        res["uid"] = uid;
        res["error"] = uid == null ? err : null;
        res["ms"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
        return res;
    }

    static string? ReadPs4(string path, out string? err)
    {
        err = null;
        // GENERIC_READ|GENERIC_WRITE: feature SET needs write access. Gamepad
        // collections are not exclusively owned by Windows, so this works as a user.
        using var h = Hid.Open(path, 0x80000000 | 0x40000000);
        if (h.IsInvalid) { err = $"open failed ({Marshal.GetLastWin32Error()})"; return null; }
        int len = Math.Max(Hid.FeatureLength(h), 64);

        var send = new byte[len];
        send[0] = 0x90; send[1] = 0x03;
        if (!HidD_SetFeature(h, send, len)) { err = $"SET_FEATURE 0x90 failed ({Marshal.GetLastWin32Error()})"; return null; }

        for (int attempt = 0; attempt < 5; attempt++)
        {
            var recv = new byte[len];
            recv[0] = 0x91;
            if (!HidD_GetFeature(h, recv, len)) { err = $"GET_FEATURE 0x91 failed ({Marshal.GetLastWin32Error()})"; Thread.Sleep(20); continue; }
            if (recv[1] == 0xAA)
                return string.Concat(recv.Skip(2).Take(12).Select(b => b.ToString("X2")));
            err = $"status 0x{recv[1]:X2}, not 0xAA";
            Thread.Sleep(20);
        }
        return null;
    }
}
