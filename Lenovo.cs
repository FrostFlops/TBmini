using System;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using static TBMini.L10n;

namespace TBMini
{
    // ---------------------------------------------------------------------------------------------
    // Performance mode = Lenovo ITS via the "Lenovo Dispatcher" service (LenovoProcessManagement).
    // Same call Lenovo's own PowerBattery.dll (IdeaNotebookAddin, used by 联想百应) makes:
    //   ControlService(LenovoProcessManagement, 0xA3 智能 / 0xA4 省电 / 0xA5 性能 / 0xAC 极客)
    // State: HKLM\...\LenovoProcessManagement\Performance\PowerSlider  ITS_CurrentSettingV (0/1/3/4)
    // The service DACL grants Interactive Users user-defined control, so no admin is needed.
    // ---------------------------------------------------------------------------------------------
    public enum PowerMode { Unknown = -1, Auto = 0, Saver = 1, Performance = 3, Geek = 4 }

    public static class Its
    {
        public const string ServiceName = "LenovoProcessManagement";
        const string SliderKey = @"SYSTEM\CurrentControlSet\Services\LenovoProcessManagement\Performance\PowerSlider";
        public static string SliderKeyPath => SliderKey;

        static int ReadInt(RegistryKey k, string name, int def = -1) => k?.GetValue(name) is int i ? i : def;

        static RegistryKey Open() =>
            RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(SliderKey);

        public static bool Supported
        {
            get { using var k = Open(); return k != null && ReadInt(k, "Version", 0) >= 0x2000; }
        }

        public static PowerMode Read()
        {
            using var k = Open();
            if (k == null) return PowerMode.Unknown;
            bool v2 = (ReadInt(k, "ITS_FN_Capability", 0) & 0x10) != 0;   // 0x10: Geek-aware -> ITS_CurrentSettingV
            int v = ReadInt(k, v2 ? "ITS_CurrentSettingV" : "ITS_CurrentSetting");
            if (v < 0) v = ReadInt(k, v2 ? "ITS_CurrentSetting" : "ITS_CurrentSettingV");
            return v switch
            {
                0 => PowerMode.Auto,
                1 => PowerMode.Saver,
                3 => PowerMode.Performance,
                4 => PowerMode.Geek,
                _ => PowerMode.Unknown,
            };
        }

        public static bool GeekSupported
        {
            get { using var k = Open(); return (ReadInt(k, "ITS_FN_Capability", 0) & 0x10) != 0; }
        }

        /// Dispatcher greys Geek out (bit 0x400 of ITS_SE_VantageSetting) when not on AC.
        public static bool GeekGreyed
        {
            get { using var k = Open(); int v = ReadInt(k, "ITS_SE_VantageSetting", 0); return ((v >> 10) & 1) == 1; }
        }

        public static bool OnAC => SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;

        public static bool GeekAvailable => GeekSupported && OnAC && !GeekGreyed;

        public static string Name(PowerMode m) => m switch
        {
            PowerMode.Auto => T("智能", "Auto"),
            PowerMode.Saver => T("省电", "Saver"),
            PowerMode.Performance => T("性能", "Performance"),
            PowerMode.Geek => T("极客", "Geek"),
            _ => T("未知", "Unknown"),
        };

        public static void Set(PowerMode m)
        {
            int code = m switch
            {
                PowerMode.Auto => 0xA3,
                PowerMode.Saver => 0xA4,
                PowerMode.Performance => 0xA5,
                PowerMode.Geek => 0xAC,
                _ => throw new ArgumentOutOfRangeException(nameof(m)),
            };
            if (m == PowerMode.Geek && !GeekAvailable) throw new InvalidOperationException(T("极客模式只能在接电源时使用", "Geek mode is only available on AC power"));

            using (var sc = new ServiceController(ServiceName))
            {
                if (sc.Status != ServiceControllerStatus.Running)
                    throw new InvalidOperationException(T("联想服务 LenovoProcessManagement 没有运行", "The Lenovo service LenovoProcessManagement is not running"));
                sc.ExecuteCommand(code);
            }
            for (int i = 0; i < 15; i++)
            {
                Thread.Sleep(200);
                if (Read() == m) return;
            }
            throw new System.TimeoutException(T($"命令已发送，但模式没有变成「{Name(m)}」（现在是「{Name(Read())}」）", $"Command sent, but the mode did not change to {Name(m)} (it is {Name(Read())})"));
        }
    }

