// OrclSKBK (Oracooll Surface Keyboard Backlight Keeper) - keeps the Surface keyboard backlight from timing out.
// Made by Claude (Anthropic's AI model), prompted, tested and directed by Oracooll. MIT License.
//
// How it works: Windows 11 25H2 drives Surface keyboard backlights through a standard
// HID "Keyboard Backlight" collection (usage page 0x0C, usage 0x07). The keyboard firmware
// turns the light off after ~30 s without physical key presses. This tray app re-sends the
// HID "Set Level" output report (usage 0x7B) every few seconds, which re-arms the firmware
// timer, so the light stays on. It follows whatever brightness Windows last set, so the
// keyboard's backlight key keeps working (and choosing "off" with that key is respected).
//
// Known limit (verified on Surface Laptop Studio 2): once the firmware has switched the light
// off, no HID write of any kind turns it back on. Only a physical key press, a trackpad touch
// or the display turning back on does. So: touch the trackpad once, and this app keeps it on.
//
// Design notes:
//  * Reports are encoded and decoded by the Windows HID parser (HidP_SetUsageValue and friends) using the
//    device's own preparsed descriptor data, so report IDs (including 0), field positions and sizes come
//    from the descriptor instead of being assumed.
//  * All device I/O runs on one background thread. The UI thread only decides when a refresh is due and
//    shows the results, so a stalled driver cannot freeze the tray menu; a watchdog cancels a stuck call.
//  * Per-keyboard state ("the user turned this one off with its key", failure counts) is kept per device.
//
// Build (no SDK needed, uses the .NET Framework compiler that ships with Windows): run build.ps1, or
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+
//     /out:OrclSKBK.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll
//     /win32manifest:app.manifest OrclSKBK.cs
//
// Written in C# 5 syntax on purpose so the in-box compiler can build it.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

[assembly: System.Reflection.AssemblyTitle("OrclSKBK " + OrclSKBK.AppInfo.Version)]
[assembly: System.Reflection.AssemblyProduct(OrclSKBK.AppInfo.Name)]
[assembly: System.Reflection.AssemblyDescription("OrclSKBK - Oracooll Surface Keyboard Backlight Keeper. Keeps the Surface keyboard backlight from timing out. Made by Claude, prompted by Oracooll.")]
[assembly: System.Reflection.AssemblyCompany("Made by Claude, prompted by Oracooll")]
[assembly: System.Reflection.AssemblyCopyright("MIT License. Copyright (c) 2026 Oracooll. Made by Claude.")]
[assembly: System.Reflection.AssemblyVersion(OrclSKBK.AppInfo.NumericVersion)]
[assembly: System.Reflection.AssemblyFileVersion(OrclSKBK.AppInfo.NumericVersion)]
[assembly: System.Reflection.AssemblyInformationalVersion(OrclSKBK.AppInfo.Version)]

namespace OrclSKBK
{
    /// <summary>Name and version. Versions follow the Oracooll 1.X.XXX scheme; bump both constants together.</summary>
    static class AppInfo
    {
        public const string Name = "OrclSKBK";
        public const string Version = "1.3.001";             // shown to users, used for the release tag
        public const string NumericVersion = "1.3.1.0";      // the same version in Windows' four-number form
        public const string DisplayName = Name + " " + Version;
        public const string LongName = "Oracooll Surface Keyboard Backlight Keeper";
    }

