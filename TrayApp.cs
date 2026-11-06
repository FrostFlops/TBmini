using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using static TBMini.L10n;

namespace TBMini
{
    sealed class TrayApp : ApplicationContext
    {
        readonly NotifyIcon _icon;
        readonly ContextMenuStrip _menu;
        readonly ModePanel _panel;
        readonly MsgWindow _msg;
        readonly RegWatcher _powerWatch;                    // event-driven: Fn+Q / mode changes
        readonly System.Windows.Forms.Timer _guardTimer;     // runs only while the touchpad guard is armed
        readonly System.Windows.Forms.Timer _devDebounce;    // WM_DEVICECHANGE burst -> one guard check
        PowerLineStatus _line;
        readonly SynchronizationContext _ui;

        PowerMode _power = PowerMode.Unknown;
        bool _ac;
        GpuSnapshot _gpu = new GpuSnapshot { Error = T("读取中…", "Reading…") };
        bool _busy;
        Icon _currentIcon;
        readonly Dictionary<string, FeatureState> _fstate = new Dictionary<string, FeatureState>();
        System.Windows.Forms.Timer _colorDebounce;
        static int _reconciling;
#if TBMINI_DEV
        readonly bool _test;                     // dev preview: read-only, no automatic writes
        public TrayApp(bool testMode = false)
        {
            _test = testMode;
#else
        readonly bool _test = false;
        public TrayApp()
        {
#endif
            _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            _menu = new ContextMenuStrip();
            _menu.Opening += (s, e) => { BuildMenu(); e.Cancel = false; };
            _icon = new NotifyIcon { ContextMenuStrip = _menu, Visible = true, Text = "TBMini" };
            _icon.MouseUp += (s, e) => { if (e.Button == MouseButtons.Left) ShowPanel(); };

            _panel = new ModePanel(this);
            _msg = new MsgWindow(ShowPanel, TouchpadRescue, OnDeviceChange);

            // No periodic timers while idle: power mode via registry change notification, AC via SystemEvents,
            // touchpad guard via WM_DEVICECHANGE (+ a slow fallback only while armed).
            _powerWatch = RegWatcher.TryCreate(Its.SliderKeyPath, () => _ui.Post(_ => PollPower(), null));
            _guardTimer = new System.Windows.Forms.Timer { Interval = 15000 };
            _guardTimer.Tick += (s, e) => GuardCheck();
            _devDebounce = new System.Windows.Forms.Timer { Interval = 1500 };
            _devDebounce.Tick += (s, e) => { _devDebounce.Stop(); GuardCheck(); };
            _line = SystemInformation.PowerStatus.PowerLineStatus;
            SystemEvents.PowerModeChanged += OnSystemPower;
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            _colorDebounce = new System.Windows.Forms.Timer { Interval = 3000 };
            _colorDebounce.Tick += (s, e) => { _colorDebounce.Stop(); _ = Task.Run(() => { try { ColorPreset.Reapply(); } catch { } }); };

            // Auto adjust (saver on battery / performance on a slow charger / geek on the full-power adapter):
            // reacts to charger events only — a one-shot timer re-checks shortly after an event, nothing polls.
            _autoPower = Registry.GetValue(@"HKEY_CURRENT_USER\Software\TBMini", "AutoPower", 0) is int ap && ap == 1;
            _autoTimer = new System.Windows.Forms.Timer();
            _autoTimer.Tick += (s, e) => AutoTick();
            Charger.Subscribe(() => _ui.Post(_ => { PollPower(force: true); ScheduleAuto(1500); }, null));

            PollPower(force: true);
            if (_test) _ = RefreshGpuAsync();
            else
            {
                StartupAsync();
                if (!Program.IsAdmin) ScheduleAuto(3000);   // elevated: StartupAsync schedules it after the boot work
            }
        }

        // ------------------------------------------------------------------ auto adjust
        bool _autoPower;
        readonly System.Windows.Forms.Timer _autoTimer;
        int _autoChecks;

        internal bool AutoPower => _autoPower;

        internal void SetAutoPower(bool on)
        {
            _autoPower = on;
            try { Registry.SetValue(@"HKEY_CURRENT_USER\Software\TBMini", "AutoPower", on ? 1 : 0, RegistryValueKind.DWord); } catch { }
            _panel.UpdateView();
            if (on) ApplyAuto();
        }

        /// Checks now-ish and once more a few seconds later (a USB-C PD charger is reported as slow only after it negotiated).
        void ScheduleAuto(int ms)
        {
            if (_test) return;
            _autoChecks = 2;
            _autoTimer.Stop(); _autoTimer.Interval = ms; _autoTimer.Start();
        }

        void AutoTick()
        {
            _autoTimer.Stop();
            if (_busy) { _autoTimer.Interval = 2000; _autoTimer.Start(); return; }   // wait for the running switch, don't spend the check
            ApplyAuto(quiet: _autoChecks > 1);                                    // only the last check may report a failure
            if (--_autoChecks > 0) { _autoTimer.Interval = 6000; _autoTimer.Start(); }
        }

        static PowerMode AutoTarget() => Charger.Current switch
        {
            ChargerKind.None => PowerMode.Saver,
            ChargerKind.Slow => PowerMode.Performance,
            _ => Its.GeekAvailable ? PowerMode.Geek : PowerMode.Performance,
        };

        void ApplyAuto(bool quiet = false)
        {
            if (_test || _busy || !Its.Supported || _power == PowerMode.Unknown) return;   // no Lenovo mode service: nothing to drive
            PowerMode target;
            if (_autoPower) target = AutoTarget();
            else if (_power == PowerMode.Geek && Charger.Current == ChargerKind.Slow) target = PowerMode.Performance;   // Geek is not allowed on a slow charger
            else return;
            if (target != _power) SetPowerCore(target, quiet);
        }

        // ------------------------------------------------------------------ state
        void PollPower(bool force = false)
        {
            var p = Its.Read();
            bool ac = Its.OnAC;
            if (!force && p == _power && ac == _ac) return;
            _power = p; _ac = ac;
            UpdateIcon();
            _panel.UpdateView();
        }

        void OnSystemPower(object sender, PowerModeChangedEventArgs e)
        {
            _ui.Post(_ => PollPower(force: true), null);
            if (_test) return;
            // StatusChange also fires for every battery-percentage step; only react to real AC plug/unplug.
            var line = SystemInformation.PowerStatus.PowerLineStatus;
            bool acChanged = line != _line;
            _line = line;
            if (Program.IsAdmin && (e.Mode == PowerModes.Resume || (e.Mode == PowerModes.StatusChange && acChanged)))
                _ui.Post(_ => { if (!_busy) _ = Task.Run(() => ReconcileDgpu(5000)); }, null);
            if (e.Mode == PowerModes.Resume || (e.Mode == PowerModes.StatusChange && acChanged))
                _ui.Post(_ => ScheduleAuto(2500), null);   // let Lenovo's dispatcher settle first
            if (e.Mode == PowerModes.Resume)
                _ui.Post(_ => { RestartColorDebounce(); _ = Task.Run(() => TouchpadGuard.Check(startup: true)); }, null);
        }

        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (_test || (e.Reason != SessionSwitchReason.SessionUnlock && e.Reason != SessionSwitchReason.SessionLogon)) return;
            _ui.Post(_ => { RestartColorDebounce(); _ = Task.Run(() => TouchpadGuard.Check(startup: true)); }, null);
        }

