using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace TBMini
{
    /// Signals when values under an HKLM key change (RegNotifyChangeKeyValue, thread-agnostic). No polling.
    public sealed class RegWatcher : IDisposable
    {
        [DllImport("advapi32.dll")]
        static extern int RegNotifyChangeKeyValue(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, bool subtree, uint filter, SafeWaitHandle ev, bool async);
        const uint REG_NOTIFY_CHANGE_LAST_SET = 0x4, REG_NOTIFY_THREAD_AGNOSTIC = 0x10000000;

        readonly RegistryKey _key;
        readonly AutoResetEvent _ev = new AutoResetEvent(false);
        readonly RegisteredWaitHandle _wait;
        readonly Action _changed;
        volatile bool _disposed;

        RegWatcher(RegistryKey key, Action changed)
        {
            _key = key; _changed = changed;
            Arm();
            _wait = ThreadPool.RegisterWaitForSingleObject(_ev, (s, t) =>
            {
                if (_disposed) return;
                Arm();
                try { _changed(); } catch { }
            }, null, Timeout.Infinite, false);
        }

        void Arm() => RegNotifyChangeKeyValue(_key.Handle, false, REG_NOTIFY_CHANGE_LAST_SET | REG_NOTIFY_THREAD_AGNOSTIC, _ev.SafeWaitHandle, true);

        public static RegWatcher TryCreate(string hklmPath, Action changed)
        {
            try
            {
                var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(hklmPath);
                return k == null ? null : new RegWatcher(k, changed);
            }
            catch { return null; }
        }

        public void Dispose()
        {
            _disposed = true;
            _wait.Unregister(null);
            _key.Dispose();
            _ev.Dispose();
        }
    }

    // =============================================================================================
    // \\.\EnergyDrv (AcpiVpc.sys, Lenovo IdeaPad/ThinkBook ACPI VPC driver). Same IOCTLs and inputs as
    // Lenovo's PowerBattery.dll / IdeaNotebookAgent and ThinkBook Toolkit. No admin needed.
    // ONLY the documented inputs below are ever sent.
    // =============================================================================================
    public static class EnergyDrv
    {
        public const uint IOCTL_BATTERY = 0x831020F8;   // GBMD (in 0xFF) / SBMC (3,5,7,8)
        public const uint IOCTL_HALS = 0x831020E8;      // status (in 2), USB 0x0A/0x0B/0x12/0x13, FnLock 0x0E/0x0F
        public const uint IOCTL_NIGHT = 0x83102150;     // BNMC
        public const uint IOCTL_KBLC = 0x83102144;      // keyboard backlight level
        public const uint IOCTL_KBLR = 0x83102154;      // backlight "remember"
        public const uint IOCTL_KBLA = 0x83102158;      // backlight auto-off 30 s

        static readonly object Gate = new object();

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFileW(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref uint inBuf, int inLen, out uint outBuf, int outLen, out int ret, IntPtr ov);

        public static uint Call(uint code, uint input)
        {
            lock (Gate)
            {
                using var h = CreateFileW(@"\\.\EnergyDrv", 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
                if (h.IsInvalid) throw new NotSupportedException("打不开 EnergyDrv：" + new Win32Exception().Message);
                if (!DeviceIoControl(h, code, ref input, 4, out uint o, 4, out _, IntPtr.Zero))
                    throw new Win32Exception();
                Thread.Sleep(20);                       // Lenovo spaces EC calls out
                return o;
            }
        }

        /// Sends a sequence of commands, 50 ms apart.
        public static void Send(uint code, params uint[] inputs)
        {
            foreach (var i in inputs) { Call(code, i); Thread.Sleep(50); }
        }

        public static bool Poll(Func<bool> ok, int tries = 10, int ms = 50)
        {
            for (int i = 0; i < tries; i++) { if (ok()) return true; Thread.Sleep(ms); }
            return ok();
        }
    }

    // =============================================================================================
    // LENOVO_BIOS_ASSISTANT (FunctionID 1 = Fn/Ctrl swap). Admin.
    // =============================================================================================
    public static class BiosAssistant
    {
        static ManagementObject First()
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM LENOVO_BIOS_ASSISTANT");
            foreach (ManagementObject o in s.Get()) return o;
            throw new NotSupportedException("LENOVO_BIOS_ASSISTANT 不存在");
        }

        public static uint Capability()
        {
            using var o = First();
            return Convert.ToUInt32(o.InvokeMethod("GetCapabilityValue", null, null)["Data"]);
        }

        public static uint Get(uint index)
        {
            using var o = First();
            var p = o.GetMethodParameters("GetValue"); p["IndexData"] = index;
            return Convert.ToUInt32(o.InvokeMethod("GetValue", p, null)["Data"]);
        }

        public static uint Set(uint index, uint value)
        {
            using var o = First();
            var p = o.GetMethodParameters("SetValue"); p["IndexData"] = index; p["ValueData"] = value;
            return Convert.ToUInt32(o.InvokeMethod("SetValue", p, null)["ReturnData"]);
        }
    }

    // =============================================================================================
    // Lenovo Fn & function keys service: Caps/Num lock OSD (control codes 146-149). No admin.
    // =============================================================================================
    public static class LockOsd
    {
        const string Svc = "LenovoFnAndFunctionKeys";
        const string Key = @"SYSTEM\CurrentControlSet\Services\LenovoFnAndFunctionKeys\VantageToast";

        public static int Read(string value)
        {
            using (var sc = new ServiceController(Svc))
                if (sc.Status != ServiceControllerStatus.Running) throw new NotSupportedException(Svc + " 没运行");
            using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(Key);
            if (k?.GetValue(value) is not int v) throw new NotSupportedException(value);
            return v == 1 ? 1 : 0;
        }

        public static void Write(string value, int on, int codeOn, int codeOff)
        {
            using (var sc = new ServiceController(Svc)) sc.ExecuteCommand(on == 1 ? codeOn : codeOff);
            if (!EnergyDrv.Poll(() => Read(value) == on, 10, 200)) throw new InvalidOperationException("服务没有应用这个设置");
        }
    }

    // =============================================================================================
    // Precision touchpad on/off via SPI_SET/GETTOUCHPADPARAMETERS (Win11 24H2+), Ctrl+Win+F24 fallback.
    // External mice are enumerated through Raw Input (USB "HID#VID_…" and Bluetooth "HID#{…" devices).
    // =============================================================================================
    public static class Touchpad
    {
        static readonly object Gate = new object();

        [StructLayout(LayoutKind.Sequential)]
        struct TOUCHPAD_PARAMETERS
        {
            public uint versionNumber, maxSupportedContacts, legacyTouchpadFeatures, flags1, flags2,
                sensitivityLevel, cursorSpeed, feedbackIntensity, clickForceSensitivity, rightClickZoneWidth, rightClickZoneHeight;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SystemParametersInfoW(uint action, uint uiParam, ref TOUCHPAD_PARAMETERS p, uint winIni);

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public KEYBDINPUT ki; public ulong pad; }
        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);

        [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }
        [DllImport("user32.dll", SetLastError = true)] static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[] list, ref uint num, uint size);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern uint GetRawInputDeviceInfoW(IntPtr h, uint cmd, StringBuilder data, ref uint size);

        static TOUCHPAD_PARAMETERS Get()
        {
            var p = new TOUCHPAD_PARAMETERS { versionNumber = 1 };
            if (!SystemParametersInfoW(0xAE, 44, ref p, 0)) throw new NotSupportedException("SPI_GETTOUCHPADPARAMETERS 失败");
            return p;
        }

        public static bool Present { get { try { return (Get().flags1 & 1) != 0; } catch { return false; } } }
        public static bool Enabled => (Get().flags1 & 8) != 0;

        /// Device paths of external (USB / Bluetooth) mice currently attached. Internal I2C devices are not counted.
        public static string[] ExternalMice()
        {
            uint n = 0, size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
            GetRawInputDeviceList(null, ref n, size);
            if (n == 0) return new string[0];
            var list = new RAWINPUTDEVICELIST[n];
            if (GetRawInputDeviceList(list, ref n, size) == uint.MaxValue) return new string[0];
            var mice = new List<string>();
            foreach (var d in list.Take((int)n))
            {
                if (d.dwType != 0 /*RIM_TYPEMOUSE*/) continue;
                uint len = 0;
                GetRawInputDeviceInfoW(d.hDevice, 0x20000007 /*RIDI_DEVICENAME*/, null, ref len);
                if (len == 0) continue;
                var sb = new StringBuilder((int)len + 1);
                if (GetRawInputDeviceInfoW(d.hDevice, 0x20000007, sb, ref len) == uint.MaxValue) continue;
                string name = sb.ToString();
                if (name.IndexOf("RDP_MOU", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (name.IndexOf("HID#VID_", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("HID#{", StringComparison.OrdinalIgnoreCase) >= 0)
                    mice.Add(name.ToUpperInvariant());
            }
            return mice.Distinct().ToArray();
        }

        public static int Read()
        {
            var p = Get();
            if ((p.flags1 & 1) == 0) throw new NotSupportedException("没有精确式触摸板");
            return (p.flags1 & 8) != 0 ? 1 : 0;
        }

        public static void Write(int on)
        {
            if (on != 0 && on != 1) throw new ArgumentOutOfRangeException(nameof(on));
            lock (Gate)
            {
                var p = Get();
                if (on == 0 && ExternalMice().Length == 0)
                    throw new InvalidOperationException("没有检测到外接鼠标。为防止无法操作，不能关闭触摸板。");
                if (((p.flags1 & 8) != 0) == (on == 1)) return;
                p.flags1 = on == 1 ? p.flags1 | 8u : p.flags1 & ~8u;
                SystemParametersInfoW(0xAF, 44, ref p, 3 /*SPIF_UPDATEINIFILE|SPIF_SENDCHANGE*/);
                if (EnergyDrv.Poll(() => Read() == on, 4, 100)) return;

                // fallback: the Windows PTP toggle hotkey Ctrl+Win+F24 (what Lenovo sends). Re-check right before
                // the blind toggle so a late SPI apply cannot make us flip it back.
                if (Read() == on) return;
                var keys = new ushort[] { 0x11, 0x5B, 0x87 };
                var inputs = keys.Select(k => Key(k, false)).Concat(keys.Reverse().Select(k => Key(k, true))).ToArray();
                SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
                if (!EnergyDrv.Poll(() => Read() == on, 20, 100)) throw new InvalidOperationException("触摸板状态没有改变");
            }
        }

        static INPUT Key(ushort vk, bool up) =>
            new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? 2u : 0u } };
    }

    /// Lock-out guard: if TBMini turned the touchpad off, turn it back on as soon as any of the external mice that
    /// were present at that moment disappears (or none is left). State persists in HKCU across restarts.
    public static class TouchpadGuard
    {
        const string Key = @"Software\TBMini";

        static int _armed = -1;   // cached: -1 unknown, 0 no, 1 yes (avoids registry reads on every event)

        public static bool Armed
        {
            get
            {
                if (_armed < 0)
                    using (var k = Registry.CurrentUser.OpenSubKey(Key))
                        _armed = k?.GetValue("TouchpadGuard") is string[] a && a.Length > 0 ? 1 : 0;
                return _armed == 1;
            }
        }

        public static void Arm(string[] mice)
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            k.SetValue("TouchpadGuard", mice.Length > 0 ? mice : new[] { "?" }, RegistryValueKind.MultiString);
            _armed = 1;
        }

        public static void Disarm()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Key, true)) k?.DeleteValue("TouchpadGuard", false);
            _armed = 0;
        }

        /// Called at startup, resume and every few seconds while armed. Also rescues a touchpad that is off with no
        /// external mouse at startup, whoever turned it off.
        public static void Check(bool startup = false)
        {
            try
            {
                if (!Touchpad.Present) return;
                if (Touchpad.Enabled) { if (Armed) Disarm(); return; }
                var now = Touchpad.ExternalMice();
                string[] recorded;
                using (var k = Registry.CurrentUser.OpenSubKey(Key)) recorded = k?.GetValue("TouchpadGuard") as string[];
                bool lost = now.Length == 0 || (recorded != null && recorded.Any(r => r != "?" && !now.Contains(r)));
                if ((recorded != null || startup) && lost) { Touchpad.Write(1); Disarm(); }
            }
            catch { }
        }
    }

    // =============================================================================================
    // Color presets: per-unit X-Rite factory ICC profiles TPLCD_<panel4>_<suffix>.icm, set as the user-scope
    // default profile of the internal panel's monitor devnode (what Lenovo's XriteSettingUser64 does).
    // =============================================================================================
    public static class ColorPreset
    {
        public static readonly string[] Labels = { "默认(D65)", "鲜艳(Native)", "网页办公(sRGB)", "摄影/影院(P3-D65)", "原生影院(P3-DCI)", "摄影印刷(Adobe RGB)", "HDTV(REC709)" };
        public static readonly string[] Suffix = { "Default", "Native", "sRGB", "DisplayP3", "DCIP3", "AdobeRGB", "REC709" };
        static readonly int[] LenovoValue = { 4, 7, 1, 3, 9, 2, 8 };
        static readonly object Gate = new object();

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevicesW(string dev, uint i, ref DISPLAY_DEVICE dd, uint flags);
        [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool WcsSetUsePerUserProfiles(string dev, uint cls, bool use);
        [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool WcsAssociateColorProfileWithDevice(int scope, string profile, string dev);
        [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool WcsSetDefaultColorProfile(int scope, string dev, int cpt, int cpst, uint id, string profile);
        [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool WcsGetDefaultColorProfileSize(int scope, string dev, int cpt, int cpst, uint id, out uint size);
        [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool WcsGetDefaultColorProfile(int scope, string dev, int cpt, int cpst, uint id, uint size, StringBuilder name);

        const int SCOPE_USER = 1, CPT_ICC = 0, CPST_RGB_WORKING_SPACE = 5;
        const uint CLASS_MONITOR = 0x6D6E7472;   // 'mntr'
        static string ColorDir => Path.Combine(Environment.SystemDirectory, @"spool\drivers\color");

        /// Internal panel PnP model token (DISPLAY\<model>\...) from the monitor connection type.
        public static string PanelModel()
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT InstanceName, VideoOutputTechnology FROM WmiMonitorConnectionParams");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    uint vot = Convert.ToUInt32(o["VideoOutputTechnology"]);
                    if (vot != 11 && vot != 0x80000000 && vot != 6 && vot != 13) continue;
                    var parts = ((o["InstanceName"] as string) ?? "").Split('\\');
                    if (parts.Length >= 2) return parts[1];
                }
            return null;
        }

        /// Active monitor DeviceID of the internal panel, e.g. MONITOR\<model>\{4d36e96e-...}\0001
        public static string MonitorDeviceId(string model)
        {
            var ad = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            for (uint i = 0; EnumDisplayDevicesW(null, i, ref ad, 0); i++)
            {
                var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                for (uint j = 0; EnumDisplayDevicesW(ad.DeviceName, j, ref mon, 0); j++)
                {
                    if ((mon.StateFlags & 1) != 0 && mon.DeviceID != null &&
                        mon.DeviceID.IndexOf("\\" + model + "\\", StringComparison.OrdinalIgnoreCase) >= 0)
                        return mon.DeviceID;
                    mon.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
                }
                ad.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
            }
            return null;
        }

        sealed class Ctx { public string Model, Dev; public string File(int i) => $"TPLCD_{Model.Substring(Model.Length - 4)}_{Suffix[i]}.icm"; }

        static Ctx Context()
        {
            string model = PanelModel() ?? throw new NotSupportedException("内屏未点亮");
            string dev = MonitorDeviceId(model) ?? throw new NotSupportedException("找不到内屏的显示器设备");
            var c = new Ctx { Model = model, Dev = dev };
            if (!System.IO.File.Exists(Path.Combine(ColorDir, c.File(0)))) throw new NotSupportedException("没有出厂校色文件");
            return c;
        }

        static int ReadOn(Ctx c)
        {
            if (!WcsGetDefaultColorProfileSize(SCOPE_USER, c.Dev, CPT_ICC, CPST_RGB_WORKING_SPACE, 0, out uint size) || size == 0) return -1;
            var sb = new StringBuilder((int)size);
            if (!WcsGetDefaultColorProfile(SCOPE_USER, c.Dev, CPT_ICC, CPST_RGB_WORKING_SPACE, 0, size, sb)) return -1;
            var m = Regex.Match(sb.ToString(), @"^TPLCD.*?_([A-Za-z0-9]+)\.icm$", RegexOptions.IgnoreCase);
            return m.Success ? Array.FindIndex(Suffix, x => x.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) : -1;
        }

        public static int Read() { lock (Gate) return ReadOn(Context()); }

        public static void Write(int i)
        {
            if (i < 0 || i >= Suffix.Length) throw new ArgumentOutOfRangeException(nameof(i));
            lock (Gate) WriteOn(Context(), i);
        }

        static void WriteOn(Ctx c, int i)
        {
            string icm = c.File(i);
            if (!System.IO.File.Exists(Path.Combine(ColorDir, icm))) throw new InvalidOperationException("缺少校色文件 " + icm);
            WcsSetUsePerUserProfiles(c.Dev, CLASS_MONITOR, true);
            WcsAssociateColorProfileWithDevice(SCOPE_USER, icm, c.Dev);
            if (!WcsSetDefaultColorProfile(SCOPE_USER, c.Dev, CPT_ICC, CPST_RGB_WORKING_SPACE, 0, icm))
                throw new Win32Exception();
            if (ReadOn(c) != i) throw new InvalidOperationException("写入后读回不一致");
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\TBMini"))
            {
                k.SetValue("ColorPreset", Suffix[i]);
                k.SetValue("ColorPresetDev", c.Dev);
            }
            try   // keep Lenovo's UI consistent if it is still installed
            {
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Lenovo\SmartDisplayAddin\Color"))
                { k.SetValue("user_current_scenery", LenovoValue[i], RegistryValueKind.DWord); k.SetValue("firstsetbyuser", 1, RegistryValueKind.DWord); }
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Lenovo\SmartEngine\modules\F2003"))
                    k.SetValue("color_type", LenovoValue[i].ToString());
            }
            catch { }
        }

        /// After a GPU mux change the panel is a different monitor devnode with no (or another) association.
        /// Re-apply the user's preset there — but never override a profile chosen elsewhere on the devnode we
        /// already manage.
        public static void Reapply()
        {
            lock (Gate)
            {
                string s, dev;
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\TBMini"))
                {
                    s = k?.GetValue("ColorPreset") as string;
                    dev = k?.GetValue("ColorPresetDev") as string;
                }
                if (s == null) return;
                int i = Array.FindIndex(Suffix, x => x.Equals(s, StringComparison.OrdinalIgnoreCase));
                if (i < 0) return;
                Ctx c;
                try { c = Context(); } catch { return; }
                if (string.Equals(dev, c.Dev, StringComparison.OrdinalIgnoreCase)) return;
                if (ReadOn(c) != i) WriteOn(c, i);
                else using (var k = Registry.CurrentUser.CreateSubKey(@"Software\TBMini")) k.SetValue("ColorPresetDev", c.Dev);
            }
        }
    }

    // =============================================================================================
    // Dolby DAX3 (HSA) through Lenovo's DolbyRpcClient.dll (x64, static CRT, ncalrpc:[DaxRpcEndpoint]). No admin.
    // Loaded by full path from an admin-protected folder; functions resolved with GetProcAddress.
    // =============================================================================================
    public static class Dolby
    {
        public static readonly string[] Labels = { "动态", "电影", "音乐", "游戏", "语音", "定制", "关闭" };
        static readonly int[] Profile = { 5, 0, 1, 2, 3, 4, -1 };
        static readonly string[] RuntimeName = { "dynamic", "movie", "music", "game", "voice", "personalize", null };
        const string Dll = "DolbyRpcClient.dll";
        static readonly object Gate = new object();
        static bool _inited;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryExW(string p, IntPtr h, uint f);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr h, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnInt();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)][return: MarshalAs(UnmanagedType.I1)] delegate bool FnBool();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)][return: MarshalAs(UnmanagedType.I1)] delegate bool FnSetBool([MarshalAs(UnmanagedType.I1)] bool on);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)][return: MarshalAs(UnmanagedType.I1)] delegate bool FnSetInt(int v);

        static FnInt _initialize, _getProfile;
        static FnBool _getEnabled, _alive;
        static FnSetBool _setEnabled;
        static FnSetInt _setProfile;

        public static string BaiyingDll => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Lenovo\baiying\" + Dll);
        static string RuntimeXml => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Dolby\DAX3\Runtime.xml");

        /// Only load from admin-protected folders (we usually run elevated).
        static string Find()
        {
            foreach (var p in new[] { BaiyingDll, Path.Combine(Path.GetDirectoryName(TrayApp.InstalledExe), Dll) })
                if (File.Exists(p)) return p;
            return null;
        }

        static T Fn<T>(IntPtr h, string name) where T : Delegate
        {
            IntPtr p = GetProcAddress(h, name);
            if (p == IntPtr.Zero) throw new NotSupportedException(Dll + " 缺少 " + name);
            return Marshal.GetDelegateForFunctionPointer<T>(p);
        }

        static void Ensure()
        {
            if (_initialize == null)
            {
                string p = Find() ?? throw new NotSupportedException("找不到 " + Dll);
                IntPtr h = LoadLibraryExW(p, IntPtr.Zero, 0x8);
                if (h == IntPtr.Zero) throw new NotSupportedException(new Win32Exception().Message);
                _getEnabled = Fn<FnBool>(h, "DolbyClient_GetDolbyEnabled");
                _getProfile = Fn<FnInt>(h, "DolbyClient_GetActiveProfile");
                _alive = Fn<FnBool>(h, "DolbyClient_IsSpatialAudioSupported");
                _setEnabled = Fn<FnSetBool>(h, "DolbyClient_SetDolbyEnabled");
                _setProfile = Fn<FnSetInt>(h, "DolbyClient_SetActiveProfile");
                _initialize = Fn<FnInt>(h, "DolbyClient_Initialize");
            }
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!_inited)
                {
                    int rc = _initialize();
                    if (rc != 0) throw new NotSupportedException("Dolby 服务连接失败 " + rc);
                    _inited = true;
                }
                if (_alive()) return;   // liveness probe
                _inited = false;
            }
            throw new NotSupportedException("Dolby 服务无响应");
        }

        /// The user's main profile from Runtime.xml (unaffected by the temporary "voice" switch during calls).
        static int MainProfileIndex()
        {
            try
            {
                var doc = new XmlDocument();
                doc.Load(RuntimeXml);
                string v = (doc.SelectSingleNode("/RuntimeConfig/SelectedMainProfile/@value") as XmlAttribute)?.Value;
                return Array.FindIndex(RuntimeName, n => n != null && n.Equals(v, StringComparison.OrdinalIgnoreCase));
            }
            catch { return -1; }
        }

        public static int Read()
        {
            lock (Gate)
            {
                Ensure();
                if (!_getEnabled()) return 6;
                int idx = Array.IndexOf(Profile, _getProfile());
                if (idx == 4)                    // voice: may be Dolby's capture-stream auto switch, report the main choice
                {
                    int main = MainProfileIndex();
                    if (main >= 0) idx = main;
                }
                return idx;
            }
        }

        public static void Write(int i)
        {
            if (i < 0 || i >= Profile.Length) throw new ArgumentOutOfRangeException(nameof(i));
            lock (Gate)
            {
                Ensure();
                if (i == 6) _setEnabled(false);
                else
                {
                    if (!_getEnabled()) { _setEnabled(true); Thread.Sleep(75); }
                    _setProfile(Profile[i]);
                }
                for (int t = 0; t < 8; t++)
                {
                    bool en = _getEnabled();
                    if (i == 6 ? !en : en && (_getProfile() == Profile[i] || MainProfileIndex() == i)) return;
                    Thread.Sleep(150);
                }
                _inited = false;
                throw new InvalidOperationException("Dolby 没有切换成功");
            }
        }
    }

    // =============================================================================================
    // Mic noise suppression (Elevoc via Lenovo SENoiseCancellingPlugin64.dll). The plugin is a non-unloadable
    // singleton, so it runs in a short-lived child process: TBMini.exe --nc get|set N
    // =============================================================================================
    public static class MicNoise
    {
        public static readonly string[] BaseLabels = { "关", "多人", "单人", "空间" };
        public static readonly int[] BaseModes = { 0, 1, 2, 3 };

        public static string AddinDir()
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Lenovo\Vantage\Addins\SmartNoiseCancelledAddin");
            if (!Directory.Exists(root)) return null;
            return Directory.GetDirectories(root)
                .Where(d => File.Exists(Path.Combine(d, "SENoiseCancellingPlugin64.dll")))
                .OrderByDescending(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version(0, 0))
                .FirstOrDefault();
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WINTRUST_FILE_INFO { public uint cbStruct; public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

        [StructLayout(LayoutKind.Sequential)]
        struct WINTRUST_DATA
        {
            public uint cbStruct; public IntPtr pPolicyCallbackData, pSIPClientData; public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
            public IntPtr pFile; public uint dwStateAction; public IntPtr hWVTStateData, pwszURLReference; public uint dwProvFlags, dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

        /// The plugin must carry a valid Authenticode signature (file digest verified by WinVerifyTrust) whose signer
        /// organisation is Lenovo — we may be running elevated.
        static bool SignedByLenovo(string file)
        {
            var fi = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = file };
            IntPtr pFi = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            try
            {
                Marshal.StructureToPtr(fi, pFi, false);
                var guid = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");          // WINTRUST_ACTION_GENERIC_VERIFY_V2
                var wd = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice = 2,              // WTD_UI_NONE
                    fdwRevocationChecks = 0,     // WTD_REVOKE_NONE (offline-safe)
                    dwUnionChoice = 1,           // WTD_CHOICE_FILE
                    pFile = pFi,
                    dwStateAction = 1,           // WTD_STATEACTION_VERIFY
                    dwProvFlags = 0x40,          // WTD_CACHE_ONLY_URL_RETRIEVAL
                };
                int rc = WinVerifyTrust(IntPtr.Zero, ref guid, ref wd);
                wd.dwStateAction = 2;            // WTD_STATEACTION_CLOSE
                WinVerifyTrust(IntPtr.Zero, ref guid, ref wd);
                if (rc != 0) return false;

                using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
                string org = cert.GetNameInfo(X509NameType.SimpleName, false) ?? "";
                return cert.Subject.Split(',').Select(p => p.Trim())
                    .Any(p => p.StartsWith("O=Lenovo", StringComparison.OrdinalIgnoreCase)) || org.StartsWith("Lenovo", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(pFi); }
        }

        /// Runs the helper and returns the plugin's JSON.
        public static string Helper(string args)
        {
            var psi = new ProcessStartInfo(TrayApp.ElevationTarget(), "--nc " + args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 };
            using var p = Process.Start(psi);
            var task = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } throw new System.TimeoutException("降噪插件无响应"); }
            if (!task.Wait(3000)) throw new System.TimeoutException("降噪插件无输出");
            string o = task.Result.Trim();
            if (o.StartsWith("ERR")) throw new NotSupportedException(o.Substring(3).Trim());
            return o;
        }

        public static int Field(string json, string name)
        {
            var m = Regex.Match(json, "\"" + name + "\"\\s*:\\s*\"?(-?\\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }

        // ---- helper-process side ----
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SeCb(IntPtr module, IntPtr action, IntPtr data, IntPtr resp, int respLen);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] delegate int SeInit(string module, SeCb cb);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] delegate int SeAction(string action, string data, out IntPtr resp);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SeDelete(IntPtr p);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryExW(string p, IntPtr h, uint f);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetDllDirectoryW(string p);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr h, string name);
        static SeCb _keep;
        static Timer _watchdog;

        static string U8(IntPtr p)
        {
            if (p == IntPtr.Zero) return "";
            int n = 0; while (Marshal.ReadByte(p, n) != 0) n++;
            var b = new byte[n]; Marshal.Copy(p, b, 0, n);
            return Encoding.UTF8.GetString(b);
        }

        /// Entry point of the helper process. Prints one line and kills itself (the plugin cannot unload).
        public static void HelperMain(string[] args)
        {
            _watchdog = new Timer(_ => Process.GetCurrentProcess().Kill(), null, 15000, Timeout.Infinite);
            string output;
            try
            {
                string dir = AddinDir() ?? throw new Exception("没有安装联想降噪插件");
                string dll = Path.Combine(dir, "SENoiseCancellingPlugin64.dll");
                if (!SignedByLenovo(dll)) throw new Exception("降噪插件签名校验失败");
                Environment.CurrentDirectory = dir;
                SetDllDirectoryW(dir);
                IntPtr h = LoadLibraryExW(dll, IntPtr.Zero, 0x8);
                if (h == IntPtr.Zero) throw new Exception("加载插件失败 " + Marshal.GetLastWin32Error());
                var init = Marshal.GetDelegateForFunctionPointer<SeInit>(GetProcAddress(h, "se_plugin_init"));
                var act = Marshal.GetDelegateForFunctionPointer<SeAction>(GetProcAddress(h, "se_plugin_action"));
                var del = Marshal.GetDelegateForFunctionPointer<SeDelete>(GetProcAddress(h, "se_plugin_delete"));
                _keep = (m, a, d, r, l) => 0;
                int rc = init("F4006", _keep);
                if (rc != 0) throw new Exception("插件初始化失败 " + rc);

                string Do(string action, string data)
                {
                    act(action, data ?? "", out IntPtr p);
                    string s = U8(p);
                    if (p != IntPtr.Zero) del(p);
                    return s;
                }

                if (args.Length >= 3 && args[1] == "set")
                {
                    int mode = int.Parse(args[2]);
                    if (Array.IndexOf(new[] { 0, 1, 2, 3, 4, 10 }, mode) < 0) throw new Exception("无效模式");
                    Do("set_microphone_mode", "{\"microphone_mode\":" + mode + "}");
                }
                output = Do("get_nc_status", "").Replace("\r", "").Replace("\n", "");
            }
            catch (Exception e) { output = "ERR " + e.Message; }
            var stdout = Console.OpenStandardOutput();
            var bytes = Encoding.UTF8.GetBytes(output + "\n");
            stdout.Write(bytes, 0, bytes.Length);
            stdout.Flush();
            Process.GetCurrentProcess().Kill();
        }
    }
}
