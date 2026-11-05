using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Win32;
using static TBMini.L10n;

namespace TBMini
{
    public enum FeatureKind { Toggle, Choice }

    /// Panel sections: stable ids, localized titles.
    public static class Sec
    {
        public const string Battery = "battery", Power = "power", Keyboard = "keyboard", Display = "display", Sound = "sound", App = "app";

        public static string Title(string id) => id switch
        {
            Battery => T("电池", "Battery"),
            Power => T("电源", "Power"),
            Keyboard => T("键盘", "Keyboard"),
            Display => T("显示", "Display"),
            Sound => T("声音", "Sound"),
            App => T("应用", "App"),
            _ => id,
        };
    }

    /// One setting row in the panel. Read/Write run on a background thread.
    /// Read() returns the option index (Toggle: 0 = off, 1 = on), -1 = unknown;
    /// it throws NotSupportedException when the feature does not exist on this machine (row is hidden).
    public abstract class Feature
    {
        public string Id;
        public string Section;                  // Sec.* id
        public string Label;
        public string Tip;
        public FeatureKind Kind = FeatureKind.Toggle;
        public string[] Options = { T("关", "Off"), T("开", "On") };
        public bool NeedsAdmin;
        public string Note;                     // shown after a successful write

        public abstract int Read();
        public abstract void Write(int value);

        /// Optional confirmation text before writing (null = no prompt).
        public virtual string Confirm(int value) => null;
    }

    /// Feature backed by a Lenovo BIOS setting (Lenovo_BiosSetting / Lenovo_SetBiosSetting).
    public sealed class BiosFeature : Feature
    {
        readonly string _item;
        readonly string[] _values;              // BIOS value per option index

        public BiosFeature(string item, string[] values) { _item = item; _values = values; NeedsAdmin = true; }

        public override int Read()
        {
            string v = Bios.Get(_item) ?? throw new NotSupportedException(_item);
            for (int i = 0; i < _values.Length; i++) if (Gpu.Eq(v, _values[i])) return i;
            return -1;
        }

        public override void Write(int value)
        {
            if ((uint)value >= (uint)_values.Length) throw new ArgumentOutOfRangeException(nameof(value));
            Bios.Set(_item, _values[value]);
        }
    }

    /// Feature from delegates (EnergyDrv IOCTLs, registry, COM …).
    public sealed class DelegateFeature : Feature
    {
        public Func<int> ReadFn;
        public Action<int> WriteFn;
        public Func<int, string> ConfirmFn;
        public override int Read() => ReadFn();
        public override void Write(int value)
        {
            if ((uint)value >= (uint)Options.Length) throw new ArgumentOutOfRangeException(nameof(value));
            WriteFn(value);
        }
        public override string Confirm(int value) => ConfirmFn?.Invoke(value);
    }

    public static class FeatureRegistry
    {
        /// Rebuilt when the UI language changes (labels are resolved at build time).
        public static List<Feature> All { get; private set; } = Build();

        public static void Rebuild() => All = Build();

        public static Feature Get(string id) => All.FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        static void Check(bool ok, string what) { if (!ok) throw new InvalidOperationException(what + T(" 没有生效", " did not take effect")); }