        void RestartColorDebounce() { _colorDebounce.Stop(); _colorDebounce.Start(); }

        void OnDeviceChange()
        {
            if (_test || !TouchpadGuard.Armed) return;
            _devDebounce.Stop(); _devDebounce.Start();
        }

        /// Guard check off the UI thread; keeps the slow fallback timer running only while armed.
        void GuardCheck()
        {
            _ = Task.Run(() => { if (TouchpadGuard.Armed) TouchpadGuard.Check(); })
                .ContinueWith(_ => SyncGuardTimer(), TaskScheduler.FromCurrentSynchronizationContext());
        }

        internal void SyncGuardTimer()
        {
            if (_test) return;
            if (TouchpadGuard.Armed) { if (!_guardTimer.Enabled) _guardTimer.Start(); }
            else _guardTimer.Stop();
        }

        /// Global hotkey Ctrl+Alt+Shift+T: always turns the touchpad back on.
        void TouchpadRescue()
        {
            _ = Task.Run(() =>
            {
                try { Touchpad.Write(1); TouchpadGuard.Disarm(); _ui.Post(_ => { Balloon(T("触摸板已打开", "Touchpad turned on")); RefreshFeatures(); SyncGuardTimer(); }, null); }
                catch (Exception e) { _ui.Post(_ => Error(T("打开触摸板失败", "Could not turn the touchpad on"), e), null); }
            });
        }

        static GpuSnapshot ReadGpu()
        {
            if (!Program.IsAdmin)
            {
                var s = new GpuSnapshot { Error = T("需要管理员权限", "needs administrator rights") };
                s.PanelOnDgpu = Gpu.InternalPanelOnDgpu();   // works without admin
                if (s.PanelOnDgpu == true) { s.Running = Gpu.BiosDiscrete; s.Mode = GpuMode.Discrete; }
                return s;
            }
            try { return Gpu.Snapshot(); }
            catch (Exception e) { return new GpuSnapshot { Error = e.Message }; }
        }