    // ---------------------------------------------------------------------------------------------
    // GPU mode = BIOS setting GraphicsDevice (Lenovo_SetBiosSetting/Lenovo_SaveBiosSettings, reboot)
    //          + inside "Switchable Graphics": LENOVO_GAMEZONE_DATA.SetIGPUModeStatus (live, no reboot).
    // Same sequence as Lenovo's LenovoProductivitySystemAddin (WmiAgent.SetAdvancedGPUMode / SetGPUMode).
    // The RUNNING mux state comes from hardware (which GPU the internal panel hangs off), the STAGED one
    // from BIOS; they differ only while a change waits for a reboot. root\WMI needs admin.
    // ---------------------------------------------------------------------------------------------
    public enum GpuMode { Unknown, Hybrid, IgpuOnly, Auto, Discrete, Uma }

    public sealed class GpuSnapshot
    {
        public string Staged;        // BIOS GraphicsDevice right now (applies at next boot)
        public bool? PanelOnDgpu;    // hardware truth; null = internal panel not active (lid closed …)
        public string Running;       // BiosDiscrete / BiosSwitchable / BiosUma that the current boot runs
        public int Igpu = -1;        // 0/1/2 when running Switchable
        public GpuMode Mode = GpuMode.Unknown;
        public string Error;

        public bool RunningKnown => PanelOnDgpu != null;
        public bool PendingReboot => RunningKnown && Staged != null && Running != null && Gpu.Family(Staged) != Running;
    }

    public static class Gpu
    {
        const string NS = @"root\WMI";
        public const string BiosDiscrete = "Discrete Graphics";
        public const string BiosSwitchable = "Switchable Graphics";
        public const string BiosUma = "UMA Graphics";

        static ManagementObject First(string cls)
        {
            using var s = new ManagementObjectSearcher(NS, "SELECT * FROM " + cls);
            foreach (ManagementObject o in s.Get()) return o;
            return null;
        }

        static ManagementBaseObject Call(string cls, string method, Action<ManagementBaseObject> fill = null)
        {
            using var o = First(cls) ?? throw new NotSupportedException(cls + T(" 不存在", " not found"));
            ManagementBaseObject inp = null;
            if (fill != null) { inp = o.GetMethodParameters(method); fill(inp); }
            return o.InvokeMethod(method, inp, null);
        }

        internal static bool Eq(string a, string b) => string.Equals(a?.Trim(), b, StringComparison.OrdinalIgnoreCase);

        /// Normalises a BIOS value to BiosDiscrete / BiosUma / BiosSwitchable (null stays null).
        public static string Family(string bios) =>
            bios == null ? null
            : Eq(bios, BiosDiscrete) ? BiosDiscrete
            : Eq(bios, BiosUma) || Eq(bios, "Integrated Graphics") ? BiosUma
            : BiosSwitchable;

        /// e.g. "Discrete Graphics" / "Switchable Graphics" / "UMA Graphics", null if not found
        public static string BiosGraphics() => Bios.Get("GraphicsDevice");

        public static string[] BiosGraphicsSelections() => Bios.Selections("GraphicsDevice");

        public static bool IgpuSupported()
        {
            try { return Convert.ToInt32(Call("LENOVO_GAMEZONE_DATA", "IsSupportIGPUMode")["Data"]) > 0; }
            catch { return false; }
        }

        /// 0 = hybrid, 1 = iGPU only, 2 = auto
        public static int IgpuStatus() => Convert.ToInt32(Call("LENOVO_GAMEZONE_DATA", "GetIGPUModeStatus")["Data"]);

        public static string Name(GpuMode g) => g switch
        {
            GpuMode.Hybrid => T("混合", "Hybrid"),
            GpuMode.IgpuOnly => T("集显", "iGPU only"),
            GpuMode.Auto => T("自动", "Auto"),
            GpuMode.Discrete => T("独显直连", "dGPU direct"),
            GpuMode.Uma => T("纯集显(BIOS)", "UMA (BIOS)"),
            _ => T("未知", "Unknown"),
        };

        public static GpuMode SubMode(int igpu) => igpu switch
        {
            0 => GpuMode.Hybrid, 1 => GpuMode.IgpuOnly, 2 => GpuMode.Auto, _ => GpuMode.Unknown,
        };