        static List<Feature> Build()
        {
            var L = new List<Feature>();
            const uint BAT = EnergyDrv.IOCTL_BATTERY, HALS = EnergyDrv.IOCTL_HALS;

            // ------------------------------------------------------------- 电池
            int ChargeMode()
            {
                uint v = EnergyDrv.Call(BAT, 0xFF);
                return (v & 0x20) != 0 ? 2 : (v & 0x04) != 0 ? 1 : 0;   // conservation checked first (as Lenovo)
            }
            L.Add(new DelegateFeature
            {
                Id = "charge", Section = Sec.Battery, Label = T("充电模式", "Charge mode"), Kind = FeatureKind.Choice,
                Options = new[] { T("普通", "Normal"), T("快充", "Rapid"), T("养护", "Conserve") },
                Tip = T("快充：更快充满（更热）；养护：长期插电时只充到约 80%，保护电池",
                        "Rapid: charges faster (runs warmer). Conserve: stops at about 80% to protect a battery that stays plugged in"),
                ReadFn = ChargeMode,
                WriteFn = i =>
                {
                    if (i == 1 && (EnergyDrv.Call(BAT, 0xFF) & 0x20000) == 0) throw new InvalidOperationException(T("这台机器不支持快充", "Rapid charge is not supported on this machine"));
                    EnergyDrv.Send(BAT, i == 0 ? new uint[] { 5, 8 } : i == 1 ? new uint[] { 5, 7 } : new uint[] { 8, 3 });
                    Check(EnergyDrv.Poll(() => ChargeMode() == i), T("充电模式", "Charge mode"));
                    // Lenovo's add-in re-applies this string at logon/unlock, keep it in sync
                    using var k = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Lenovo\VantageService\AddinData\IdeaNotebookAddin");
                    k.SetValue("BatteryChargeMode", i == 0 ? "Normal" : i == 1 ? "Quick" : "Storage", RegistryValueKind.ExpandString);
                },
            });

            int Night()
            {
                uint cap = EnergyDrv.Call(EnergyDrv.IOCTL_NIGHT, 0x13);
                if ((cap & 0x11) != 0x11) throw new NotSupportedException("night charge");
                uint s = EnergyDrv.Call(EnergyDrv.IOCTL_NIGHT, 0x11);
                return (s & 1) == 0 ? -1 : (s & 0x10) != 0 ? 1 : 0;
            }
            L.Add(new DelegateFeature
            {
                Id = "night", Section = Sec.Battery, Label = T("夜间充电", "Night charging"),
                Tip = T("夜间充到 80%，白天再充满，减缓电池老化（由电池固件学习作息）",
                        "Holds at 80% overnight and tops up before you wake, to slow battery wear (the firmware learns your schedule)"),
                ReadFn = Night,
                WriteFn = v => { EnergyDrv.Call(EnergyDrv.IOCTL_NIGHT, v == 1 ? 0x80000012u : 0x12u); Check(EnergyDrv.Poll(() => Night() == v), T("夜间充电", "Night charging")); },
            });

            // ------------------------------------------------------------- 电源
            int Usb()
            {
                uint s = EnergyDrv.Call(HALS, 2);
                if ((s & 0x40) == 0) throw new NotSupportedException("always-on USB");
                bool on = (s & 0x80) != 0, bat = (s & 0x4000) != 0 && (s & 0x8000) != 0;
                return !on ? 0 : bat ? 2 : 1;
            }
            L.Add(new DelegateFeature
            {
                Id = "usb", Section = Sec.Power, Label = T("持续USB供电", "Always-on USB"), Kind = FeatureKind.Choice,
                Options = new[] { T("关", "Off"), T("开", "On"), T("含电池关机", "+ battery") },
                Tip = T("睡眠/休眠/关机时继续给 USB 设备供电；「含电池关机」= 不插电关机时也用电池供电（耗电）",
                        "Keeps USB powered in sleep, hibernate and shutdown. \"+ battery\" does this when unplugged too (drains the battery)"),
                ReadFn = Usb,
                WriteFn = i =>
                {
                    if (i == 2 && (EnergyDrv.Call(HALS, 2) & 0x4000) == 0) throw new InvalidOperationException(T("不支持关机时用电池供电", "USB power from the battery while off is not supported"));
                    EnergyDrv.Send(HALS, i == 0 ? new uint[] { 0x0B, 0x12 } : i == 1 ? new uint[] { 0x0A, 0x12 } : new uint[] { 0x0A, 0x13 });
                    Check(EnergyDrv.Poll(() => Usb() == i), T("持续USB供电", "Always-on USB"));
                },
            });

            L.Add(new BiosFeature("FlipToStart", new[] { "Disable", "Enable" })
            {
                Id = "flip", Section = Sec.Power, Label = T("开盖开机", "Power on with lid open"),
                Tip = T("关机状态下打开屏幕就自动开机", "Turns the laptop on when you open the lid"), Note = T("已写入 BIOS", "Saved to BIOS"),
            });

            // ------------------------------------------------------------- 键盘
            int KbLevels()
            {
                uint cap = EnergyDrv.Call(EnergyDrv.IOCTL_KBLC, 1);
                if ((cap & 1) == 0 || (cap >> 1) == 0) throw new NotSupportedException("keyboard backlight");
                return (int)(cap >> 1);
            }
            DelegateFeature kbd = null;
            int KbRead()
            {
                int lv = KbLevels();
                kbd.Options = lv >= 3 ? KbOptions(4) : lv == 2 ? KbOptions(3) : new[] { T("关", "Off"), T("开", "On") };
                uint st = EnergyDrv.Call(EnergyDrv.IOCTL_KBLC, ((uint)lv << 4) | 2);
                if ((st & 1) == 0) return -1;
                uint s = st >> 1;
                return (s & 0x8000) == 0 ? 0 : (int)(s & 0x7FFF);
            }
            kbd = new DelegateFeature
            {
                Id = "kbd", Section = Sec.Keyboard, Label = T("键盘背光", "Backlight"), Kind = FeatureKind.Choice, Options = KbOptions(4),
                Tip = T("也可以用 Fn+空格 切换", "Fn+Space also cycles it"),
                ReadFn = KbRead,
                WriteFn = level =>
                {
                    uint lv = (uint)KbLevels();
                    if ((uint)level > Math.Min(lv, 3u)) throw new ArgumentOutOfRangeException(nameof(level));
                    if ((EnergyDrv.Call(EnergyDrv.IOCTL_KBLC, (lv << 4) | 2) & 0x10000) == 0) throw new InvalidOperationException(T("键盘当前被禁用", "The keyboard is currently disabled"));
                    uint r = EnergyDrv.Call(EnergyDrv.IOCTL_KBLC, ((uint)level << 16) | (lv << 4) | 3);
                    if ((r & 1) == 0) throw new InvalidOperationException(T("EC 拒绝了这个设置", "The EC rejected this setting"));
                    Check(EnergyDrv.Poll(() => KbRead() == level), T("键盘背光", "Keyboard backlight"));
                },
            };
            L.Add(kbd);

            Feature OnOff(string id, string label, uint ioctl, string tip) => new DelegateFeature
            {
                Id = id, Section = Sec.Keyboard, Label = label, Tip = tip,
                ReadFn = () =>
                {
                    if (EnergyDrv.Call(ioctl, 1) != 3) throw new NotSupportedException(id);
                    uint s = EnergyDrv.Call(ioctl, 2);
                    return s == 3 ? 1 : s == 1 ? 0 : -1;
                },
                WriteFn = v =>
                {
                    EnergyDrv.Call(ioctl, v == 1 ? 0x13u : 0x03u);
                    Check(EnergyDrv.Poll(() => { uint s = EnergyDrv.Call(ioctl, 2); return s == (v == 1 ? 3u : 1u); }), label);
                },
            };
            L.Add(OnOff("kbdauto", T("背光30秒自动关", "Backlight off after 30 s"), EnergyDrv.IOCTL_KBLA,
                T("30 秒没有键盘/触摸板操作时自动关背光", "Turns the backlight off after 30 s without keyboard or touchpad input")));
            L.Add(OnOff("kbdkeep", T("记住背光设置", "Remember backlight"), EnergyDrv.IOCTL_KBLR,
                T("重启/唤醒后保持你选的背光档位", "Keeps your backlight level after restart and wake")));

            int FnLock()
            {
                uint s = EnergyDrv.Call(HALS, 2);
                if ((s & 0x200) == 0) throw new NotSupportedException("Fn lock");
                bool hot = ((s & 0x800) != 0) ^ ((s & 0x400) != 0);
                return hot ? 0 : 1;
            }
            L.Add(new DelegateFeature
            {
                Id = "fnlock", Section = Sec.Keyboard, Label = T("F1-F12 首行键", "Top-row keys"), Kind = FeatureKind.Choice,
                Options = new[] { T("热键", "Hotkeys"), "F1-F12" },
                Tip = T("直接按 F1-F12 时：热键 = 音量/亮度等功能；F1-F12 = 标准功能键（Fn+Esc 也能切换）",
                        "What F1-F12 do without Fn: Hotkeys = volume, brightness and so on; F1-F12 = standard function keys (Fn+Esc also toggles)"),
                ReadFn = FnLock,
                WriteFn = i =>
                {
                    uint s = EnergyDrv.Call(HALS, 2);
                    bool primeHot = (s & 0x800) != 0;
                    bool fnOn = (i == 0) != primeHot;
                    EnergyDrv.Call(HALS, fnOn ? 0x0Eu : 0x0Fu);
                    Check(EnergyDrv.Poll(() => FnLock() == i), "Fn Lock");
                },
            });

            L.Add(new DelegateFeature
            {
                Id = "fnctrl", Section = Sec.Keyboard, Label = T("Fn/Ctrl 交换", "Swap Fn/Ctrl"), NeedsAdmin = true,
                Tip = T("交换左下角 Fn 和 Ctrl 键的功能", "Swaps the bottom-left Fn and Ctrl keys"),
                ReadFn = () =>
                {
                    uint cap = BiosAssistant.Capability();
                    if (((cap >> 31) & 1) == 0 || ((cap >> 3) & 1) == 0) throw new NotSupportedException("FnCtrlSwap");
                    uint d = BiosAssistant.Get(1);
                    return ((d >> 31) & 1) == 0 ? -1 : (int)(d & 1);
                },
                WriteFn = v =>
                {
                    uint r = BiosAssistant.Set(1, (uint)v);
                    if ((r & 0x80000000) == 0) throw new InvalidOperationException(T("BIOS 拒绝了这个设置", "The BIOS rejected this setting"));
                    Thread.Sleep(100);
                    uint d = BiosAssistant.Get(1);
                    Check(((d >> 31) & 1) == 1 && (int)(d & 1) == v, T("Fn/Ctrl 交换", "Swap Fn/Ctrl"));
                },
            });

            L.Add(new DelegateFeature
            {
                Id = "capsosd", Section = Sec.Keyboard, Label = T("大写锁定提示", "Caps Lock indicator"),
                Tip = T("按 CapsLock 时屏幕上显示提示", "Shows an on-screen indicator when you press Caps Lock"),
                ReadFn = () => LockOsd.Read("ShowCapslkOSD"), WriteFn = v => LockOsd.Write("ShowCapslkOSD", v, 146, 147),
            });
            L.Add(new DelegateFeature
            {
                Id = "numosd", Section = Sec.Keyboard, Label = T("数字锁定提示", "Num Lock indicator"),
                Tip = T("按 NumLock 时屏幕上显示提示", "Shows an on-screen indicator when you press Num Lock"),
                ReadFn = () => LockOsd.Read("ShowNumlkOSD"), WriteFn = v => LockOsd.Write("ShowNumlkOSD", v, 148, 149),
            });

            L.Add(new DelegateFeature
            {
                Id = "touchpad", Section = Sec.Keyboard, Label = T("触摸板", "Touchpad"),
                Tip = T("只有接了外接鼠标时才能关闭；鼠标拔掉后会自动重新打开。随时按 Ctrl+Alt+Shift+T 可以重新打开触摸板",
                        "Can only be turned off while an external mouse is connected, and turns back on when that mouse is removed. Ctrl+Alt+Shift+T turns it back on at any time"),
                ReadFn = Touchpad.Read,
                WriteFn = v =>
                {
                    if (v == 0) TouchpadGuard.Arm(Touchpad.ExternalMice());
                    try { Touchpad.Write(v); }
                    catch { if (Touchpad.Enabled) TouchpadGuard.Disarm(); throw; }
                    if (v == 1) TouchpadGuard.Disarm();
                },
            });

            // ------------------------------------------------------------- 显示 / 声音
            L.Add(new DelegateFeature
            {
                Id = "color", Section = Sec.Display, Label = T("色彩模式", "Color mode"), Kind = FeatureKind.Choice, Options = ColorPreset.Labels,
                Tip = T("内屏出厂 X-Rite 校色预设（开了 HDR / 自动颜色管理时不生效）。切换显卡模式后会自动重新应用",
                        "Factory X-Rite presets for the internal display (no effect with HDR or Auto color management on). Re-applied automatically after a GPU mode switch"),
                ReadFn = ColorPreset.Read, WriteFn = ColorPreset.Write,
            });

            L.Add(new DelegateFeature
            {
                Id = "dolby", Section = Sec.Sound, Label = T("杜比音效", "Dolby Audio"), Kind = FeatureKind.Choice, Options = Dolby.Labels,
                Tip = T("麦克风被占用（通话）时杜比会自动临时切到「语音」", "Dolby switches to Voice by itself while the microphone is in use (calls)"),
                ReadFn = Dolby.Read, WriteFn = Dolby.Write,
            });

            DelegateFeature nc = null;
            int[] ncModes = MicNoise.BaseModes;
            nc = new DelegateFeature
            {
                Id = "micnc", Section = Sec.Sound, Label = T("麦克风降噪", "Mic noise filter"), Kind = FeatureKind.Choice, Options = MicNoise.BaseLabels,
                Tip = T("多人：会议室多人发言；单人：只突出离麦克风最近的人；空间：保留环境空间感",
                        "Group: several people in a meeting room. Single: only the person closest to the mic. Spatial: keeps the room's sense of space"),
                ReadFn = () =>
                {
                    string j = MicNoise.Helper("get");
                    bool voice = MicNoise.Field(j, "voiceid_mode") == 1;
                    ncModes = voice ? new[] { 0, 1, 2, 3, 4 } : MicNoise.BaseModes;
                    nc.Options = voice ? MicNoise.BaseLabels.Concat(new[] { T("声纹", "Voice ID") }).ToArray() : MicNoise.BaseLabels;
                    return Array.IndexOf(ncModes, MicNoise.Field(j, "microphone_mode"));
                },
                WriteFn = i =>
                {
                    string j = MicNoise.Helper("set " + ncModes[i]);
                    Check(MicNoise.Field(j, "microphone_mode") == ncModes[i], T("麦克风降噪", "Mic noise suppression"));
                },
            };
            L.Add(nc);

            // ------------------------------------------------------------- 应用
            L.Add(new DelegateFeature
            {
                Id = "lang", Section = Sec.App, Label = T("界面语言", "Language"), Kind = FeatureKind.Choice,
                Options = new[] { T("跟随系统", "System"), "中文", "English" },
                Tip = T("跟随系统 = 按 Windows 显示语言（中文系统显示中文，其它显示英文）",
                        "System = follow the Windows display language (Chinese on Chinese Windows, English otherwise)"),
                ReadFn = () => (int)Pref,
                WriteFn = i => L10n.Set((LangPref)i),
            });
            L.Add(new DelegateFeature
            {
                Id = "autostart", Section = Sec.App, Label = T("开机自启", "Start at sign-in"), NeedsAdmin = true,
                Tip = T(@"复制到 C:\Program Files\TBMini，并以计划任务在登录时启动（不弹 UAC）",
                        @"Copies TBMini to C:\Program Files\TBMini and starts it at sign-in as a scheduled task (no UAC prompt)"),
                ReadFn = () => TrayApp.AutostartEnabled() ? 1 : 0,
                WriteFn = v =>
                {
                    if (v == 1) TrayApp.EnableAutostart(); else TrayApp.DisableAutostart();
                    Check(TrayApp.AutostartEnabled() == (v == 1), T("开机自启", "Start at sign-in"));
                },
            });
            return L;
        }

        static string[] KbOptions(int n) => n >= 4
            ? new[] { T("关", "Off"), T("暗", "Low"), T("亮", "High"), T("自动", "Auto") }
            : new[] { T("关", "Off"), T("暗", "Low"), T("亮", "High") };
    }
}
