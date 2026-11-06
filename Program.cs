using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using static TBMini.L10n;

[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]

namespace TBMini
{
    static partial class Program
    {
        const string MutexName = @"Local\TBMini.Instance";
        internal const string MsgWindowTitle = "TBMini.MsgWindow";
        internal static readonly int ShowMessage = RegisterWindowMessage("TBMini.Show");

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string s);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
        [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilter(int msg, int flag);

        internal static bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        internal static string ExePath => Process.GetCurrentProcess().MainModule.FileName;
        internal const string RepoUrl = "https://github.com/FrostFlops/TBmini";

        /// Opens a web page in the user's browser. TBMini usually runs elevated, and a browser started from it would run
        /// elevated too; handing the URL to Explorer opens it in the normal (unelevated) desktop session instead.
        internal static void OpenUrl(string url)
        {
            try
            {
                if (IsAdmin) Process.Start(new ProcessStartInfo("explorer.exe", "\"" + url + "\"") { UseShellExecute = false });
                else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }
        internal static string Version
        {
            get
            {
                var a = typeof(Program).Assembly;
                var iv = ((System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    a, typeof(System.Reflection.AssemblyInformationalVersionAttribute)))?.InformationalVersion;
                return string.IsNullOrEmpty(iv) ? a.GetName().Version.ToString(3) : iv.Split('+')[0];
            }
        }

        static bool Has(string[] args, string flag) => Array.Exists(args, x => x.Equals(flag, StringComparison.OrdinalIgnoreCase));

        [STAThread]
        static int Main(string[] args)
        {
            string a0 = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (a0 == "--nc") { MicNoise.HelperMain(args); return 0; }   // noise-plugin helper child process
#if TBMINI_DEV
            if (DevMain(args, out int devRc)) return devRc;
#endif
            if (a0 == "--status" || a0 == "--power" || a0 == "--autostart" || a0 == "--features" || a0 == "--sensors" ||
                a0 == "--version" || a0 == "--help" || a0 == "-h" || a0 == "/?")
                return Cli(args);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // ---- tray mode ----
            // Already running (possibly elevated)? Ask it to show its panel. The running instance lets this message
            // through UIPI, and we hand it foreground rights so its panel can take focus.
            IntPtr existing = FindWindow(null, MsgWindowTitle);
            if (existing != IntPtr.Zero && !Has(args, "--wait-mutex"))
            {
                AllowSetForegroundWindow(-1 /*ASFW_ANY*/);
                PostMessage(existing, ShowMessage, IntPtr.Zero, IntPtr.Zero);
                return 0;
            }

            if (!IsAdmin && !Has(args, "--no-elevate"))
            {
                try
                {
                    string exe = TrayApp.ElevationTarget();
                    Process.Start(new ProcessStartInfo(exe, "--tray") { UseShellExecute = true, Verb = "runas" });
                    return 0;
                }
                catch (Win32Exception) { /* UAC cancelled -> run without GPU control */ }
            }

            Mutex mutex;
            try
            {
                mutex = new Mutex(false, MutexName);
                bool got;
                try { got = mutex.WaitOne(Has(args, "--wait-mutex") ? 15000 : 0); }
                catch (AbandonedMutexException) { got = true; }
                if (!got) return 0;
            }
            catch (UnauthorizedAccessException) { return 0; }   // an elevated instance owns it

            // Elevated: let Explorer's TaskbarCreated through UIPI so the tray icon comes back after an Explorer restart
            if (IsAdmin) ChangeWindowMessageFilter(RegisterWindowMessage("TaskbarCreated"), 1 /*MSGFLT_ADD*/);

            Application.Run(new TrayApp());
            GC.KeepAlive(mutex);
            return 0;
        }

        // ---------------------------------------------------------------- CLI
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n);

        static string OutFile(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i].Equals("--out", StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        static int Emit(StringBuilder sb, string outFile, int rc)
        {
            try
            {
                if (outFile != null) File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
                else
                {
                    if (GetStdHandle(-11) == IntPtr.Zero) AttachConsole(-1);
                    Console.Write(sb.ToString());
                }
            }
            catch { rc = rc == 0 ? 2 : rc; }
            return rc;
        }

        static int Cli(string[] args)
        {
            var sb = new StringBuilder();
            int rc = 0;
            try
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "--power":
                        var m = (args.Length > 1 ? args[1].ToLowerInvariant() : "") switch
                        {
                            "saver" or "quiet" or "省电" => PowerMode.Saver,
                            "auto" or "intelligent" or "智能" => PowerMode.Auto,
                            "perf" or "performance" or "性能" => PowerMode.Performance,
                            "geek" or "极客" => PowerMode.Geek,
                            _ => throw new ArgumentException(T("用法: ", "Usage: ") + "TBMini --power saver|auto|perf|geek"),
                        };
                        Its.Set(m);
                        sb.AppendLine("power=" + Its.Name(Its.Read()));
                        break;

                    case "--status":
                        sb.AppendLine("power=" + Its.Name(Its.Read()) + "  (its_supported=" + Its.Supported + ")");
                        sb.AppendLine("ac=" + Its.OnAC + "  charger=" + Charger.Current + "  adapter_type=" + Charger.AdapterType() + "  geek_supported=" + Its.GeekSupported + "  geek_greyed=" + Its.GeekGreyed + "  geek_available=" + Its.GeekAvailable);
                        sb.AppendLine("windows_power_mode " + WinPowerMode.Describe());
                        sb.AppendLine("usbc_class=" + Charger.ClassifyPd() + "  cpu_pl1=" + (OtherMethod.Get(OtherMethod.CpuPl1)?.ToString() ?? "n/a") + "  cpu_pl2=" + (OtherMethod.Get(OtherMethod.CpuPl2)?.ToString() ?? "n/a"));
                        sb.AppendLine("auto_adjust=" + (Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\TBMini", "AutoPower", 0) is int ap && ap == 1)
                                      + "  charger_events=" + Charger.Subscribe(() => { }));
                        sb.AppendLine("panel_on_dgpu=" + (Gpu.InternalPanelOnDgpu()?.ToString() ?? "unknown"));
                        if (IsAdmin)
                        {
                            var s = Gpu.Snapshot();
                            sb.AppendLine("bios_staged=" + s.Staged + "  running=" + s.Running + "  pending_reboot=" + s.PendingReboot);
                            try { sb.AppendLine("bios_selections=" + string.Join(" | ", Gpu.BiosGraphicsSelections())); }
                            catch (Exception e) { sb.AppendLine("bios_selections=ERR " + e.Message); }
                            sb.AppendLine("igpu_supported=" + Gpu.IgpuSupported() + "  igpu_status=" + s.Igpu);
                            sb.AppendLine("gpu_mode=" + Gpu.Name(s.Mode));
                            sb.AppendLine("dgpu_present=" + Gpu.DgpuPresent());
                            try { sb.AppendLine("bios_password=" + Gpu.SupervisorPasswordSet()); } catch (Exception e) { sb.AppendLine("bios_password=ERR " + e.Message); }
                            sb.AppendLine("bitlocker=" + Gpu.SystemDriveBitLocker());
                        }
                        else sb.AppendLine(T("gpu: (BIOS 设置需要管理员权限读取)", "gpu: (reading BIOS settings needs administrator rights)"));
                        sb.AppendLine("after_boot_igpu=" + AfterBoot.Load());
                        break;

                    case "--features":   // read every feature (read-only)
                        foreach (var f in FeatureRegistry.All)
                        {
                            var sw = Stopwatch.StartNew();
                            string val;
                            try { int v = f.Read(); val = v >= 0 && v < f.Options.Length ? $"{v} ({f.Options[v]})" : v.ToString(); }
                            catch (NotSupportedException e) { val = "unsupported: " + e.Message; }
                            catch (Exception e) { val = "ERR " + e.GetType().Name + ": " + e.Message; }
                            sb.AppendLine($"{f.Id,-9} {Sec.Title(f.Section)}/{f.Label} = {val}   [{string.Join("|", f.Options)}]  {sw.ElapsedMilliseconds} ms");
                        }
                        break;

                    case "--sensors":    // two samples 1 s apart (power counters need an interval)
                        Sensors.Begin();
                        Sensors.Read();
                        Thread.Sleep(1000);
                        var r = Sensors.Read();
                        Sensors.Release();
                        sb.AppendLine("cpu_temp_c=" + Sensors.Fmt(r.CpuTemp) + "  cpu_w=" + Sensors.Fmt(r.CpuW, 1));
                        sb.AppendLine("gpu=" + r.Gpu + "  gpu_temp_c=" + Sensors.Fmt(r.GpuTemp) + "  gpu_w=" + Sensors.Fmt(r.GpuW, 1));
                        sb.AppendLine("total_w=" + Sensors.Fmt(r.TotalW, 1) + "  source=" + r.Total + "  charge_w=" + Sensors.Fmt(r.ChargeW, 1));
                        sb.AppendLine("pawnio_installed=" + Sensors.PawnIOInstalled + "  admin=" + IsAdmin);
                        break;

                    case "--autostart":
                        string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
                        if (sub == "on") sb.AppendLine(TrayApp.EnableAutostart());
                        else if (sub == "off") sb.AppendLine(TrayApp.DisableAutostart());
                        sb.AppendLine("autostart=" + TrayApp.AutostartEnabled() + "  installed=" + File.Exists(TrayApp.InstalledExe));
                        break;

                    case "--version":
                        sb.AppendLine("TBMini " + Version);
                        break;

                    default:
                        sb.AppendLine("TBMini " + Version);
                        sb.AppendLine("TBMini                                  " + T("托盘模式（自动请求管理员权限）", "tray mode (asks for administrator rights)"));
                        sb.AppendLine("TBMini --status [--out file]            " + T("当前状态（只读）", "current state (read-only)"));
                        sb.AppendLine("TBMini --features [--out file]          " + T("所有设置项的当前值（只读）", "every setting's current value (read-only)"));
                        sb.AppendLine("TBMini --sensors [--out file]           " + T("温度和功耗（只读）", "temperatures and power (read-only)"));
                        sb.AppendLine("TBMini --power saver|auto|perf|geek     " + T("切换性能模式", "switch the performance mode"));
                        sb.AppendLine("TBMini --autostart on|off|status        " + T("开机自启（需要管理员）", "start at sign-in (needs administrator)"));
                        break;
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("ERROR: " + e.Message);
                rc = 1;
            }
            return Emit(sb, OutFile(args), rc);
        }
    }
}