    // ------------------------------------------------------------------ Win32 / HID interop
    static class Native
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1;
        public const uint FILE_SHARE_WRITE = 2;
        public const uint OPEN_EXISTING = 3;
        public const int HidP_Input = 0, HidP_Output = 1, HidP_Feature = 2;
        public const int HIDP_STATUS_SUCCESS = 0x00110000;
        public const uint CR_SUCCESS = 0;
        public const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;
        public const uint THREAD_TERMINATE = 0x0001;   // access right required by CancelSynchronousIo
        public static readonly Guid GUID_DEVINTERFACE_HID = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");
        public static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new Guid("6FE69556-704A-47A0-8F24-C28D936FDA47");
        public const int WM_POWERBROADCAST = 0x0218;
        public const int PBT_POWERSETTINGCHANGE = 0x8013;
        public const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(SafeFileHandle h, byte[] buf, uint len, out uint written, IntPtr overlapped);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CancelSynchronousIo(IntPtr thread);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern uint CM_Get_Device_Interface_List_Size(out uint size, ref Guid classGuid, string deviceId, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern uint CM_Get_Device_Interface_List(ref Guid classGuid, string deviceId, char[] buffer, uint bufferLen, uint flags);

        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
        [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr pp);
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
        [DllImport("hid.dll")] public static extern int HidP_GetValueCaps(int reportType, [Out] HIDP_VALUE_CAPS[] caps, ref ushort len, IntPtr pp);
        [DllImport("hid.dll")] public static extern int HidP_InitializeReportForID(int reportType, byte reportId, IntPtr pp, byte[] report, uint reportLength);
        [DllImport("hid.dll")] public static extern int HidP_SetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage, uint value, IntPtr pp, byte[] report, uint reportLength);
        [DllImport("hid.dll")] public static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage, out uint value, IntPtr pp, byte[] report, uint reportLength);
        [DllImport("hid.dll")] public static extern int HidP_GetUsageValueArray(int reportType, ushort usagePage, ushort linkCollection, ushort usage, byte[] values, ushort valuesLength, IntPtr pp, byte[] report, uint reportLength);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, uint flags);
        [DllImport("user32.dll")] public static extern bool UnregisterPowerSettingNotification(IntPtr handle);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("wtsapi32.dll", SetLastError = true, EntryPoint = "WTSQuerySessionInformationW")]
        static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);
        [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr memory);

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES { public uint Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage; public ushort UsagePage;
            public ushort InputReportByteLength; public ushort OutputReportByteLength; public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps; public ushort NumberInputValueCaps; public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps; public ushort NumberOutputValueCaps; public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps; public ushort NumberFeatureValueCaps; public ushort NumberFeatureDataIndices;
        }

        // Explicit layout of HIDP_VALUE_CAPS (72 bytes); only the fields used here are declared.
        [StructLayout(LayoutKind.Explicit, Size = 72)]
        public struct HIDP_VALUE_CAPS
        {
            [FieldOffset(0)] public ushort UsagePage;
            [FieldOffset(2)] public byte ReportID;
            [FieldOffset(12)] public byte IsRange;
            [FieldOffset(15)] public byte IsAbsolute;
            [FieldOffset(18)] public ushort BitSize;
            [FieldOffset(20)] public ushort ReportCount;
            [FieldOffset(40)] public int LogicalMin;
            [FieldOffset(44)] public int LogicalMax;
            [FieldOffset(56)] public ushort UsageMin;
            [FieldOffset(58)] public ushort UsageMax;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct POWERBROADCAST_SETTING { public Guid PowerSetting; public uint DataLength; public byte Data; }

        public static List<string> EnumerateHidInterfaces()
        {
            var result = new List<string>();
            Guid g = GUID_DEVINTERFACE_HID;
            uint size;
            if (CM_Get_Device_Interface_List_Size(out size, ref g, null, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS || size == 0) return result;
            var buf = new char[size];
            if (CM_Get_Device_Interface_List(ref g, null, buf, size, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS) return result;
            var sb = new StringBuilder();
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i] == '\0') { if (sb.Length > 0) { result.Add(sb.ToString()); sb.Length = 0; } else break; }
                else sb.Append(buf[i]);
            }
            return result;
        }

        /// <summary>
        /// True if this session is locked or not the active one (for example disconnected by fast user switching),
        /// false if it is active and unlocked, null if Windows could not say.
        /// </summary>
        public static bool? IsSessionLocked()
        {
            IntPtr buf; int bytes;
            if (!WTSQuerySessionInformation(IntPtr.Zero, -1 /* current session */, 25 /* WTSSessionInfoEx */, out buf, out bytes) || buf == IntPtr.Zero) return null;
            try
            {
                if (bytes < 20 || Marshal.ReadInt32(buf, 0) != 1) return null;   // WTSINFOEX.Level must be 1
                // WTSINFOEX_LEVEL1_W sits in an 8-byte aligned union: SessionId at 8, SessionState at 12, SessionFlags at 16.
                int state = Marshal.ReadInt32(buf, 12);   // WTS_CONNECTSTATE_CLASS, 0 = WTSActive
                int flags = Marshal.ReadInt32(buf, 16);   // WTS_SESSIONSTATE_LOCK = 0, WTS_SESSIONSTATE_UNLOCK = 1
                if (state != 0) return true;
                if (flags == 0) return true;
                if (flags == 1) return false;
                return null;
            }
            finally { WTSFreeMemory(buf); }
        }
    }

    // ------------------------------------------------------------------ Log file
    /// <summary>
    /// %LOCALAPPDATA%\OrclSKBK\keeper.log. Normal lines are written only while "Write log file" is ticked.
    /// Crash reports are always written. Both go through the same rotation (one previous file kept, ~512 KB each).
    /// </summary>
    static class Logger
    {
        public static volatile bool Enabled;
        static readonly object Sync = new object();
        const long MaxBytes = 512 * 1024;

        public static string Dir { get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrclSKBK"); } }

        public static void Write(string message) { if (Enabled) Append(message); }

        public static void Crash(string what, Exception ex) { Append(what + ": " + (ex == null ? "(no details)" : ex.ToString())); }

        static void Append(string message)
        {
            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(Dir);
                    string file = System.IO.Path.Combine(Dir, "keeper.log");
                    if (File.Exists(file) && new FileInfo(file).Length > MaxBytes)
                    {
                        string old = System.IO.Path.Combine(Dir, "keeper.old.log");
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(file, old);
                    }
                    File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ Backlight HID device
    /// <summary>Immutable description of a detected backlight, safe to hand to the UI thread.</summary>
    sealed class DeviceInfo
    {
        public string Path, Name;
        public ushort Vid, Pid;
        public int LogicalMin, LogicalMax;
        public int[] Suggestions;
    }

    /// <summary>One HID Keyboard Backlight collection. Used only from the device thread (or the self test).</summary>
    sealed class BacklightDevice : IDisposable
    {
        public const ushort UsagePageConsumer = 0x0C;
        public const ushort UsageKeyboardBacklight = 0x07;
        public const ushort UsageSetLevel = 0x7B;
        public const ushort UsageLevelSuggestion = 0x517;

        public string Path;
        public string Name = "keyboard backlight";
        public ushort Vid, Pid;
        public int LogicalMin, LogicalMax;
        public int[] Suggestions = new int[0];
        public string LastReportHex = "";

        int _outputLength, _featureLength;
        byte _setLevelReportId; int _setLevelBits;
        bool _hasSuggestions; byte _suggestionReportId; int _suggestionCount, _suggestionBits;
        bool _hasInitialLevel; byte _initialLevelReportId; int _initialLevelBits;
        IntPtr _pp = IntPtr.Zero;    // preparsed descriptor data, kept for the device's lifetime to encode and decode reports
        SafeFileHandle _h;

        public bool IsOpen { get { return _h != null && !_h.IsInvalid && !_h.IsClosed && _pp != IntPtr.Zero; } }

        public DeviceInfo Snapshot()
        {
            var i = new DeviceInfo();
            i.Path = Path; i.Name = Name; i.Vid = Vid; i.Pid = Pid;
            i.LogicalMin = LogicalMin; i.LogicalMax = LogicalMax; i.Suggestions = (int[])Suggestions.Clone();
            return i;
        }

        public static List<BacklightDevice> FindAll(Action<string> log)
        {
            var found = new List<BacklightDevice>();
            foreach (var path in Native.EnumerateHidInterfaces())
            {
                BacklightDevice d = null;
                try { d = TryOpen(path, log); } catch (Exception ex) { log("Error probing " + path + ": " + ex.Message); }
                if (d != null) found.Add(d);
            }
            return found;
        }

        static BacklightDevice TryOpen(string path, Action<string> log)
        {
            // The interface path carries no usage information, so every HID collection is opened with no access
            // rights (allowed even for keyboards and mice) just to read its capabilities.
            var probe = Native.CreateFile(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            if (probe.IsInvalid) return null;
            var d = new BacklightDevice();
            bool keep = false;
            try
            {
                if (!Native.HidD_GetPreparsedData(probe, out d._pp)) { d._pp = IntPtr.Zero; return null; }
                Native.HIDP_CAPS caps;
                if (Native.HidP_GetCaps(d._pp, out caps) != Native.HIDP_STATUS_SUCCESS) return null;
                if (caps.UsagePage != UsagePageConsumer || caps.Usage != UsageKeyboardBacklight) return null;

                d.Path = path;
                d._outputLength = caps.OutputReportByteLength;
                d._featureLength = caps.FeatureReportByteLength;

                bool hasSetLevel = false;
                foreach (var vc in d.GetValueCaps(Native.HidP_Output, caps.NumberOutputValueCaps))
                {
                    if (vc.UsagePage != UsagePageConsumer || vc.IsRange != 0 || vc.UsageMin != UsageSetLevel) continue;
                    if (vc.ReportCount != 1 || vc.BitSize < 1 || vc.BitSize > 31) continue;
                    d._setLevelReportId = vc.ReportID; d._setLevelBits = vc.BitSize;
                    d.LogicalMin = vc.LogicalMin; d.LogicalMax = vc.LogicalMax;
                    hasSetLevel = true;
                    break;
                }
                if (!hasSetLevel || d.LogicalMax <= d.LogicalMin || d._outputLength < 1)
                {
                    log("Backlight collection without a usable Set Level output report, skipped: " + path);
                    return null;
                }
                foreach (var vc in d.GetValueCaps(Native.HidP_Feature, caps.NumberFeatureValueCaps))
                {
                    if (vc.UsagePage != UsagePageConsumer || vc.IsRange != 0 || vc.BitSize < 1 || vc.BitSize > 31 || vc.ReportCount < 1) continue;
                    if (vc.UsageMin == UsageLevelSuggestion && !d._hasSuggestions)
                    { d._hasSuggestions = true; d._suggestionReportId = vc.ReportID; d._suggestionCount = vc.ReportCount; d._suggestionBits = vc.BitSize; }
                    else if (vc.UsageMin == UsageSetLevel && vc.ReportCount == 1 && !d._hasInitialLevel)
                    { d._hasInitialLevel = true; d._initialLevelReportId = vc.ReportID; d._initialLevelBits = vc.BitSize; }
                }
                probe.Close();

                d._h = Native.CreateFile(path, Native.GENERIC_READ | Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
                if (d._h.IsInvalid)
                    d._h = Native.CreateFile(path, Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
                if (d._h.IsInvalid)
                {
                    log("Found a backlight collection but could not open it for writing (error " + Marshal.GetLastWin32Error() + "): " + path);
                    return null;
                }
                var attr = new Native.HIDD_ATTRIBUTES(); attr.Size = (uint)Marshal.SizeOf(typeof(Native.HIDD_ATTRIBUTES));
                if (Native.HidD_GetAttributes(d._h, ref attr)) { d.Vid = attr.VendorID; d.Pid = attr.ProductID; }
                d.Name = ReadProductName(d._h, d.Vid);
                d.ReadSuggestions();
                log(string.Format("Backlight device: {0} VID={1:X4} PID={2:X4} setLevelReport={3} ({4} bit) range={5}..{6} suggestions=[{7}] initialLevelReport={8} path={9}",
                    d.Name, d.Vid, d.Pid, d._setLevelReportId, d._setLevelBits, d.LogicalMin, d.LogicalMax,
                    string.Join(",", Array.ConvertAll(d.Suggestions, x => x.ToString())),
                    d._hasInitialLevel ? d._initialLevelReportId.ToString() : "none", path));
                keep = true;
                return d;
            }
            finally
            {
                if (!probe.IsClosed) probe.Close();
                if (!keep) d.Dispose();
            }
        }

        static string ReadProductName(SafeFileHandle h, ushort vid)
        {
            var buf = new byte[256];
            string s = "";
            if (Native.HidD_GetProductString(h, buf, buf.Length))
            {
                s = Encoding.Unicode.GetString(buf);
                int z = s.IndexOf('\0'); if (z >= 0) s = s.Substring(0, z);
                // The Surface mini-driver returns a raw USB string descriptor (bLength, bDescriptorType=3): strip that header.
                if (s.Length > 0 && (s[0] >> 8) == 3) s = s.Substring(1);
                s = s.Trim();
            }
            if (s.Length >= 8) return s;
            return vid == 0x045E ? "Surface keyboard" : "keyboard backlight";
        }

        Native.HIDP_VALUE_CAPS[] GetValueCaps(int reportType, ushort count)
        {
            if (count == 0) return new Native.HIDP_VALUE_CAPS[0];
            var caps = new Native.HIDP_VALUE_CAPS[count];
            ushort n = count;
            if (Native.HidP_GetValueCaps(reportType, caps, ref n, _pp) != Native.HIDP_STATUS_SUCCESS) return new Native.HIDP_VALUE_CAPS[0];
            if (n < caps.Length) Array.Resize(ref caps, n);
            return caps;
        }

        static uint ToRaw(int value, int bits) { return (uint)value & ((1u << bits) - 1); }

        int FromRaw(uint raw, int bits)
        {
            // Sign-extend only when the descriptor declares a signed range.
            if (LogicalMin < 0 && (raw & (1u << (bits - 1))) != 0) return (int)(raw | ~((1u << bits) - 1));
            return (int)raw;
        }

        static uint ExtractBits(byte[] data, int bitOffset, int bitCount)
        {
            uint v = 0;
            for (int i = 0; i < bitCount; i++)
            {
                int bit = bitOffset + i;
                if (((data[bit >> 3] >> (bit & 7)) & 1) != 0) v |= 1u << i;
            }
            return v;
        }

        byte[] GetFeatureReport(byte reportId)
        {
            if (!IsOpen || _featureLength < 1) return null;
            // A Get Feature request only needs the report ID (0 when the descriptor has none) in byte 0; the device fills
            // the rest. HidP_InitializeReportForID is not used here because it reports HIDP_STATUS_REPORT_DOES_NOT_EXIST
            // for feature reports whose only field is declared Constant, as the Surface's initial-level report is.
            var buf = new byte[_featureLength];
            buf[0] = reportId;
            if (!Native.HidD_GetFeature(_h, buf, buf.Length)) return null;
            return buf;
        }

        void ReadSuggestions()
        {
            if (!_hasSuggestions) return;
            var report = GetFeatureReport(_suggestionReportId);
            if (report == null) return;
            var raw = new List<int>();
            if (_suggestionCount == 1)
            {
                uint v;
                if (Native.HidP_GetUsageValue(Native.HidP_Feature, UsagePageConsumer, 0, UsageLevelSuggestion, out v, _pp, report, (uint)report.Length) == Native.HIDP_STATUS_SUCCESS)
                    raw.Add(FromRaw(v, _suggestionBits));
            }
            else
            {
                int bytes = (_suggestionCount * _suggestionBits + 7) / 8;
                if (bytes > ushort.MaxValue) return;
                var values = new byte[bytes];
                if (Native.HidP_GetUsageValueArray(Native.HidP_Feature, UsagePageConsumer, 0, UsageLevelSuggestion, values, (ushort)bytes, _pp, report, (uint)report.Length) != Native.HIDP_STATUS_SUCCESS) return;
                for (int i = 0; i < _suggestionCount; i++) raw.Add(FromRaw(ExtractBits(values, i * _suggestionBits, _suggestionBits), _suggestionBits));
            }
            var list = new List<int>();
            foreach (int v in raw) if (v >= LogicalMin && v <= LogicalMax && !list.Contains(v)) list.Add(v);
            list.Sort();
            Suggestions = list.ToArray();
        }

        /// <summary>Level the keyboard reports as its initial/last level, if it implements that feature report.</summary>
        public int? ReadDeviceLevel()
        {
            if (!_hasInitialLevel) return null;
            var report = GetFeatureReport(_initialLevelReportId);
            if (report == null) return null;
            uint v;
            if (Native.HidP_GetUsageValue(Native.HidP_Feature, UsagePageConsumer, 0, UsageSetLevel, out v, _pp, report, (uint)report.Length) != Native.HIDP_STATUS_SUCCESS) return null;
            int level = FromRaw(v, _initialLevelBits);
            if (level < LogicalMin || level > LogicalMax) return null;
            return level;
        }

        /// <summary>Brightness Windows last applied to this device (HKLM Lighting state, readable by standard users).</summary>
        public int? ReadWindowsLevel()
        {
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Lighting\Backlight\State\" + StateKeyName))
                {
                    if (k == null) return null;
                    object v = k.GetValue("ManualBrightnessNits");
                    if (v is int) return (int)v;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Registry key name Windows uses to persist this device's manual brightness.</summary>
        string StateKeyName
        {
            get
            {
                var p = Path;
                if (p.StartsWith(@"\\?\")) p = p.Substring(4);
                return p.Replace('\\', '#');
            }
        }

        /// <summary>Encodes a Set Level output report with the HID parser. Null, with a reason, if the descriptor rejects it.</summary>
        public byte[] BuildSetLevelReport(int level, out string error)
        {
            error = "";
            if (_pp == IntPtr.Zero) { error = "no descriptor data"; return null; }
            if (level < LogicalMin) level = LogicalMin;
            if (level > LogicalMax) level = LogicalMax;
            var buf = new byte[_outputLength];
            int st = Native.HidP_InitializeReportForID(Native.HidP_Output, _setLevelReportId, _pp, buf, (uint)buf.Length);
            if (st == Native.HIDP_STATUS_SUCCESS)
                st = Native.HidP_SetUsageValue(Native.HidP_Output, UsagePageConsumer, 0, UsageSetLevel, ToRaw(level, _setLevelBits), _pp, buf, (uint)buf.Length);
            if (st != Native.HIDP_STATUS_SUCCESS) { error = "HID encoding status 0x" + st.ToString("X8"); return null; }
            return buf;
        }

        /// <summary>
        /// Sends the Set Level output report. The Surface HID mini-driver implements only the write path (WriteFile);
        /// HidD_SetOutputReport returns ERROR_NOT_SUPPORTED there, so it is kept only as a fallback for other keyboards.
        /// </summary>
        public bool SetLevel(int level, out string error)
        {
            if (!IsOpen) { error = "device not open"; return false; }
            var buf = BuildSetLevelReport(level, out error);
            if (buf == null) return false;
            LastReportHex = BitConverter.ToString(buf);
            uint written;
            if (Native.WriteFile(_h, buf, (uint)buf.Length, out written, IntPtr.Zero))
            {
                if (written == buf.Length) return true;
                error = "short write (" + written + " of " + buf.Length + " bytes)";
            }
            else error = "WriteFile error " + Marshal.GetLastWin32Error();
            if (Native.HidD_SetOutputReport(_h, buf, buf.Length)) { error = ""; return true; }
            error += "; HidD_SetOutputReport error " + Marshal.GetLastWin32Error();
            return false;
        }

        public void Dispose()
        {
            if (_h != null && !_h.IsClosed) _h.Close();
            if (_pp != IntPtr.Zero) { Native.HidD_FreePreparsedData(_pp); _pp = IntPtr.Zero; }
        }
    }

    // ------------------------------------------------------------------ Device thread
    /// <summary>What the app remembers about one keyboard between refreshes. Device thread only.</summary>
    sealed class DeviceState
    {
        public int LastWindowsLevel = -1;   // last ManualBrightnessNits seen, to notice the key being pressed to "off"
        public bool RespectKeyOff;          // the user turned this keyboard off with its key while the app was running
        public int LastKnownLevel = -1;
        public int ConsecutiveFailures;
        public int LastSentLevel = -1;      // 0 = off via the key
        public string LastError = "";
        public DateTime LastSend = DateTime.MinValue;
        public string LastLogged = ""; public DateTime LastLogTime = DateTime.MinValue;
    }

    /// <summary>
    /// Owns every HID handle; all device I/O happens on this one background thread so a stalled driver can never
    /// block the UI. The UI submits a job per refresh and receives a report back.
    /// </summary>
    sealed class DeviceWorker
    {
        public sealed class Job
        {
            public int FixedLevel = -1;
            public bool DipAndRestore;
            public int IntervalSeconds = 10;
            public bool WriteLevels;        // false = only (re)detect devices, e.g. while paused
        }

        public sealed class Report
        {
            public DeviceInfo[] Devices = new DeviceInfo[0];
            public int Lit, OffByKey, Failing;
            public int Level = -1;          // level of the first lit keyboard
            public DateTime LastSend = DateTime.MinValue;
            public string Error = "";
        }

        readonly Thread _thread;
        readonly AutoResetEvent _kick = new AutoResetEvent(false);
        readonly object _jobLock = new object();
        readonly Action<Report> _onReport;
        readonly List<BacklightDevice> _devices = new List<BacklightDevice>();
        readonly Dictionary<string, DeviceState> _states = new Dictionary<string, DeviceState>(StringComparer.OrdinalIgnoreCase);
        Job _job;
        volatile bool _stop, _rescanRequested = true, _resetKeyOff;
        volatile uint _nativeThreadId;
        long _busySinceTicks;               // UTC ticks while a pass runs, 0 when idle

        public DeviceWorker(Action<Report> onReport)
        {
            _onReport = onReport;
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "Backlight device I/O";
            _thread.Start();
        }

        public void Submit(Job job) { lock (_jobLock) _job = job; _kick.Set(); }
        public void RequestRescan() { _rescanRequested = true; }
        public void ResetKeyOff() { _resetKeyOff = true; }

        /// <summary>How long the current pass has been running; zero when idle.</summary>
        public TimeSpan BusyFor
        {
            get { long t = Interlocked.Read(ref _busySinceTicks); return t == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(DateTime.UtcNow.Ticks - t); }
        }

        /// <summary>Cancels a synchronous HID call stuck in a driver. The call then fails and the device is re-detected.</summary>
        public bool CancelStuckIo()
        {
            uint tid = _nativeThreadId;
            if (tid == 0) return false;
            IntPtr h = Native.OpenThread(Native.THREAD_TERMINATE, false, tid);
            if (h == IntPtr.Zero) return false;
            try { return Native.CancelSynchronousIo(h); } finally { Native.CloseHandle(h); }
        }

        public void Stop() { _stop = true; _kick.Set(); _thread.Join(1500); }

        void Loop()
        {
            Thread.BeginThreadAffinity();   // CancelStuckIo targets this exact OS thread
            _nativeThreadId = Native.GetCurrentThreadId();
            while (true)
            {
                _kick.WaitOne();
                if (_stop) break;
                Job job;
                lock (_jobLock) job = _job;
                if (job == null) continue;
                Report report = null;
                Interlocked.Exchange(ref _busySinceTicks, DateTime.UtcNow.Ticks);
                try { report = Pass(job); }
                catch (Exception ex) { Logger.Crash("Device thread error", ex); _rescanRequested = true; }
                finally { Interlocked.Exchange(ref _busySinceTicks, 0); }
                if (report != null && !_stop) _onReport(report);
            }
            foreach (var d in _devices) d.Dispose();
            _devices.Clear();
            Thread.EndThreadAffinity();
        }

        DeviceState StateFor(BacklightDevice d)
        {
            DeviceState s;
            if (!_states.TryGetValue(d.Path, out s)) { s = new DeviceState(); _states[d.Path] = s; }
            return s;
        }

        Report Pass(Job job)
        {
            bool failing = false;
            foreach (var d in _devices) if (StateFor(d).ConsecutiveFailures >= 3) failing = true;
            if (_rescanRequested || _devices.Count == 0 || failing) Rescan();
            if (_resetKeyOff) { _resetKeyOff = false; foreach (var s in _states.Values) s.RespectKeyOff = false; }

            var r = new Report();
            r.Devices = _devices.ConvertAll(d => d.Snapshot()).ToArray();
            foreach (var d in _devices)
            {
                var st = StateFor(d);
                if (job.WriteLevels) Write(d, st, job);
                if (st.LastError.Length > 0)
                {
                    r.Failing++;
                    if (r.Error.Length == 0) r.Error = _devices.Count > 1 ? st.LastError + " on " + d.Name : st.LastError;
                }
                else if (st.LastSentLevel == 0) r.OffByKey++;
                else if (st.LastSentLevel > 0)
                {
                    r.Lit++;
                    if (r.Level < 0) r.Level = st.LastSentLevel;
                    if (st.LastSend > r.LastSend) r.LastSend = st.LastSend;
                }
            }
            return r;
        }

        void Rescan()
        {
            _rescanRequested = false;
            foreach (var d in _devices) d.Dispose();
            _devices.Clear();
            _devices.AddRange(BacklightDevice.FindAll(Logger.Write));
            if (_devices.Count == 0) Logger.Write("No HID keyboard-backlight collection found. Is this a Surface with a backlit keyboard on Windows 11 25H2 (build 26200.7922+)?");
            foreach (var d in _devices)
            {
                var st = StateFor(d);
                st.ConsecutiveFailures = 0; st.LastError = ""; st.LastLogged = "";
                if (st.LastKnownLevel <= 0) { int? lvl = d.ReadDeviceLevel(); if (lvl.HasValue && lvl.Value > 0) st.LastKnownLevel = lvl.Value; }
            }
        }

        static void Write(BacklightDevice d, DeviceState st, Job job)
        {
            string source;
            int level = ResolveTargetLevel(d, st, job, out source);
            if (level <= 0) { st.LastSentLevel = 0; st.LastError = ""; return; }   // turned off with the key: respect it
            string error, how;
            bool ok;
            if (job.DipAndRestore)
            {
                int dip = level > d.LogicalMin + 1 ? level - 1 : Math.Min(level + 1, d.LogicalMax);
                ok = d.SetLevel(dip, out error);
                if (ok) { Thread.Sleep(20); ok = d.SetLevel(level, out error); }
                how = "dip via " + dip;
            }
            else { ok = d.SetLevel(level, out error); how = "resend"; }

            if (ok)
            {
                st.LastSend = DateTime.Now; st.LastSentLevel = level; st.LastError = ""; st.ConsecutiveFailures = 0;
                if (Logger.Enabled)
                {
                    string line = "Sending level " + level + " (" + source + ", " + how + ", report " + d.LastReportHex + ") to " + d.Name;
                    if (line != st.LastLogged || (DateTime.Now - st.LastLogTime).TotalMinutes >= 10)
                    {
                        Logger.Write(line + " every " + job.IntervalSeconds + " s");
                        st.LastLogged = line; st.LastLogTime = DateTime.Now;
                    }
                }
            }
            else
            {
                st.ConsecutiveFailures++; st.LastError = "write failed, " + error; st.LastLogged = "";
                Logger.Write("Set Level failed (" + error + ") on " + d.Name + "; failures=" + st.ConsecutiveFailures);
            }
        }

        static int ResolveTargetLevel(BacklightDevice d, DeviceState st, Job job, out string source)
        {
            if (job.FixedLevel >= 0) { source = "fixed"; return Math.Min(job.FixedLevel, d.LogicalMax); }
            int? w = d.ReadWindowsLevel();
            if (w.HasValue)
            {
                if (w.Value > 0)
                {
                    st.LastKnownLevel = w.Value; st.LastWindowsLevel = w.Value; st.RespectKeyOff = false;
                    source = "Windows setting"; return w.Value;
                }
                // Windows stores "off" for this keyboard. If that changed while the app was running, the user pressed the
                // backlight key to turn it off: respect that. Otherwise (app start, re-enable) "enabled" means "on".
                if (st.LastWindowsLevel > 0) st.RespectKeyOff = true;
                st.LastWindowsLevel = 0;
                if (st.RespectKeyOff) { source = "off (backlight key)"; return 0; }
                source = "last used (Windows has it off)";
                return LastKnownOrDefault(d, st);
            }
            int? dl = d.ReadDeviceLevel();
            if (dl.HasValue && dl.Value > 0) { source = "device"; st.LastKnownLevel = dl.Value; return dl.Value; }
            source = "last used";
            return LastKnownOrDefault(d, st);
        }

        static int LastKnownOrDefault(BacklightDevice d, DeviceState st)
        {
            if (st.LastKnownLevel > 0) return st.LastKnownLevel;
            var nonZero = new List<int>();
            foreach (int s in d.Suggestions) if (s > 0) nonZero.Add(s);
            if (nonZero.Count > 0) return nonZero[nonZero.Count / 2];   // middle preset (6 nits on the Surface Laptop Studio 2)
            return d.LogicalMax;
        }
    }

    // ------------------------------------------------------------------ Settings
    sealed class Settings
    {
        const string KeyPath = @"Software\OrclSKBK";
        const string LegacyKeyPath = @"Software\SurfaceBacklightKeeper";   // name used up to 1.2.0
        public bool Enabled = true;
        public int IntervalSeconds = 10;
        public int FixedLevel = -1;            // -1 = follow Windows setting
        public bool DipAndRestore = false;     // fallback keep-alive method
        public bool PauseWhenDisplayOff = true;
        public bool PauseWhenLocked = true;
        public bool PauseOnBattery = false;
        public bool Logging = false;
        public bool FirstRun;                  // no settings key existed when the app started

        /// <summary>Copies settings saved under the pre-1.3 name the first time OrclSKBK runs, so an upgrade keeps them.</summary>
        static void MigrateLegacySettings()
        {
            try
            {
                using (var existing = Registry.CurrentUser.OpenSubKey(KeyPath)) { if (existing != null) return; }
                using (var old = Registry.CurrentUser.OpenSubKey(LegacyKeyPath))
                {
                    if (old == null) return;
                    using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
                        foreach (string name in old.GetValueNames()) k.SetValue(name, old.GetValue(name), old.GetValueKind(name));
                }
            }
            catch { }
        }

        public static Settings Load()
        {
            MigrateLegacySettings();
            var s = new Settings();
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (k == null) { s.FirstRun = true; return s; }
                    s.Enabled = ReadInt(k, "Enabled", 1) != 0;
                    // The firmware timeout was observed to be under 25 s, so anything above 15 s risks a gap.
                    s.IntervalSeconds = Math.Max(3, Math.Min(15, ReadInt(k, "IntervalSeconds", 10)));
                    s.FixedLevel = ReadInt(k, "FixedLevel", -1);
                    s.DipAndRestore = ReadInt(k, "DipAndRestore", 0) != 0;
                    s.PauseWhenDisplayOff = ReadInt(k, "PauseWhenDisplayOff", 1) != 0;
                    s.PauseWhenLocked = ReadInt(k, "PauseWhenLocked", 1) != 0;
                    s.PauseOnBattery = ReadInt(k, "PauseOnBattery", 0) != 0;
                    s.Logging = ReadInt(k, "Logging", 0) != 0;
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    k.SetValue("Enabled", Enabled ? 1 : 0);
                    k.SetValue("IntervalSeconds", IntervalSeconds);
                    k.SetValue("FixedLevel", FixedLevel);
                    k.SetValue("DipAndRestore", DipAndRestore ? 1 : 0);
                    k.SetValue("PauseWhenDisplayOff", PauseWhenDisplayOff ? 1 : 0);
                    k.SetValue("PauseWhenLocked", PauseWhenLocked ? 1 : 0);
                    k.SetValue("PauseOnBattery", PauseOnBattery ? 1 : 0);
                    k.SetValue("Logging", Logging ? 1 : 0);
                }
            }
            catch { }
        }

        static int ReadInt(RegistryKey k, string name, int def)
        {
            object v = k.GetValue(name); if (v is int) return (int)v; return def;
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "OrclSKBK";
        const string LegacyRunName = "SurfaceBacklightKeeper";   // name used up to 1.2.0
        public static bool IsStartWithWindows()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) { return k != null && k.GetValue(RunName) != null; } } catch { return false; }
        }
        public static void SetStartWithWindows(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunName, false);
                    k.DeleteValue(LegacyRunName, false);   // never start the old version alongside this one
                }
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ Tray application (UI thread)
    sealed class KeeperForm : Form
    {
        const double StallSeconds = 3;

        readonly Settings _s;
        readonly NotifyIcon _tray;
        readonly System.Windows.Forms.Timer _timer, _clickTimer;
        readonly DeviceWorker _worker;
        readonly Icon _iconOn, _iconOff;
        DeviceWorker.Report _report;           // latest result from the device thread
        string _deviceKey;                     // device list the Brightness menu was built for
        IntPtr _powerNotify = IntPtr.Zero;
        bool _displayOff, _locked, _suspended, _stalled;
        ToolStripMenuItem _miEnabled, _miLevel, _miInterval, _miMethodResend, _miMethodDip, _miPauseDisplay, _miPauseLock, _miPauseBattery, _miStartup, _miLogging, _miStatus;

        public KeeperForm()
        {
            _s = Settings.Load();
            Logger.Enabled = _s.Logging;
            Text = AppInfo.DisplayName; ShowInTaskbar = false; WindowState = FormWindowState.Minimized; Opacity = 0; FormBorderStyle = FormBorderStyle.FixedToolWindow;
            CreateHandle();

            _iconOn = MakeIcon(Color.FromArgb(255, 214, 92), true);
            _iconOff = MakeIcon(Color.FromArgb(140, 140, 140), false);
            _tray = new NotifyIcon(); _tray.Icon = _iconOn; _tray.Visible = true; _tray.Text = AppInfo.DisplayName;
            _tray.ContextMenuStrip = BuildMenu();
            // Left click opens the menu, double-click toggles. The menu waits for the double-click interval so the
            // two can be told apart.
            _clickTimer = new System.Windows.Forms.Timer(); _clickTimer.Interval = Math.Max(150, SystemInformation.DoubleClickTime);
            _clickTimer.Tick += delegate { _clickTimer.Stop(); ShowTrayMenu(); };
            _tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) _clickTimer.Start(); };
            _tray.MouseDoubleClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _clickTimer.Stop(); ToggleEnabled(); } };

            _timer = new System.Windows.Forms.Timer(); _timer.Interval = _s.IntervalSeconds * 1000; _timer.Tick += delegate { OnTimer(); };

            // Registration delivers the current display state immediately, then every change.
            Guid g = Native.GUID_CONSOLE_DISPLAY_STATE;
            _powerNotify = Native.RegisterPowerSettingNotification(Handle, ref g, Native.DEVICE_NOTIFY_WINDOW_HANDLE);
            if (_powerNotify == IntPtr.Zero)
                Logger.Write("Could not register for display on/off notifications (error " + Marshal.GetLastWin32Error() + "); 'Pause when display is off' will not take effect.");
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            bool? locked = Native.IsSessionLocked();
            _locked = locked.HasValue && locked.Value;

            Logger.Write("---- " + AppInfo.DisplayName + " starting (interval " + _s.IntervalSeconds + " s, level " +
                (_s.FixedLevel < 0 ? "follow Windows" : _s.FixedLevel.ToString()) + ", method " + (_s.DipAndRestore ? "dip-and-restore" : "resend") +
                ", session " + (locked.HasValue ? (locked.Value ? "locked" : "unlocked") : "lock state unknown") + ")");

            _worker = new DeviceWorker(OnWorkerReport);
            RefreshMenu();
            _timer.Start();
            RequestPass(true);   // first pass detects the keyboard, and writes unless paused
        }

        protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); }

        // ---------------- talking to the device thread
        /// <summary>
        /// Hands the device thread a pass with the current settings. While paused nothing is written; a forced pass
        /// then only (re)detects keyboards so the menu stays accurate.
        /// </summary>
        void RequestPass(bool force)
        {
            string why;
            bool paused = Paused(out why);
            if (!paused || force)
            {
                var job = new DeviceWorker.Job();
                job.FixedLevel = _s.FixedLevel; job.DipAndRestore = _s.DipAndRestore; job.IntervalSeconds = _s.IntervalSeconds;
                job.WriteLevels = !paused;
                _worker.Submit(job);
            }
            UpdateTray();
        }

        void OnWorkerReport(DeviceWorker.Report r)   // device thread
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action<DeviceWorker.Report>(ApplyReport), r); }
            catch (InvalidOperationException) { }    // window already gone during exit
        }

        void ApplyReport(DeviceWorker.Report r)
        {
            if (IsDisposed) return;
            bool first = _report == null;
            _report = r;
            _stalled = false;
            string key = string.Join("|", Array.ConvertAll(r.Devices, d => d.Path));
            if (key != _deviceKey) { _deviceKey = key; RefreshMenu(); } else UpdateTray();
            if (!first) return;
            if (r.Devices.Length == 0)
                _tray.ShowBalloonTip(8000, AppInfo.DisplayName, "No keyboard backlight device was found. This needs a Surface with a backlit keyboard on Windows 11 25H2 or later.", ToolTipIcon.Warning);
            else if (_s.FirstRun)
            {
                _s.FirstRun = false; _s.Save();
                _tray.ShowBalloonTip(6000, AppInfo.DisplayName + " is running", "It keeps the keyboard backlight on once it is lit. Touch the trackpad or a key to light it. Click the tray icon for options.", ToolTipIcon.Info);
            }
        }

        void OnTimer()
        {
            bool? locked = Native.IsSessionLocked();
            if (locked.HasValue) _locked = locked.Value;   // self-correcting, in case a session event was missed
            if (_worker.BusyFor.TotalSeconds >= StallSeconds) { OnStall(); return; }
            RequestPass(false);
        }

        void OnStall()
        {
            if (!_stalled)
            {
                _stalled = true;
                Logger.Write("Keyboard backlight device has not responded for " + (int)_worker.BusyFor.TotalSeconds + " s; cancelling the stuck call and re-detecting.");
                _worker.RequestRescan();
            }
            _worker.CancelStuckIo();
            UpdateTray();
        }

        /// <summary>True while "OrclSKBK.exe --test" is cycling the levels, so the tray app does not interfere.</summary>
        static bool SelfTestRunning()
        {
            EventWaitHandle ev;
            if (!EventWaitHandle.TryOpenExisting(Program.SelfTestEventName, out ev)) return false;
            using (ev) return ev.WaitOne(0);
        }

        bool Paused(out string why)
        {
            why = null;
            if (!_s.Enabled) { why = "disabled"; return true; }
            if (SelfTestRunning()) { why = "self test running"; return true; }
            if (_suspended) { why = "system suspended"; return true; }
            if (_s.PauseWhenDisplayOff && _displayOff) { why = "display off"; return true; }
            if (_s.PauseWhenLocked && _locked) { why = "session locked"; return true; }
            if (_s.PauseOnBattery && SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline) { why = "on battery"; return true; }
            return false;
        }

        // ---------------- system events
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_POWERBROADCAST && (int)m.WParam.ToInt64() == Native.PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
            {
                var ps = (Native.POWERBROADCAST_SETTING)Marshal.PtrToStructure(m.LParam, typeof(Native.POWERBROADCAST_SETTING));
                if (ps.PowerSetting == Native.GUID_CONSOLE_DISPLAY_STATE)
                {
                    bool wasOff = _displayOff;
                    _displayOff = ps.Data == 0;
                    Logger.Write("Display state -> " + (ps.Data == 0 ? "off" : ps.Data == 2 ? "dimmed" : "on"));
                    // The embedded controller relights the keyboard when the display comes back; re-arm right away.
                    if (wasOff && !_displayOff && _worker != null) RequestPass(false); else UpdateTray();
                }
            }
            base.WndProc(ref m);
        }

        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (InvokeRequired) { BeginInvoke(new SessionSwitchEventHandler(OnSessionSwitch), sender, e); return; }
            if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.ConsoleDisconnect || e.Reason == SessionSwitchReason.RemoteDisconnect)
            { _locked = true; UpdateTray(); }
            else if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.ConsoleConnect || e.Reason == SessionSwitchReason.RemoteConnect)
            { _locked = false; RequestPass(false); }
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (InvokeRequired) { BeginInvoke(new PowerModeChangedEventHandler(OnPowerModeChanged), sender, e); return; }
            if (e.Mode == PowerModes.Resume)
            {
                _suspended = false;
                // Give the keyboard a moment to come back, then re-open it.
                var t = new System.Windows.Forms.Timer(); t.Interval = 4000;
                t.Tick += delegate { t.Stop(); t.Dispose(); _worker.RequestRescan(); RequestPass(false); };
                t.Start();
            }
            else if (e.Mode == PowerModes.Suspend) { _suspended = true; UpdateTray(); }
            else if (e.Mode == PowerModes.StatusChange) RequestPass(false);   // AC/battery change may start or end the battery pause
        }

        // ---------------- tray UI
        ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip();
            _miStatus = new ToolStripMenuItem("Status"); _miStatus.Enabled = false; m.Items.Add(_miStatus);
            m.Items.Add(new ToolStripSeparator());
            _miEnabled = new ToolStripMenuItem("Keep keyboard backlight on", null, delegate { ToggleEnabled(); }); m.Items.Add(_miEnabled);
            _miLevel = new ToolStripMenuItem("Brightness"); m.Items.Add(_miLevel);
            _miInterval = new ToolStripMenuItem("Refresh every"); m.Items.Add(_miInterval);
            foreach (int sec in new[] { 5, 10, 15 })
            {
                int s = sec;
                _miInterval.DropDownItems.Add(new ToolStripMenuItem(s + " seconds", null, delegate { _s.IntervalSeconds = s; _timer.Interval = s * 1000; _s.Save(); RefreshMenu(); RequestPass(false); }));
            }
            var method = new ToolStripMenuItem("Keep-alive method"); m.Items.Add(method);
            _miMethodResend = new ToolStripMenuItem("Re-send the current level (default, no flicker)", null, delegate { _s.DipAndRestore = false; _s.Save(); RefreshMenu(); });
            _miMethodDip = new ToolStripMenuItem("Tiny dip and restore on every refresh (only if the light still times out)", null, delegate { _s.DipAndRestore = true; _s.Save(); RefreshMenu(); });
            method.DropDownItems.Add(_miMethodResend); method.DropDownItems.Add(_miMethodDip);
            var pause = new ToolStripMenuItem("Pause when"); m.Items.Add(pause);
            _miPauseDisplay = new ToolStripMenuItem("Display is off", null, delegate { _s.PauseWhenDisplayOff = !_s.PauseWhenDisplayOff; _s.Save(); RefreshMenu(); RequestPass(false); });
            _miPauseLock = new ToolStripMenuItem("Screen is locked", null, delegate { _s.PauseWhenLocked = !_s.PauseWhenLocked; _s.Save(); RefreshMenu(); RequestPass(false); });
            _miPauseBattery = new ToolStripMenuItem("Running on battery", null, delegate { _s.PauseOnBattery = !_s.PauseOnBattery; _s.Save(); RefreshMenu(); RequestPass(false); });
            pause.DropDownItems.Add(_miPauseDisplay); pause.DropDownItems.Add(_miPauseLock); pause.DropDownItems.Add(_miPauseBattery);
            m.Items.Add(new ToolStripSeparator());
            _miStartup = new ToolStripMenuItem("Start with Windows", null, delegate { Settings.SetStartWithWindows(!Settings.IsStartWithWindows()); RefreshMenu(); }); m.Items.Add(_miStartup);
            _miLogging = new ToolStripMenuItem("Write log file", null, delegate
            {
                _s.Logging = !_s.Logging; _s.Save(); Logger.Enabled = _s.Logging;
                if (_s.Logging) Logger.Write("Logging enabled (" + AppInfo.DisplayName + ")");
                RefreshMenu();
            });
            m.Items.Add(_miLogging);
            m.Items.Add(new ToolStripMenuItem("Open log folder", null, delegate { try { Directory.CreateDirectory(Logger.Dir); Process.Start("explorer.exe", Logger.Dir); } catch { } }));
            m.Items.Add(new ToolStripMenuItem("Re-detect keyboard", null, delegate { _worker.RequestRescan(); RequestPass(true); }));
            m.Items.Add(new ToolStripSeparator());
            var about = new ToolStripMenuItem("About"); m.Items.Add(about);
            foreach (string line in new[] {
                AppInfo.Name,
                AppInfo.LongName,
                "Version " + AppInfo.Version,
                "Made by Claude, prompted by Oracooll",
                "Open source (MIT): github.com/Oracooll/OrclSKBK",
                "-",
                "Keeps the Surface keyboard backlight from switching off after",
                "~30 s without typing. Every few seconds it re-sends the brightness",
                "level through the same HID Keyboard Backlight interface Windows 11",
                "uses, which re-arms the keyboard firmware's idle timer.",
                "-",
                "The firmware only lights the keyboard on a key press or trackpad",
                "touch, so touch the trackpad once; the app keeps it on from there.",
                "The keyboard's backlight key still changes the level or turns it off." })
            {
                if (line == "-") { about.DropDownItems.Add(new ToolStripSeparator()); continue; }
                var li = new ToolStripMenuItem(line); li.Enabled = false; about.DropDownItems.Add(li);
            }
            m.Items.Add(new ToolStripMenuItem("Exit", null, delegate { Close(); }));
            m.Opening += delegate { RefreshMenu(); };
            return m;
        }

        /// <summary>Opens the tray menu exactly as a right-click would (same position, closes on focus loss).</summary>
        void ShowTrayMenu()
        {
            RefreshMenu();
            var mi = typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (mi != null) { try { mi.Invoke(_tray, null); return; } catch { } }
            _tray.ContextMenuStrip.Show(Cursor.Position);
        }

        void ToggleEnabled()
        {
            _s.Enabled = !_s.Enabled; _s.Save();
            if (_s.Enabled) _worker.ResetKeyOff();   // enabling means "on", even if the key had turned a keyboard off earlier
            RefreshMenu();
            RequestPass(false);
        }

        void RefreshMenu()
        {
            _miEnabled.Checked = _s.Enabled;
            foreach (ToolStripMenuItem mi in _miInterval.DropDownItems) mi.Checked = mi.Text.StartsWith(_s.IntervalSeconds + " ");
            _miMethodResend.Checked = !_s.DipAndRestore; _miMethodDip.Checked = _s.DipAndRestore;
            _miPauseDisplay.Checked = _s.PauseWhenDisplayOff; _miPauseLock.Checked = _s.PauseWhenLocked; _miPauseBattery.Checked = _s.PauseOnBattery;
            _miStartup.Checked = Settings.IsStartWithWindows(); _miLogging.Checked = _s.Logging;

            _miLevel.DropDownItems.Clear();
            var follow = new ToolStripMenuItem("Follow Windows setting (use the keyboard's backlight key)", null, delegate { _s.FixedLevel = -1; _s.Save(); RefreshMenu(); RequestPass(false); });
            follow.Checked = _s.FixedLevel < 0; _miLevel.DropDownItems.Add(follow);
            var d = _report != null && _report.Devices.Length > 0 ? _report.Devices[0] : null;
            if (d != null)
            {
                var levels = new List<int>(d.Suggestions);
                if (levels.Count == 0) { for (int l = d.LogicalMin; l <= d.LogicalMax; l++) levels.Add(l); }
                if (!levels.Contains(d.LogicalMax)) levels.Add(d.LogicalMax);
                int idx = 0;
                foreach (int lv in levels)
                {
                    if (lv <= 0) continue;
                    int l = lv; idx++;
                    var mi = new ToolStripMenuItem("Always level " + idx + "  (" + l + " nits)", null, delegate { _s.FixedLevel = l; _s.Save(); RefreshMenu(); RequestPass(false); });
                    mi.Checked = _s.FixedLevel == l; _miLevel.DropDownItems.Add(mi);
                }
            }
            UpdateTray();
        }

        void UpdateTray()
        {
            string why;
            bool paused = Paused(out why);
            var r = _report;
            string status;
            bool dim = false;
            if (r == null) status = "Starting...";
            else if (r.Devices.Length == 0) { status = "No keyboard backlight device found"; dim = true; }
            else if (_stalled) { status = "Keyboard not responding - retrying"; dim = true; }
            else if (paused) { status = "Paused: " + why; dim = true; }
            else if (r.Failing > 0) { status = "Error: " + r.Error; dim = true; }
            else if (r.Lit == 0 && r.OffByKey > 0) status = "Off via the backlight key - press it again to turn it back on";
            else if (r.Lit > 0)
                status = (r.Devices.Length > 1 ? "Keeping " + r.Lit + " of " + r.Devices.Length + " keyboards on" : "Keeping backlight on at " + r.Level + " nits")
                    + " (last sent " + r.LastSend.ToString("HH:mm:ss") + ")";
            else status = "Starting...";
            _miStatus.Text = status;
            string tip = "OrclSKBK: " + status;
            if (tip.Length > 63) tip = tip.Substring(0, 60) + "...";   // NotifyIcon.Text max is 63 chars on .NET Framework
            _tray.Text = tip;
            _tray.Icon = dim ? _iconOff : _iconOn;
        }

        static Icon MakeIcon(Color glow, bool lit)
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
                    if (lit) using (var b = new SolidBrush(Color.FromArgb(90, glow))) g.FillEllipse(b, 1, 1, 30, 30);
                    using (var b = new SolidBrush(Color.FromArgb(50, 50, 50))) g.FillRectangle(b, 3, 10, 26, 14);
                    using (var p = new Pen(glow, 1.5f)) g.DrawRectangle(p, 3, 10, 26, 14);
                    using (var b = new SolidBrush(glow))
                    {
                        for (int r = 0; r < 2; r++) for (int c = 0; c < 6; c++) g.FillRectangle(b, 6 + c * 4, 13 + r * 4, 2, 2);
                        g.FillRectangle(b, 8, 21, 16, 2);
                    }
                }
                IntPtr h = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); } finally { Native.DestroyIcon(h); }
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop(); _clickTimer.Stop();
            SystemEvents.SessionSwitch -= OnSessionSwitch; SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            if (_powerNotify != IntPtr.Zero) Native.UnregisterPowerSettingNotification(_powerNotify);
            _worker.Stop();
            _tray.Visible = false; _tray.Dispose();
            base.OnFormClosed(e);
        }
    }

    static class Program
    {
        public const string SelfTestEventName = "Local\\OrclSKBK.SelfTest";

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Logger.Crash("UI thread exception", e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { Logger.Crash("Unhandled exception", e.ExceptionObject as Exception); };
            if (args.Length > 0 && (args[0] == "--test" || args[0] == "/test")) { RunSelfTest(); return; }

            bool created;
            using (var mutex = new Mutex(true, "Local\\OrclSKBK.SingleInstance", out created))
            {
                if (!created) return;
                try { Application.Run(new KeeperForm()); }
                catch (Exception ex) { Logger.Crash("Fatal", ex); throw; }
            }
        }

        /// <summary>Visibly steps the backlight through its levels so the user can confirm the app controls it.</summary>
        static void RunSelfTest()
        {
            var sb = new StringBuilder();
            var devices = BacklightDevice.FindAll(delegate(string s) { sb.AppendLine(s); });
            if (devices.Count == 0)
            {
                MessageBox.Show("No HID keyboard-backlight collection was found on this PC.\r\n\r\n" + sb, AppInfo.DisplayName + " - self test", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var d = devices[0];
            for (int i = 1; i < devices.Count; i++) devices[i].Dispose();
            int? restore = d.ReadWindowsLevel();
            if (!restore.HasValue) restore = d.ReadDeviceLevel();
            // Never write level 0: the firmware would switch the light off and only a key press or trackpad touch brings it back.
            var levels = new List<int>();
            foreach (int s in d.Suggestions) if (s > 0) levels.Add(s);
            if (levels.Count < 2) { levels.Clear(); levels.Add(Math.Max(1, d.LogicalMin)); levels.Add((d.LogicalMin + d.LogicalMax) / 2); levels.Add(d.LogicalMax); }
            var seq = new List<int>(levels); levels.Reverse(); seq.AddRange(levels);   // up then down
            var log = new StringBuilder();
            int failures = 0;
            string err;
            using (var pauseTray = new EventWaitHandle(true, EventResetMode.ManualReset, SelfTestEventName))
            {
                foreach (int l in seq)
                {
                    bool ok = d.SetLevel(l, out err);
                    log.AppendLine("Set level " + l + " nits (report " + d.LastReportHex + ") -> " + (ok ? "OK" : "FAILED: " + err));
                    if (!ok) failures++;
                    Thread.Sleep(900);
                }
                if (restore.HasValue && restore.Value > 0) d.SetLevel(restore.Value, out err);
                pauseTray.Reset();
            }
            d.Dispose();
            MessageBox.Show("Device: " + d.Name + " (VID " + d.Vid.ToString("X4") + " PID " + d.Pid.ToString("X4") + ")\r\n" +
                "Levels reported by the keyboard: " + string.Join(", ", Array.ConvertAll(d.Suggestions, delegate(int x) { return x.ToString(); })) + " nits\r\n\r\n" +
                log + "\r\n" + (failures == 0 ? "If you saw the keyboard light step up and back down, the app can control the backlight.\r\n(The light must already be on: touch the trackpad, then run the test.)" : failures + " write(s) failed.") +
                (restore.HasValue && restore.Value > 0 ? "\r\nRestored Windows' level: " + restore.Value + " nits." : ""),
                AppInfo.DisplayName + " - self test", MessageBoxButtons.OK, failures == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
    }
}