        // ---- hardware truth: which adapter drives the internal (eDP) panel ----
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Locate_DevNodeW(out uint dn, string id, int flags);
        [DllImport("cfgmgr32.dll")] static extern int CM_Get_Parent(out uint parent, uint dn, int flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_IDW(uint dn, char[] buf, int len, int flags);

        /// true = panel on NVIDIA (dGPU direct running), false = on Intel, null = no active internal panel / unknown.
        /// Works without admin.
        public static bool? InternalPanelOnDgpu()
        {
            try
            {
                using var s = new ManagementObjectSearcher(NS, "SELECT InstanceName, VideoOutputTechnology FROM WmiMonitorConnectionParams");
                foreach (ManagementObject o in s.Get())
                    using (o)
                    {
                        uint vot = Convert.ToUInt32(o["VideoOutputTechnology"]);
                        if (vot != 11 /*DP embedded*/ && vot != 0x80000000 /*internal*/ && vot != 6 /*LVDS*/ && vot != 13 /*UDI embedded*/) continue;
                        string inst = (o["InstanceName"] as string) ?? "";
                        int us = inst.LastIndexOf('_');
                        if (us > 0) inst = inst.Substring(0, us);
                        if (CM_Locate_DevNodeW(out uint dn, inst, 0) != 0 || CM_Get_Parent(out uint par, dn, 0) != 0) return null;
                        var buf = new char[400];
                        if (CM_Get_Device_IDW(par, buf, buf.Length, 0) != 0) return null;
                        string pid = new string(buf).TrimEnd('\0');
                        if (pid.IndexOf("VEN_10DE", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        if (pid.IndexOf("VEN_8086", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                    }
            }
            catch { }
            return null;
        }

        public static GpuSnapshot Snapshot()
        {
            var s = new GpuSnapshot();
            s.PanelOnDgpu = InternalPanelOnDgpu();
            s.Staged = BiosGraphics();
            if (s.PanelOnDgpu == true) s.Running = BiosDiscrete;
            else if (s.PanelOnDgpu == false)
                s.Running = Family(s.Staged) == BiosUma && !DgpuPresent() ? BiosUma : BiosSwitchable;
            else s.Running = Family(s.Staged);

            if (s.Running == BiosSwitchable) { try { s.Igpu = IgpuStatus(); } catch { } }
            s.Mode = s.Running == BiosDiscrete ? GpuMode.Discrete
                   : s.Running == BiosUma ? GpuMode.Uma
                   : s.Running == BiosSwitchable ? SubMode(s.Igpu)
                   : GpuMode.Unknown;
            return s;
        }

        /// Live switch inside Switchable Graphics. Returns true if the read-back matches.
        public static bool SetIgpu(int mode)
        {
            if (mode < 0 || mode > 2) throw new ArgumentOutOfRangeException(nameof(mode));
            Call("LENOVO_GAMEZONE_DATA", "SetIGPUModeStatus", p => p["mode"] = (uint)mode);
            for (int i = 0; i < 10; i++)
            {
                if (IgpuStatus() == mode) return true;
                Thread.Sleep(300);
            }
            return false;
        }

        public static bool SupervisorPasswordSet() => Bios.SupervisorPasswordSet();

        /// Writes BIOS GraphicsDevice and saves. Takes effect after a reboot.
        public static void SetBios(string value) => Bios.Set("GraphicsDevice", value);

        public static bool DgpuPresent()
        {
            using var s = new ManagementObjectSearcher(@"root\cimv2",
                @"SELECT ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPClass='Display' AND PNPDeviceID LIKE 'PCI\\VEN_10DE%'");
            foreach (ManagementObject o in s.Get())
                using (o)
                    if (Convert.ToInt32(o["ConfigManagerErrorCode"]) == 0) return true;
            return false;
        }

        /// Tells the firmware whether the OS sees the dGPU (Lenovo add-in / LLT do this on device changes).
        public static void NotifyDgpu(bool present) =>
            Call("LENOVO_GAMEZONE_DATA", "NotifyDGPUStatus", p => p["status"] = present ? 1u : 0u);

        /// 1 = BitLocker protection on for the system drive, 0 = off, -1 unknown (needs admin)
        public static int SystemDriveBitLocker()
        {
            try
            {
                string sd = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
                using var s = new ManagementObjectSearcher(@"root\CIMV2\Security\MicrosoftVolumeEncryption",
                    $"SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter='{sd}'");
                foreach (ManagementObject o in s.Get()) using (o) return Convert.ToInt32(o["ProtectionStatus"]);
            }
            catch { }
            return -1;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Generic Lenovo BIOS settings over WMI (same calls Lenovo's add-ins use). Admin required.
    // ---------------------------------------------------------------------------------------------
    public static class Bios
    {
        const string NS = @"root\WMI";
        static readonly object Gate = new object();   // one BIOS transaction at a time

        static ManagementBaseObject Call(string cls, string method, Action<ManagementBaseObject> fill)
        {
            using var s = new ManagementObjectSearcher(NS, "SELECT * FROM " + cls);
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    var inp = o.GetMethodParameters(method);
                    fill(inp);
                    return o.InvokeMethod(method, inp, null);
                }
            throw new NotSupportedException(cls + T(" 不存在", " not found"));
        }

        /// Current value of a BIOS item (e.g. "FlipToStart" -> "Disable"), null if the item does not exist.
        public static string Get(string item)
        {
            string prefix = item + ",";
            using var s = new ManagementObjectSearcher(NS, "SELECT CurrentSetting FROM Lenovo_BiosSetting");
            foreach (ManagementObject o in s.Get())
                using (o)
                    if (o["CurrentSetting"] is string cs && cs.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        string v = cs.Substring(prefix.Length);
                        int semi = v.IndexOf(';');
                        if (semi >= 0) v = v.Substring(0, semi);
                        return v.Trim();
                    }
            return null;
        }

        public static string[] Selections(string item)
        {
            var r = Call("Lenovo_GetBiosSelections", "GetBiosSelections", p => p["Item"] = item);
            return ((r["Selections"] as string) ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        }

        public static bool SupervisorPasswordSet()
        {
            using var s = new ManagementObjectSearcher(NS, "SELECT PasswordState FROM Lenovo_BiosPasswordSettings");
            foreach (ManagementObject o in s.Get()) using (o) return (Convert.ToUInt32(o["PasswordState"]) & 2) != 0;
            return false;
        }

        /// SetBiosSetting("item,value;") + SaveBiosSettings(";"), discard on save failure, verify by read-back.
        public static void Set(string item, string value)
        {
            lock (Gate)
            {
                if (SupervisorPasswordSet()) throw new InvalidOperationException(T("BIOS 设有管理员密码，请到 BIOS 设置里修改", "A BIOS supervisor password is set; change this in BIOS setup"));
                var sel = Selections(item);
                if (sel.Length > 0 && !sel.Any(x => Gpu.Eq(x, value)))
                    throw new InvalidOperationException(T($"BIOS 项 {item} 不支持「{value}」（可选：{string.Join(" / ", sel)}）", $"BIOS item {item} does not accept \"{value}\" (choices: {string.Join(" / ", sel)})"));

                var r = Call("Lenovo_SetBiosSetting", "SetBiosSetting", p => p["parameter"] = item + "," + value + ";");
                string ret = r["return"] as string;
                if (!Gpu.Eq(ret, "Success")) throw new InvalidOperationException(T($"SetBiosSetting({item}) 返回：{ret}", $"SetBiosSetting({item}) returned: {ret}"));
                var r2 = Call("Lenovo_SaveBiosSettings", "SaveBiosSettings", p => p["parameter"] = ";");
                string ret2 = r2["return"] as string;
                if (!Gpu.Eq(ret2, "Success"))
                {
                    string undo;
                    try
                    {
                        var d = Call("Lenovo_DiscardBiosSettings", "DiscardBiosSettings", p => p["parameter"] = ";");
                        undo = Gpu.Eq(d["return"] as string, "Success") ? T("已撤销本次修改", "change discarded") : T("撤销失败：", "discard failed: ") + d["return"];
                    }
                    catch (Exception ex) { undo = T("撤销失败：", "discard failed: ") + ex.Message; }
                    throw new InvalidOperationException(T("SaveBiosSettings 返回：", "SaveBiosSettings returned: ") + ret2 + T("（", " (") + undo + T("）", ")"));
                }
                string now = Get(item);
                if (!Gpu.Eq(now, value)) throw new InvalidOperationException(T($"写入后读回的是「{now}」，不是「{value}」", $"Read back \"{now}\" after writing, not \"{value}\""));
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // iGPU sub-mode to apply once a reboot into Switchable Graphics has happened (HKCU, expires in 3 days)
    // ---------------------------------------------------------------------------------------------
    public static class AfterBoot
    {
        const string Key = @"Software\TBMini";

        public static void Save(int igpu)
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            k.SetValue("AfterBootIgpu", igpu, RegistryValueKind.DWord);
            k.SetValue("AfterBootSaved", DateTime.UtcNow.ToFileTimeUtc(), RegistryValueKind.QWord);
        }

        /// -1 if nothing (or expired / invalid)
        public static int Load()
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            if (k?.GetValue("AfterBootIgpu") is not int igpu || k.GetValue("AfterBootSaved") is not long saved) return -1;
            if (igpu < 0 || igpu > 2) return -1;
            if (DateTime.UtcNow - DateTime.FromFileTimeUtc(saved) > TimeSpan.FromDays(3)) return -1;
            return igpu;
        }

        public static void Clear()
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key, true);
            if (k == null) return;
            k.DeleteValue("AfterBootIgpu", false);
            k.DeleteValue("AfterBootSaved", false);
        }
    }
}