        async void StartupAsync()
        {
            if (Program.IsAdmin)
            {
                _busy = true;
                string msg = null;
                try { msg = await Task.Run(ApplyAfterBoot); }
                finally { _busy = false; }
                ScheduleAuto(1500);                          // after the boot work, so the first auto check is not lost to _busy
                if (msg != null) Balloon(msg);
                _ = Task.Run(() => ReconcileDgpu(10000));
                _ = Task.Run(RefreshInstalledCopy);
            }
            _ = Task.Run(() => { try { ColorPreset.Reapply(); } catch { } });
            await Task.Run(() => TouchpadGuard.Check(startup: true));
            SyncGuardTimer();
            await RefreshGpuAsync();
        }

        void OnDisplayChanged(object sender, EventArgs e)
        {
            if (!_test) _ui.Post(_ => RestartColorDebounce(), null);
        }

        // ------------------------------------------------------------------ feature rows
        internal FeatureState FeatureStateOf(Feature f)
        {
            if (!_fstate.TryGetValue(f.Id, out var st)) _fstate[f.Id] = st = new FeatureState();
            return st;
        }

        readonly HashSet<string> _reading = new HashSet<string>();

        void RefreshFeatures()
        {
            foreach (var f in FeatureRegistry.All)
            {
                var st = FeatureStateOf(f);
                if (f.NeedsAdmin && !Program.IsAdmin) { st.Supported = false; continue; }
                if (st.Busy || !_reading.Add(f.Id)) continue;      // unsupported rows are re-probed on every open
                var ff = f;
                Task.Run(() =>
                {
                    bool? sup; int v = -1;
                    try { v = ff.Read(); sup = true; }
                    catch (NotSupportedException) { sup = false; }
                    catch { sup = st.Supported ?? true; }
                    return (sup, v);
                }).ContinueWith(t =>
                {
                    _reading.Remove(ff.Id);
                    var (sup, v) = t.Result;
                    st.Supported = sup; if (sup == true) st.Value = v;
                    _panel.UpdateView();
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
        }

        /// value = option index, or -2 = toggle based on a fresh hardware read (the panel may be stale).
        internal async void SetFeature(Feature f, int value)
        {
            var st = FeatureStateOf(f);
            if (st.Busy) return;
            string confirm = value >= 0 ? f.Confirm(value) : null;
            if (confirm != null && !Ask(confirm)) return;
            st.Busy = true; _panel.UpdateView();
            bool wasZh = Zh;
            try
            {
                await Task.Run(() =>
                {
                    int v = value;
                    if (v == -2) { int cur = f.Read(); v = cur == 1 ? 0 : 1; }
                    f.Write(v);
                });
                if (f.Note != null) Balloon(f.Label + T("：", ": ") + f.Note);
                if (f.Id == "lang" && Zh != wasZh) ApplyLanguage();
            }
            catch (Exception e) { Error(f.Label + T(" 设置失败", ": setting failed"), e); }
            finally
            {
                try { st.Value = await Task.Run(() => f.Read()); } catch { }
                st.Busy = false;
                _panel.UpdateView();
                if (f.Id == "touchpad") SyncGuardTimer();
            }
        }

        internal async Task RefreshGpuAsync()
        {
            _gpu = await Task.Run(ReadGpu);
            UpdateIcon();
            _panel.UpdateView();
        }

        /// After a reboot into Switchable Graphics: apply the iGPU sub-mode the user picked before rebooting.
        static string ApplyAfterBoot()
        {
            try
            {
                int want = AfterBoot.Load();
                if (want < 0) { AfterBoot.Clear(); return null; }
                var s = Gpu.Snapshot();
                // Not rebooted yet (panel still on the dGPU), lid closed, or BIOS changed elsewhere -> keep / drop
                if (s.PanelOnDgpu == null) return null;
                if (s.PanelOnDgpu == true) return null;
                if (Gpu.Family(s.Staged) != Gpu.BiosSwitchable) { AfterBoot.Clear(); return null; }
                AfterBoot.Clear();
                if (s.Igpu == want) return null;
                for (int i = 0; i < 6; i++)
                {
                    try { if (Gpu.SetIgpu(want)) return T("显卡已切换为「" + Gpu.Name(Gpu.SubMode(want)) + "」", "GPU switched to " + Gpu.Name(Gpu.SubMode(want))); } catch { }
                    Thread.Sleep(5000);
                }
                return T("重启后没能切到「" + Gpu.Name(Gpu.SubMode(want)) + "」（独显可能被占用），请在面板里再点一次",
                         "Could not switch to " + Gpu.Name(Gpu.SubMode(want)) + " after the restart (the dGPU may be in use). Click it again in the panel.");
            }
            catch (Exception e) { return T("处理重启后的显卡设置失败：", "Applying the GPU setting after the restart failed: ") + e.Message; }
        }

        /// Keep the firmware informed about dGPU presence (Lenovo's add-in / LLT do this on start, resume, AC change).
        static void ReconcileDgpu(int delayMs)
        {
            if (Interlocked.Exchange(ref _reconciling, 1) == 1) return;    // one at a time
            try { Thread.Sleep(delayMs); ReconcileCore(); }
            finally { Interlocked.Exchange(ref _reconciling, 0); }
        }

        static void ReconcileCore()
        {
            try
            {
                if (Gpu.InternalPanelOnDgpu() != false) return;          // only meaningful in Switchable mode
                int igpu = Gpu.IgpuStatus();
                if (igpu == 1) NudgeDgpuEject();
                else Gpu.NotifyDgpu(Gpu.DgpuPresent());
            }
            catch { }
        }

        /// LLT-style: while iGPU-only is set but the dGPU is still enumerated, tell the firmware it is present so it
        /// can finish detaching it once idle. Returns true if the dGPU is gone (or the mode changed).
        static bool NudgeDgpuEject()
        {
            for (int i = 0; i < 5; i++)
            {
                Thread.Sleep(5000);
                try
                {
                    if (Gpu.IgpuStatus() != 1 || !Gpu.DgpuPresent()) return true;
                    Gpu.NotifyDgpu(true);
                }
                catch { return true; }
            }
            try { return !Gpu.DgpuPresent(); } catch { return true; }
        }

        /// Best-effort list of processes using the NVIDIA GPU (only called after an iGPU-only switch did not detach it).
        static string DgpuProcesses()
        {
            try
            {
                string smi = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
                if (!File.Exists(smi)) return null;
                var p = Process.Start(new ProcessStartInfo(smi) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match m in Regex.Matches(o, @"\|\s+\d+\s+\S+\s+\S+\s+\d+\s+(?:C\+G|C|G)\s+(.+?)\s+(?:N/A|\d+MiB)\s+\|"))
                {
                    string n = m.Groups[1].Value.Trim();
                    int slash = n.LastIndexOfAny(new[] { '\\', '/' });
                    if (slash >= 0) n = n.Substring(slash + 1);
                    if (n.Length > 0 && !n.StartsWith("...")) names.Add(n);
                }
                return names.Count == 0 ? null : string.Join(T("、", ", "), names.Take(6));
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ actions
        internal PowerMode Power => _power;
        internal bool OnAc => _ac;
        internal GpuSnapshot GpuInfo => _gpu;
        internal bool Busy => _busy;

        PowerMode? _pendingPower;      // a user choice made while another switch was running

        /// A mode picked by the user (panel, tray menu): this also turns auto adjust off.
        internal void SetPower(PowerMode m)
        {
            if (_autoPower) SetAutoPower(false);
            if (_busy) { _pendingPower = m; return; }      // applied when the running switch finishes
            SetPowerCore(m);
        }

        async void SetPowerCore(PowerMode m, bool quiet = false)
        {
            if (_busy || m == _power) { _panel.UpdateView(); return; }
            _busy = true; _panel.UpdateView();
            try { await Task.Run(() => Its.Set(m)); }
            catch (Exception e) { if (!quiet) Error(T("切换性能模式失败", "Could not switch the performance mode"), e); }
            finally { _busy = false; PollPower(force: true); RunPendingPower(); }
        }

        void RunPendingPower()
        {
            if (_pendingPower is PowerMode p && !_busy)
            {
                _pendingPower = null;
                if (!_autoPower && p != _power) SetPowerCore(p);
            }
        }

        internal async void SetGpu(GpuMode target)
        {
            if (_busy) return;
            if (!Program.IsAdmin)
            {
                if (Ask(T("切换显卡需要管理员权限。\n以管理员身份重新启动 TBMini？", "Switching the GPU needs administrator rights.\nRestart TBMini as administrator?"))) RelaunchElevated();
                return;
            }
            _busy = true; _panel.UpdateView();
            try
            {
                var s = await Task.Run(Gpu.Snapshot);
                if (s.Staged == null) throw new InvalidOperationException(T("读不到 BIOS 的显卡设置", "Cannot read the BIOS graphics setting"));
                if (!s.RunningKnown) throw new InvalidOperationException(T("内屏没有点亮（合盖？），无法确认当前是不是独显直连。打开内屏后再试。",
                    "The internal display is off (lid closed?), so TBMini cannot tell whether dGPU direct is active. Turn the internal display on and try again."));

                bool wantDiscrete = target == GpuMode.Discrete;
                string wantBios = wantDiscrete ? Gpu.BiosDiscrete : Gpu.BiosSwitchable;
                int wantIgpu = target switch { GpuMode.Hybrid => 0, GpuMode.IgpuOnly => 1, GpuMode.Auto => 2, _ => -1 };

                if (s.Running == wantBios)
                {
                    // ---- no reboot needed (possibly cancelling a staged change) ----
                    int before = s.Igpu;
                    bool ok = await Task.Run(() =>
                    {
                        if (Gpu.Family(s.Staged) != wantBios) Gpu.SetBios(wantBios);
                        AfterBoot.Clear();
                        return wantIgpu < 0 || s.Igpu == wantIgpu || Gpu.SetIgpu(wantIgpu);
                    });
                    if (!ok && wantIgpu == 1)
                        Balloon(T("集显模式没有生效：独显正被程序占用。关闭用独显的程序后再试。", "iGPU only did not take effect: an app is using the dGPU. Close it and try again."));
                    else if (!ok)
                        Balloon(T("显卡模式写入后读回不一致，请稍后再看一下状态", "The GPU mode read back differently after writing; check the status again shortly"));
                    else if (wantIgpu == 1 && before != 1)
                        _ = Task.Run(() =>
                        {
                            if (NudgeDgpuEject()) return;
                            string who = DgpuProcesses();
                            _ui.Post(_ => Balloon(T("已切到集显，但独显还没断开" + (who != null ? "，正在使用独显：" + who : "") + "。关闭这些程序后会自动断开。",
                                "Switched to iGPU only, but the dGPU is still attached" + (who != null ? ". Using the dGPU: " + who : "") + ". It detaches once those apps close.")), null);
                        });
                    else if (wantIgpu == 0 && before == 1)
                        _ = Task.Run(() => ReconcileDgpu(5000));
                }
                else
                {
                    // ---- mux change: BIOS write + reboot ----
                    if (Gpu.Family(s.Staged) == wantBios && (wantIgpu < 0 || AfterBoot.Load() == wantIgpu))
                    {
                        AskRestart(T("这个切换已经写入，重启后生效。", "This switch is already saved and takes effect after a restart."));
                        return;
                    }
                    _panel.Hide();
                    string warn = "";
                    if (await Task.Run(Gpu.SystemDriveBitLocker) == 1)
                        warn += T("\n\n⚠ 系统盘开着 BitLocker，修改 BIOS 设置可能要求输入恢复密钥，请先确认你有恢复密钥。",
                                  "\n\n⚠ BitLocker is on for the system drive. Changing BIOS settings may ask for the recovery key, so make sure you have it.");
                    var choice = MessageBox.Show(
                        T($"切换到「{Gpu.Name(target)}」需要重启电脑才能生效。外接显示器可能需要重新插拔。{warn}\n\n" +
                          "是：写入 BIOS 并立即重启（请先保存工作）\n否：写入 BIOS，稍后自己重启\n取消：不切换",
                          $"Switching to {Gpu.Name(target)} takes effect after a restart. External displays may need to be reconnected.{warn}\n\n" +
                          "Yes: save to BIOS and restart now (save your work first)\nNo: save to BIOS, restart later yourself\nCancel: do not switch"),
                        "TBMini", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button3,
                        MessageBoxOptions.DefaultDesktopOnly);
                    if (choice == DialogResult.Cancel) return;

                    await Task.Run(() =>
                    {
                        // LLT resets a latched iGPU-only flag before entering dGPU-direct
                        if (wantDiscrete && s.Running == Gpu.BiosSwitchable && s.Igpu > 0) Gpu.SetIgpu(0);
                        if (Gpu.Family(s.Staged) != wantBios) Gpu.SetBios(wantBios);
                        if (wantIgpu >= 0) AfterBoot.Save(wantIgpu); else AfterBoot.Clear();
                    });
                    if (choice == DialogResult.Yes) Reboot();
                    else Balloon(T($"已写入，下次重启后切到「{Gpu.Name(target)}」", $"Saved. Switches to {Gpu.Name(target)} at the next restart"));
                }
            }
            catch (Exception e) { Error(T("切换显卡模式失败", "Could not switch the GPU mode"), e); }
            finally
            {
                _busy = false;
                RunPendingPower();
                await RefreshGpuAsync();
            }
        }

        static void Reboot() =>
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { UseShellExecute = false, CreateNoWindow = true });

        void AskRestart(string head)
        {
            if (Ask(head + T("\n\n现在重启吗？（请先保存工作）", "\n\nRestart now? (Save your work first.)"))) Reboot();
        }

        internal void RestartNow() => AskRestart(T("显卡模式的更改要重启后才生效。", "The GPU mode change takes effect after a restart."));

        void RelaunchElevated()
        {
            string exe = ElevationTarget();
            _msg.DestroyHandle();                    // so the elevated copy does not just wake us up
            try
            {
                Process.Start(new ProcessStartInfo(exe, "--tray --wait-mutex") { UseShellExecute = true, Verb = "runas" });
                ExitThread();
            }
            catch (Win32Exception) { _msg.Recreate(); }   // UAC cancelled: keep running
        }

        // ------------------------------------------------------------------ autostart (Task Scheduler, highest privileges)
        // The elevated task points at a copy in Program Files (admin-only writable), never at a user-writable path.
        const string TaskName = "TBMini";
        static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TBMini");
        internal static string InstalledExe => Path.Combine(InstallDir, "TBMini.exe");

        /// Exe to relaunch elevated: the admin-protected installed copy, but only if it is byte-identical to us
        /// (never an older build left behind in Program Files).
        internal static string ElevationTarget()
        {
            try
            {
                string self = Program.ExePath;
                if (!File.Exists(InstalledExe) || string.Equals(Path.GetFullPath(self), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
                    return self;
                using var sha = System.Security.Cryptography.SHA256.Create();
                byte[] a, b;
                using (var fa = File.OpenRead(self)) a = sha.ComputeHash(fa);
                using (var fb = File.OpenRead(InstalledExe)) b = sha.ComputeHash(fb);
                return a.SequenceEqual(b) ? InstalledExe : self;
            }
            catch { return Program.ExePath; }
        }

        static (int code, string output) Schtasks(string args)
        {
            var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            string err = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return (p.ExitCode, err.Trim());
        }

        internal static bool AutostartEnabled()
        {
            try { return Schtasks($"/Query /TN \"{TaskName}\"").code == 0; } catch { return false; }
        }

        void ToggleAutostart()
        {
            try { Balloon(AutostartEnabled() ? DisableAutostart() : EnableAutostart()); }
            catch (Exception e) { Error(T("设置开机自启失败", "Could not change start at sign-in"), e); }
        }

        /// Update path: if auto-start is on and we are a different build than the installed copy (and not the
        /// installed copy itself), copy ourselves over it so the next logon starts this version.
        static void RefreshInstalledCopy()
        {
            try
            {
                string self = Program.ExePath;
                if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase)) return;
                if (!File.Exists(InstalledExe) || ElevationTarget() == InstalledExe || !AutostartEnabled()) return;
                File.Copy(self, InstalledExe, true);
                string cfg = self + ".config";
                if (File.Exists(cfg)) File.Copy(cfg, InstalledExe + ".config", true);
            }
            catch { }
        }

        internal static string DisableAutostart()
        {
            var (c, o) = Schtasks($"/Delete /TN \"{TaskName}\" /F");
            if (c != 0) throw new InvalidOperationException(o);
            return T("已关闭开机自启", "Start at sign-in turned off");
        }

        internal static string EnableAutostart()
        {
            {
                Directory.CreateDirectory(InstallDir);
                string self = Program.ExePath;
                if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(self, InstalledExe, true);
                    string cfg = self + ".config";
                    if (File.Exists(cfg)) File.Copy(cfg, InstalledExe + ".config", true);
                }
                // keep Dolby control working if Baiying is uninstalled later
                try { if (File.Exists(Dolby.BaiyingDll)) File.Copy(Dolby.BaiyingDll, Path.Combine(InstallDir, "DolbyRpcClient.dll"), true); } catch { }
                string user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
                string xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>TBMini tray (performance / GPU mode switcher)</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{Esc(user)}</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>
  <Principals><Principal id=""Author""><UserId>{Esc(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>5</Priority>
  </Settings>
  <Actions Context=""Author""><Exec><Command>{Esc(InstalledExe)}</Command><Arguments>--tray</Arguments></Exec></Actions>
</Task>";
                string xmlPath = Path.Combine(InstallDir, "task.xml");   // admin-only folder (no TOCTOU via %TEMP%)
                File.WriteAllText(xmlPath, xml, Encoding.Unicode);
                try
                {
                    var (c, o) = Schtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
                    if (c != 0) throw new InvalidOperationException(o);
                }
                finally { try { File.Delete(xmlPath); } catch { } }
                return T($"已开启开机自启（程序已复制到 {InstallDir}）", $"Start at sign-in turned on (copied to {InstallDir})");
            }
        }

        static string Esc(string s) => System.Security.SecurityElement.Escape(s);

        // ------------------------------------------------------------------ UI bits
        void BuildMenu()
        {
            _menu.Items.Clear();
            _menu.Items.Add(new ToolStripMenuItem($"TBMini {Program.Version}") { Enabled = false });
            _menu.Items.Add(new ToolStripMenuItem(T($"性能：{Its.Name(_power)}    显卡：{GpuText()}", $"Performance: {Its.Name(_power)}    GPU: {GpuText()}")) { Enabled = false });
            _menu.Items.Add(new ToolStripSeparator());
            foreach (var m in new[] { PowerMode.Saver, PowerMode.Auto, PowerMode.Performance, PowerMode.Geek })
            {
                bool geekOff = m == PowerMode.Geek && !Its.GeekAvailable;
                string why = geekOff ? (Charger.Current == ChargerKind.Slow ? T("（需原装大功率适配器）", " (needs the full-power adapter)") : T("（需接电源）", " (needs AC power)")) : "";
                var it = new ToolStripMenuItem(Its.Name(m) + why) { Checked = _power == m && !_autoPower, Enabled = !_busy && !geekOff };
                var mm = m; it.Click += (s, e) => SetPower(mm);
                _menu.Items.Add(it);
            }
            var adapt = new ToolStripMenuItem(T($"自动调节（当前：{Its.Name(_power)}）", $"Auto adjust (now: {Its.Name(_power)})")) { Checked = _autoPower, Enabled = !_busy && Its.Supported };
            adapt.Click += (s, e) => SetAutoPower(!_autoPower);
            _menu.Items.Add(adapt);
            _menu.Items.Add(new ToolStripSeparator());
            foreach (var g in new[] { GpuMode.Hybrid, GpuMode.IgpuOnly, GpuMode.Discrete })
            {
                var it = new ToolStripMenuItem(T("显卡：", "GPU: ") + Gpu.Name(g)) { Checked = _gpu.Mode == g, Enabled = !_busy };
                var gg = g; it.Click += (s, e) => SetGpu(gg);
                _menu.Items.Add(it);
            }
            if (_gpu.PendingReboot)
            {
                var it = new ToolStripMenuItem(T("重启以切到：", "Restart to switch to: ") + PendingText());
                it.Click += (s, e) => RestartNow();
                _menu.Items.Add(it);
            }
            _menu.Items.Add(new ToolStripSeparator());
            var lang = new ToolStripMenuItem("语言 / Language");
            var langNames = new[] { T("跟随系统", "System"), "中文", "English" };
            for (int i = 0; i < 3; i++)
            {
                var p = (LangPref)i;
                var li = new ToolStripMenuItem(langNames[i]) { Checked = Pref == p };
                li.Click += (s, e) => SetLanguage(p);
                lang.DropDownItems.Add(li);
            }
            _menu.Items.Add(lang);
            if (Program.IsAdmin)
            {
                var auto = new ToolStripMenuItem(T("开机自启", "Start at sign-in")) { Checked = AutostartEnabled(), Enabled = !_busy };
                auto.Click += (s, e) => ToggleAutostart();
                _menu.Items.Add(auto);
            }
            else
            {
                var el = new ToolStripMenuItem(T("以管理员身份重启 TBMini（显卡切换需要）", "Restart TBMini as administrator (needed for GPU switching)"));
                el.Click += (s, e) => RelaunchElevated();
                _menu.Items.Add(el);
            }
            var exit = new ToolStripMenuItem(T("退出", "Exit")) { Enabled = !_busy && !_fstate.Values.Any(x => x.Busy) };
            exit.Click += (s, e) => ExitThread();
            _menu.Items.Add(exit);
            GlassMenu.Style(_menu, Theme.Load());   // same glass / colours as the panel, rebuilt with the current theme
        }

        /// Tray-menu path: save the preference and refresh the UI.
        void SetLanguage(LangPref p)
        {
            try { if (L10n.Set(p)) ApplyLanguage(); else RefreshFeatures(); }
            catch (Exception e) { Error("Language", e); }
        }

        /// Re-resolve every UI string after a language change (labels are built into the feature registry).
        void ApplyLanguage()
        {
            FeatureRegistry.Rebuild();
            _panel.Relocalize();
            UpdateIcon();
            RefreshFeatures();
            _ = RefreshGpuAsync();     // GPU status texts are produced while reading
        }

        internal string GpuText()
        {
            if (_gpu.Mode != GpuMode.Unknown) return Gpu.Name(_gpu.Mode);
            if (_gpu.PanelOnDgpu == false && !Program.IsAdmin) return T("非独显直连", "not dGPU direct");
            return "—";
        }

        internal string PendingText()
        {
            if (!_gpu.PendingReboot) return null;
            string fam = Gpu.Family(_gpu.Staged);
            if (fam == Gpu.BiosDiscrete) return Gpu.Name(GpuMode.Discrete);
            if (fam == Gpu.BiosUma) return Gpu.Name(GpuMode.Uma);
            int ab = AfterBoot.Load();
            return Gpu.Name(ab >= 0 ? Gpu.SubMode(ab) : GpuMode.Hybrid);
        }

        void UpdateIcon()
        {
            var old = _currentIcon;
            _currentIcon = Draw.TrayIcon(_power);
            _icon.Icon = _currentIcon;
            old?.Dispose();
            string t = T($"TBMini · 性能：{Its.Name(_power)} · 显卡：{GpuText()}", $"TBMini · {Its.Name(_power)} · GPU: {GpuText()}");
            if (_gpu.PendingReboot) t += T($"（重启后：{PendingText()}）", $" (after restart: {PendingText()})");
            _icon.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }

#if TBMINI_DEV
        internal void RefreshForRender() => RefreshFeatures();
        internal void RenderPanelTo(string path) => _panel.RenderTo(path);
        internal void ApplyLanguageForDev() => ApplyLanguage();
        internal ModePanel PanelForDev => _panel;
        internal ContextMenuStrip MenuForDev() { BuildMenu(); return _menu; }
#endif

        void ShowPanel()
        {
            if (_panel.Visible) { _panel.Hide(); return; }
            if (unchecked(Environment.TickCount - _panel.HiddenAt) < 300) return;   // this click just closed it
            _panel.ShowNearCursor();
            RefreshFeatures();
            _ = RefreshGpuAsync();
        }

        void Balloon(string text) => _icon.ShowBalloonTip(5000, "TBMini", text, ToolTipIcon.Info);

        void Error(string title, Exception e)
        {
            string msg = e is AggregateException ae && ae.InnerException != null ? ae.InnerException.Message : e.Message;
            _icon.ShowBalloonTip(8000, title, msg, ToolTipIcon.Error);
        }

        static bool Ask(string text) =>
            MessageBox.Show(text, "TBMini", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2, MessageBoxOptions.DefaultDesktopOnly) == DialogResult.Yes;

        protected override void ExitThreadCore()
        {
            SystemEvents.PowerModeChanged -= OnSystemPower;
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            _autoTimer.Dispose();
            _powerWatch?.Dispose();
            _guardTimer.Stop();
            _devDebounce.Stop();
            _icon.Visible = false;
            _icon.Dispose();
            _panel.Dispose();
            _msg.DestroyHandle();
            _currentIcon?.Dispose();
            base.ExitThreadCore();
        }
    }

    /// Hidden top-level window so a second launch (even non-elevated) can ask us to show the panel.
    sealed class MsgWindow : NativeWindow
    {
        readonly Action _show, _hotkey, _devChange;
        [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilterEx(IntPtr h, int msg, int action, IntPtr p);
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);

        public MsgWindow(Action show, Action hotkey, Action devChange) { _show = show; _hotkey = hotkey; _devChange = devChange; Recreate(); }

        public void Recreate()
        {
            if (Handle != IntPtr.Zero) return;
            CreateHandle(new CreateParams { Caption = Program.MsgWindowTitle });
            ChangeWindowMessageFilterEx(Handle, Program.ShowMessage, 1 /*MSGFLT_ALLOW*/, IntPtr.Zero);
            RegisterHotKey(Handle, 1, 0x1 | 0x2 | 0x4 | 0x4000 /*ALT|CTRL|SHIFT|NOREPEAT*/, 0x54 /*T*/);
        }

        public override void DestroyHandle()
        {
            if (Handle != IntPtr.Zero) UnregisterHotKey(Handle, 1);
            base.DestroyHandle();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Program.ShowMessage) { _show(); return; }
            if (m.Msg == 0x0312 /*WM_HOTKEY*/ && m.WParam == (IntPtr)1) { _hotkey(); return; }
            if (m.Msg == 0x0219 /*WM_DEVICECHANGE*/) _devChange();
            base.WndProc(ref m);
        }
    }

    static class Draw
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);

        public static Color ModeColor(PowerMode m) => m switch
        {
            PowerMode.Saver => Color.FromArgb(0x1F, 0x9D, 0x6B),
            PowerMode.Auto => Color.FromArgb(0x3B, 0x82, 0xF6),
            PowerMode.Performance => Color.FromArgb(0xF9, 0x73, 0x16),
            PowerMode.Geek => Color.FromArgb(0xA8, 0x55, 0xF7),
            _ => Color.Gray,
        };

        public static Icon TrayIcon(PowerMode m)
        {
            int sz = Math.Max(16, GetSystemMetrics(49 /*SM_CXSMICON*/));
            using var bmp = new Bitmap(sz, sz);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                using (var path = RoundRect(new RectangleF(0.5f, 0.5f, sz - 1, sz - 1), sz * 0.28f))
                using (var b = new SolidBrush(ModeColor(m)))
                    g.FillPath(b, path);
                using var f = new Font(Theme.IconFont, sz * 0.6f, FontStyle.Regular, GraphicsUnit.Pixel);
                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(Glyph.ForPower(m), f, Brushes.White, new RectangleF(0, sz * 0.04f, sz, sz), sf);
            }
            IntPtr h = bmp.GetHicon();
            var icon = (Icon)Icon.FromHandle(h).Clone();
            DestroyIcon(h);
            return icon;
        }

        public static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
