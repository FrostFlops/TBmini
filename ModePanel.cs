using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using static TBMini.L10n;

namespace TBMini
{
    /// Live state of one feature row, owned by TrayApp.
    sealed class FeatureState
    {
        public bool? Supported;      // null = not read yet
        public int Value = -1;
        public bool Busy;
        public string Unavailable;   // non-null: exists but not usable now (row disabled, this is the tooltip)
    }

    // ===================================================================================== theme
    sealed class Theme
    {
        public bool Dark;
        public Color Bg, Card, CardBorder, Divider, Fg, Sub, Ctl, CtlHover, Track, Accent, Disabled;
#if TBMINI_DEV
        public static bool? ForceDark;   // screenshot hook only
#endif

        public static string IconFont { get; } =
            new InstalledFontCollection().Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

        public static Theme Load()
        {
            bool dark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int v && v == 0;
#if TBMINI_DEV
            if (ForceDark.HasValue) dark = ForceDark.Value;
#endif
            var t = new Theme { Dark = dark };
            if (dark)
            {
                t.Bg = C(0x1F1F1F); t.Card = C(0x2A2A2A); t.CardBorder = C(0x363636); t.Divider = C(0x333333);
                t.Fg = C(0xF3F3F3); t.Sub = C(0x9D9D9D); t.Ctl = C(0x353535); t.CtlHover = C(0x404040);
                t.Track = C(0x1C1C1C); t.Disabled = C(0x666666);
            }
            else
            {
                t.Bg = C(0xF3F3F3); t.Card = C(0xFFFFFF); t.CardBorder = C(0xE3E3E3); t.Divider = C(0xEDEDED);
                t.Fg = C(0x1B1B1B); t.Sub = C(0x6B6B6B); t.Ctl = C(0xF2F2F2); t.CtlHover = C(0xE8E8E8);
                t.Track = C(0xEBEBEB); t.Disabled = C(0xA8A8A8);
            }
            var acc = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null);
            Color a = acc is int abgr ? Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF) : C(0x0F6CBD);
            t.Accent = dark ? Mix(a, Color.White, 0.25f) : a;
            return t;
        }

        static Color C(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

        public static Color Mix(Color a, Color b, float t) =>
            Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        /// White or near-black text, whichever reads better on c.
        public static Color On(Color c) => (c.R * 0.299 + c.G * 0.587 + c.B * 0.114) > 160 ? Color.FromArgb(0x1B, 0x1B, 0x1B) : Color.White;
    }

    static class Glyph
    {
        public const string Eco = "", Sparkle = "", Fast = "", Flame = "";
        public const string Hybrid = "", Chip = "", Monitor = "";
        public const string Battery = "", Charging = "", Plug = "", Keyboard = "", Palette = "", Speaker = "";
        public const string Chevron = "";

        public const string Game = "\uE7FC", Bolt = "\uE945";

        public static string ForPower(PowerMode m) => m switch
        {
            PowerMode.Saver => Eco, PowerMode.Auto => Sparkle, PowerMode.Performance => Fast, PowerMode.Geek => Flame, _ => "?",
        };

        /// Battery level glyphs E850 (empty) .. E859 (full)
        public static string BatteryLevel(float pct) => ((char)(0xE850 + Math.Max(0, Math.Min(9, (int)Math.Round(pct * 9))))).ToString();
    }

    // ===================================================================================== panel
    /// The popup shown when the tray icon is left-clicked.
    sealed class ModePanel : Form
    {
        static readonly PowerMode[] PowerModes = { PowerMode.Saver, PowerMode.Auto, PowerMode.Performance, PowerMode.Geek };
        static readonly GpuMode[] GpuModes = { GpuMode.Hybrid, GpuMode.IgpuOnly, GpuMode.Discrete };
        static readonly string[] GpuGlyphs = { Glyph.Hybrid, Glyph.Chip, Glyph.Monitor };
        static readonly Color[] GpuColors = { Color.FromArgb(0x3B, 0x82, 0xF6), Color.FromArgb(0x14, 0xB8, 0xA6), Color.FromArgb(0x76, 0xB9, 0x00) };
        // Body bands, top to bottom: left[i] and right[i] end on the same line (band 0 also holds the mode tiles).
        static readonly string[] LeftSections = { Sec.Battery, Sec.Power, Sec.App };
        static readonly string[] RightSections = { Sec.Keyboard, Sec.Display, Sec.Sound };

        readonly TrayApp _app;
        readonly float _s;
        Theme _t = Theme.Load();
        readonly Font _title, _sec, _row, _small, _tileText, _value;
        readonly ToolTip _tip = new ToolTip { InitialDelay = 350, AutoPopDelay = 15000 };

        readonly Label _hTitle;
        readonly Chip _chipPower, _chipGpu;
        readonly Metric _mCpu, _mGpu, _mTotal;
        readonly Timer _sensorTimer = new Timer { Interval = 1500 };
        bool _sensing;
        readonly Timer _anim = new Timer { Interval = 10 };   // pop-up: short slide + fade
        int _animFrom, _animTo, _animStart;
        const int AnimMs = 280, AnimRise = 40;
        IntPtr _powerNotify;
        readonly Label _secPerf, _secGpu;
        readonly Card _cardPerf, _cardGpu;
        readonly Tile[] _pt = new Tile[4];
        readonly Tile[] _gt = new Tile[3];
        readonly Label _status;
        readonly LinkLabel _link;
        readonly Dictionary<string, Row> _rows = new Dictionary<string, Row>();
        readonly Dictionary<string, (Label label, Card card)> _sections = new Dictionary<string, (Label, Card)>();

        bool _menuOpen, _anchorBottom;
        public int HiddenAt;   // Environment.TickCount when the panel last auto-hid

        sealed class Row
        {
            public Feature F;
            public Label Label;
            public Control Ctl;          // Toggle / Segmented / Drop
            public bool Shown;           // own flag: Control.Visible reads false while the form is hidden
            public string TipNow;        // tooltip currently set on the row
        }

        int S(float v) => (int)Math.Round(v * _s);
        const int ColW = 370, Pad = 16, Gap = 12, RowH = 40, TileH = 62;

        public ModePanel(TrayApp app)
        {
            _app = app;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            using (var g = CreateGraphics()) _s = g.DpiX / 96f;

            const string ui = "Microsoft YaHei UI";
            Font = new Font(ui, 9f);
            _title = new Font("Segoe UI Semibold", 12f);
            _sec = new Font(ui, 8.5f, FontStyle.Bold);
            _row = new Font(ui, 9f);
            _small = new Font(ui, 8.25f);
            _tileText = new Font(ui, 8.75f);
            _value = new Font("Segoe UI Semibold", 9.25f);
            Width = S(2 * ColW + Gap + 2 * Pad);

            _hTitle = new Label { Text = "TBMini", Font = _title, AutoSize = true };
            _chipPower = new Chip { Font = _small, Ui = _s };
            _chipGpu = new Chip { Font = _small, Ui = _s, Glyph = Glyph.Chip };
            Controls.AddRange(new Control[] { _hTitle, _chipPower, _chipGpu });
            _mCpu = new Metric { Font = _small, ValueFont = _value, Ui = _s, Glyph = Glyph.Chip, Label = "CPU", Warm = 80, Hot = 95 };
            _mGpu = new Metric { Font = _small, ValueFont = _value, Ui = _s, Glyph = Glyph.Game, Label = "GPU", Warm = 75, Hot = 87, ReserveTemp = true };
            _mTotal = new Metric { Font = _small, ValueFont = _value, Ui = _s, Glyph = Glyph.Bolt };
            Controls.AddRange(new Control[] { _mCpu, _mGpu, _mTotal });
            // with PawnIO usable, the CPU temperature will arrive: lay the header out for it from the start
            _mCpu.ReserveTemp = Program.IsAdmin && Sensors.PawnIOInstalled;
            _sensorTimer.Tick += (s, e) => PollSensors();
            _anim.Tick += (s, e) => AnimStep();
            SystemEvents.SessionSwitch += OnSessionSwitch;

            _secPerf = SectionLabel("");
            _cardPerf = new Card { Ui = _s };
            for (int i = 0; i < 4; i++)
            {
                var m = PowerModes[i];
                var t = new Tile { Glyph = Glyph.ForPower(m), Accent = Draw.ModeColor(m), Font = _tileText, Ui = _s };
                t.Click += (s, e) => _app.SetPower(m);
                _pt[i] = t; _cardPerf.Controls.Add(t);
            }
            _secGpu = SectionLabel("");
            _cardGpu = new Card { Ui = _s };
            for (int i = 0; i < 3; i++)
            {
                var g = GpuModes[i];
                var t = new Tile { Glyph = GpuGlyphs[i], Accent = GpuColors[i], Font = _tileText, Ui = _s };
                t.Click += (s, e) => _app.SetGpu(g);
                _gt[i] = t; _cardGpu.Controls.Add(t);
            }
            Controls.AddRange(new Control[] { _secPerf, _cardPerf, _secGpu, _cardGpu });

            _status = new Label { Font = _small, AutoSize = false };
            _link = new LinkLabel { Font = _small, AutoSize = true, Visible = false };
            _link.LinkClicked += (s, e) => { Hide(); _app.RestartNow(); };
            Controls.AddRange(new Control[] { _status, _link });

            Deactivate += (s, e) => { if (!_menuOpen) { HiddenAt = Environment.TickCount; Hide(); } };
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
            ApplyTexts();
            ApplyTheme();
            Relayout();
        }

        /// Fixed texts (feature rows carry their own, from the registry).
        void ApplyTexts()
        {
            _secPerf.Text = T("性能模式", "Performance mode");
            _secGpu.Text = T("显卡模式", "GPU mode");
            for (int i = 0; i < 4; i++) _pt[i].Text = Its.Name(PowerModes[i]);
            for (int i = 0; i < 3; i++) _gt[i].Text = Gpu.Name(GpuModes[i]);
            _tip.SetToolTip(_pt[0], T("省电：低功耗、安静", "Saver: low power, quiet"));
            _tip.SetToolTip(_pt[1], T("智能：按负载自动调节", "Auto: adjusts to the load"));
            _tip.SetToolTip(_pt[2], T("性能：高功耗释放", "Performance: higher power limits"));
            _tip.SetToolTip(_gt[0], T("混合：内屏走核显，独显按需工作（切入/切出独显直连要重启）",
                                      "Hybrid: the display runs on the iGPU and the dGPU works on demand (switching to or from dGPU direct needs a restart)"));
            _tip.SetToolTip(_gt[1], T("集显：混合模式下关掉独显，不用重启；独显被占用时不会生效",
                                      "iGPU only: turns the dGPU off within Hybrid, no restart; does not apply while an app is using the dGPU"));
            _tip.SetToolTip(_gt[2], T("独显直连：内屏直接接独显（MUX），需要重启", "dGPU direct: the display is wired straight to the dGPU (MUX); needs a restart"));
            _tip.SetToolTip(_mCpu, T("CPU 封装温度和功耗。温度通过 PawnIO 驱动读取（需安装 PawnIO 并以管理员身份运行）",
                                     "CPU package temperature and power. The temperature is read through the PawnIO driver (needs PawnIO and administrator rights)"));
            _tip.SetToolTip(_mGpu, T("独显温度和功耗（NVIDIA 驱动）。独显休眠时不读取，以免把它唤醒",
                                     "dGPU temperature and power (NVIDIA driver). Not read while the dGPU sleeps, so it is never woken up"));
            _mGpu.Templates = new[] { "188 W", T("休眠", "Asleep"), T("已断开", "Off") };
            if (_last == null) _mTotal.Label = T("整机", "System");
            else _mTotal.Label = _last.Total == TotalSource.CpuGpu ? "CPU+GPU" : T("整机", "System");
            _link.Text = T("立即重启", "Restart now");
            _totalTip = null;
        }

        /// Language changed: re-create the feature rows from the rebuilt registry and refresh every text.
        public void Relocalize()
        {
            if (IsDisposed) return;
            SuspendLayout();
            foreach (var r in _rows.Values) { _tip.SetToolTip(r.Label, null); _tip.SetToolTip(r.Ctl, null); }
            foreach (var (label, card) in _sections.Values) { Controls.Remove(label); Controls.Remove(card); label.Dispose(); card.Dispose(); }
            _sections.Clear();
            _rows.Clear();
            ApplyTexts();
            ResumeLayout(false);
            ApplyTheme();
            UpdateView();
            Relayout();
            if (_last != null) ShowSensors(_last);
        }

        // ------------------------------------------------------------------ sensors (only while visible)
        SensorData _last;
        string _totalTip;

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) { Sensors.Begin(); _sensorTimer.Start(); PollSensors(); }
            else
            {
                _sensorTimer.Stop(); Sensors.End();
                _chargeW = double.NaN;          // stale once hidden; the next poll brings a fresh value
                if (_anim.Enabled) { _anim.Stop(); Top = _animTo; }
                if (Opacity < 1) Opacity = 1;
            }
        }

        void HideNow()
        {
            if (!Visible) return;
            HiddenAt = Environment.TickCount;
            Hide();
        }

        // Never leave the monitor running unattended: hide on lock, on display off and before sleep.
        void OnSessionSwitch(object s, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.ConsoleDisconnect || e.Reason == SessionSwitchReason.RemoteDisconnect)
                if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(HideNow));
        }

        static Guid DisplayStatusGuid = new Guid("2B84C20E-AD23-4ddf-93DB-05FFBD7EFCA5");   // GUID_SESSION_DISPLAY_STATUS
        [DllImport("user32.dll")] static extern IntPtr RegisterPowerSettingNotification(IntPtr h, ref Guid setting, int flags);
        [DllImport("user32.dll")] static extern bool UnregisterPowerSettingNotification(IntPtr h);

        protected override void WndProc(ref Message m)
        {
            const int WM_POWERBROADCAST = 0x218, PBT_APMSUSPEND = 0x4, PBT_POWERSETTINGCHANGE = 0x8013;
            if (m.Msg == WM_POWERBROADCAST)
            {
                int ev = (int)(long)m.WParam;
                if (ev == PBT_APMSUSPEND) HideNow();
                else if (ev == PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero
                         && Marshal.PtrToStructure<Guid>(m.LParam) == DisplayStatusGuid
                         && Marshal.ReadInt32(m.LParam, 20) == 0)      // POWERBROADCAST_SETTING.Data: 0 = display off
                    HideNow();
            }
            base.WndProc(ref m);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (_powerNotify != IntPtr.Zero) { UnregisterPowerSettingNotification(_powerNotify); _powerNotify = IntPtr.Zero; }
            base.OnHandleDestroyed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { SystemEvents.SessionSwitch -= OnSessionSwitch; _anim.Dispose(); _sensorTimer.Dispose(); }
            base.Dispose(disposing);
        }

        void PollSensors()
        {
            if (_sensing || !Visible) return;
            _sensing = true;
            Task.Run(Sensors.Read).ContinueWith(t =>
            {
                _sensing = false;
                if (IsDisposed || !Visible || t.Status != TaskStatus.RanToCompletion) return;
                _last = t.Result;
                ShowSensors(_last);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        double _chargeW = double.NaN;   // battery charge rate from the last poll (only while the panel is open)

        /// "接电源 · 82% · +45 W" while charging, "接电源 · 100%" when full or held, "电池 · 85%" on battery.
        /// Returns true when the chip's width changed.
        bool UpdatePowerChip()
        {
            var ps = SystemInformation.PowerStatus;
            bool ac = _app.OnAc;
            string pct = ps.BatteryLifePercent <= 1f ? $"{(int)Math.Round(ps.BatteryLifePercent * 100)}%" : "";
            string text = (ac ? T("接电源", "AC") : T("电池", "Battery")) + (pct != "" ? " · " + pct : "");
            bool charging = ac && !double.IsNaN(_chargeW) && _chargeW >= 0.5;
            if (charging) text += " · +" + (_chargeW < 10 ? $"{_chargeW:0.0}" : $"{_chargeW:0}") + " W";
            string glyph = ac ? Glyph.Charging : Glyph.BatteryLevel(ps.BatteryLifePercent);
            string tip = charging ? T("正在充电：+W 是充进电池的功率，不是整机功耗", "Charging: +W is the power going into the battery, not the laptop's total draw") : null;
            if (tip != _powerTip) { _powerTip = tip; _tip.SetToolTip(_chipPower, tip); }
            if (text == _chipPower.Text && glyph == _chipPower.Glyph) return false;
            int before = _chipPower.PreferredWidth;
            _chipPower.Text = text; _chipPower.Glyph = glyph;
            _chipPower.Invalidate();
            return _chipPower.PreferredWidth != before;
        }

        string _powerTip;

        static string Deg(double v) => double.IsNaN(v) ? "" : $"{v:0}°C";
        static string Watt(double v) => double.IsNaN(v) ? "—" : v < 10 ? $"{v:0.0} W" : $"{v:0} W";

        void ShowSensors(SensorData d)
        {
            bool relayout = false;
            // reserve room for a temperature once one has been seen, so the bubbles do not change width every tick
            if (!double.IsNaN(d.CpuTemp) && !_mCpu.ReserveTemp) { _mCpu.ReserveTemp = true; relayout = true; }
            _mCpu.Set(d.CpuTemp, Deg(d.CpuTemp), Watt(d.CpuW), false);
            switch (d.Gpu)
            {
                case GpuPower.Active: _mGpu.Set(d.GpuTemp, Deg(d.GpuTemp), Watt(d.GpuW), false); break;
                case GpuPower.Asleep: _mGpu.Set(double.NaN, "", T("休眠", "Asleep"), true); break;
                case GpuPower.Off: _mGpu.Set(double.NaN, "", T("已断开", "Off"), true); break;
                default: _mGpu.Set(double.NaN, "", "—", true); break;
            }
            if (d.Total != TotalSource.None)
            {
                string label = d.Total == TotalSource.CpuGpu ? "CPU+GPU" : T("整机", "System");
                if (_mTotal.Label != label) { _mTotal.Label = label; _mTotal.Invalidate(); relayout = true; }
            }
            _mTotal.Set(double.NaN, "", Watt(d.TotalW), false);
            _chargeW = d.ChargeW;
            if (UpdatePowerChip()) relayout = true;
            string tip = (d.Total switch
            {
                TotalSource.Platform => T("整机功耗：CPU 的平台能量计数器（PSys），通过 PawnIO 读取",
                                          "Whole-system power: the CPU's platform energy counter (PSys), read through PawnIO"),
                TotalSource.Battery => T("整机功耗（电池放电功率）", "Whole-system power (battery discharge rate)"),
                TotalSource.CpuGpu => T("接电源时读不到整机功耗（需要 PawnIO，且主板接了 PSys），这里是 CPU 与独显功耗之和。拔掉电源后显示整机功耗。",
                                        "Whole-system power is not readable on AC here (needs PawnIO and a board that wires up PSys), so this is CPU plus dGPU power. Unplug to see whole-system power."),
                _ => T("整机功耗", "Whole-system power"),
            });
            if (tip != _totalTip) { _totalTip = tip; _tip.SetToolTip(_mTotal, tip); }   // re-setting resets a showing tooltip
            if (relayout) LayoutHeader();
        }

        Label SectionLabel(string text) => new Label { Text = text, Font = _sec, AutoSize = true };

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80;                                 // WS_EX_TOOLWINDOW
                if (!Win11) cp.ClassStyle |= 0x20000;               // CS_DROPSHADOW (Win11 gets DWM corners+border)
                return cp;
            }
        }

        static bool Win11 => Environment.OSVersion.Version.Build >= 22000;
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

        void ApplyDwm()
        {
            if (!Win11 || !IsHandleCreated) return;
            int round = 2, dark = _t.Dark ? 1 : 0;
            int border = _t.CardBorder.R | (_t.CardBorder.G << 8) | (_t.CardBorder.B << 16);
            DwmSetWindowAttribute(Handle, 33 /*WINDOW_CORNER_PREFERENCE*/, ref round, 4);
            DwmSetWindowAttribute(Handle, 20 /*USE_IMMERSIVE_DARK_MODE*/, ref dark, 4);
            DwmSetWindowAttribute(Handle, 34 /*BORDER_COLOR*/, ref border, 4);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDwm();
            _powerNotify = RegisterPowerSettingNotification(Handle, ref DisplayStatusGuid, 0 /*DEVICE_NOTIFY_WINDOW_HANDLE*/);
        }

        // ------------------------------------------------------------------ feature rows
        Row EnsureRow(Feature f)
        {
            if (_rows.TryGetValue(f.Id, out var r)) return r;
            if (!_sections.TryGetValue(f.Section, out var sec))
            {
                sec = (SectionLabel(Sec.Title(f.Section)), new Card { Ui = _s });
                _sections[f.Section] = sec;
                Controls.Add(sec.label); Controls.Add(sec.card);
            }
            r = new Row { F = f, Label = new Label { Text = f.Label, Font = _row, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft } };
            if (f.Tip != null) _tip.SetToolTip(r.Label, f.Tip);
            sec.card.Controls.Add(r.Label);
            _rows[f.Id] = r;
            BuildControl(r);
            ApplyTheme();
            return r;
        }

        void BuildControl(Row r)
        {
            var card = _sections[r.F.Section].card;
            if (r.Ctl != null) { _tip.SetToolTip(r.Ctl, null); card.Controls.Remove(r.Ctl); r.Ctl.Dispose(); }
            r.TipNow = null;                 // the next UpdateView sets the right tooltip on the new control
            var f = r.F;
            if (f.Kind == FeatureKind.Toggle)
            {
                var t = new Toggle { Ui = _s };
                t.Click += (s, e) => _app.SetFeature(f, -2);
                r.Ctl = t;
            }
            else if (f.Options.Length <= 4)
            {
                var seg = new Segmented { Options = f.Options, Font = _small, Ui = _s };
                seg.Picked += i => _app.SetFeature(f, i);
                r.Ctl = seg;
            }
            else
            {
                var d = new Drop { Font = _small, Ui = _s };
                d.Click += (s, e) => ShowChoiceMenu(f, d);
                r.Ctl = d;
            }
            if (f.Tip != null) _tip.SetToolTip(r.Ctl, f.Tip);
            card.Controls.Add(r.Ctl);
        }

        void ShowChoiceMenu(Feature f, Control anchor)
        {
            var st = _app.FeatureStateOf(f);
            var m = new ContextMenuStrip { Font = _row, ShowImageMargin = false, ShowCheckMargin = true };
            if (_t.Dark) m.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors(_t));
            for (int i = 0; i < f.Options.Length; i++)
            {
                int ii = i;
                var it = new ToolStripMenuItem(f.Options[i]) { Checked = st.Value == i, Enabled = !st.Busy, ForeColor = _t.Fg };
                it.Click += (s, e) => _app.SetFeature(f, ii);
                m.Items.Add(it);
            }
            _menuOpen = true;
            m.Closed += (s, e) =>
            {
                _menuOpen = false;
                var reason = e.CloseReason;
                BeginInvoke(new Action(() =>
                {
                    if (reason == ToolStripDropDownCloseReason.AppFocusChange ||
                        (reason == ToolStripDropDownCloseReason.AppClicked && Form.ActiveForm != this))
                    { HiddenAt = Environment.TickCount; Hide(); }
                    else if (!ContainsFocus) Activate();
                    m.Dispose();
                }));
            };
            m.Show(anchor, new Point(anchor.Width - m.PreferredSize.Width, anchor.Height + S(2)));
        }

        // ------------------------------------------------------------------ layout
        void Relayout()
        {
            AutoScrollPosition = new Point(0, 0);   // positions below assume no scroll offset
            int pad = S(Pad), colW = S(ColW), gap = S(Gap);
            int x0 = pad, x1 = pad + colW + gap;

            // header: title, live readings and status chips share one row
            LayoutHeader();
            int top = HeaderY + S(HeaderH) + S(16);

            // Body: three bands, each pairing a left and a right block that end on the same line:
            //   性能 + 显卡 + 电池 | 键盘,   电源 | 显示,   应用 | 声音
            foreach (var (label, card) in _sections.Values) { label.Visible = card.Visible = false; }
            int y = top, bottom = top;
            for (int band = 0; band < LeftSections.Length; band++)
            {
                var left = new List<Block>();
                if (band == 0) { left.Add(TileBlock(_secPerf, _cardPerf, _pt)); left.Add(TileBlock(_secGpu, _cardGpu, _gt)); }
                var lb = SectionBlock(LeftSections[band]); if (lb != null) left.Add(lb);
                var right = new List<Block>();
                var rb = SectionBlock(RightSections[band]); if (rb != null) right.Add(rb);
                if (left.Count + right.Count == 0) continue;
                int bandH = Math.Max(ColumnHeight(left), ColumnHeight(right));
                PlaceColumn(left, x0, y, bandH, colW);
                PlaceColumn(right, x1, y, bandH, colW);
                bottom = y + bandH;
                y = bottom + S(14);
            }
            bottom += S(14);
            _status.Bounds = new Rectangle(x0 + S(2), bottom - S(4), Width - 2 * pad, S(22));
            int h = _status.Bottom + S(10);

            // during the slide-in the window is briefly outside its target (possibly over a monitor above): use the target
            var wa = Screen.FromPoint(Visible ? new Point(Left, _anim.Enabled ? _animTo : Top) : Cursor.Position).WorkingArea;
            int max = wa.Height - S(16);
            if (h > max) { AutoScroll = true; AutoScrollMinSize = new Size(0, h); Height = max; }
            else { AutoScroll = false; Height = h; }
            if (Visible)
            {
                int m = S(8);
                int cur = _anim.Enabled ? _animTo : Top;
                int t = _anchorBottom ? wa.Bottom - m - Height : Math.Min(cur, wa.Bottom - m - Height);
                t = Math.Max(wa.Top + m, t);
                if (_anim.Enabled) { _animFrom += t - _animTo; _animTo = t; } else Top = t;
            }
            Invalidate();
        }

        /// One titled card in a column: either mode tiles or a feature section with visible rows.
        sealed class Block
        {
            public Label Title; public Card Card;
            public Tile[] Tiles;               // tile block
            public List<Row> Rows;             // feature section block
            public int CardH;                  // natural card height
        }

        Block TileBlock(Label title, Card card, Tile[] tiles) =>
            new Block { Title = title, Card = card, Tiles = tiles, CardH = S(TileH) + 2 * S(8) };

        /// The section's visible rows, or null when none is supported.
        Block SectionBlock(string section)
        {
            if (!_sections.TryGetValue(section, out var sec)) return null;
            var rows = FeatureRegistry.All.Where(f => f.Section == section && _rows.ContainsKey(f.Id)).Select(f => _rows[f.Id]).ToList();
            foreach (var r in rows)
            {
                r.Shown = _app.FeatureStateOf(r.F).Supported == true;
                r.Label.Visible = r.Shown; r.Ctl.Visible = r.Shown;
            }
            var shown = rows.Where(r => r.Shown).ToList();
            if (shown.Count == 0) return null;
            return new Block { Title = sec.label, Card = sec.card, Rows = shown, CardH = shown.Count * S(RowH) };
        }

        int ColumnHeight(List<Block> blocks) =>
            blocks.Count == 0 ? 0 : blocks.Sum(b => S(24) + b.CardH) + S(14) * (blocks.Count - 1);

        /// Places a column's blocks from y so that the last one ends at y + bandH: extra height goes into the gaps
        /// between blocks, or, for a single feature section, into slightly taller rows (at most +50 %).
        void PlaceColumn(List<Block> blocks, int x, int y, int bandH, int colW)
        {
            if (blocks.Count == 0) return;
            int extra = bandH - ColumnHeight(blocks);
            int stretch = blocks.Count == 1 && blocks[0].Rows != null
                ? Math.Min(extra, blocks[0].Rows.Count * S(RowH) / 2) : 0;
            for (int i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];
                if (i > 0 && blocks.Count > 1) y += extra / (blocks.Count - 1) + (i == blocks.Count - 1 ? extra % (blocks.Count - 1) : 0);
                b.Title.Location = new Point(x + S(2), y);
                b.Title.Visible = true;
                int cardY = y + S(24);
                if (b.Tiles != null) PlaceTiles(b.Card, b.Tiles, x, cardY, colW);
                else PlaceRows(b, x, cardY, colW, b.CardH + stretch);
                b.Card.Visible = true;
                y = b.Card.Bottom + S(14);
            }
        }

        void PlaceRows(Block b, int x, int y, int colW, int cardH)
        {
            int n = b.Rows.Count, inX = S(14);
            b.Card.Bounds = new Rectangle(x, y, colW, cardH);
            var dividers = new List<int>();
            for (int i = 0; i < n; i++)
            {
                var r = b.Rows[i];
                int ry = (int)Math.Round((double)i * cardH / n), rh = (int)Math.Round((double)(i + 1) * cardH / n) - ry;
                if (i > 0) dividers.Add(ry);
                Size cs = r.Ctl is Toggle ? new Size(S(40), S(20)) : new Size(S(200), S(28));
                if (r.Ctl is Segmented sg && sg.Options.Length <= 2) cs.Width = S(150);
                r.Ctl.Bounds = new Rectangle(colW - inX - cs.Width, ry + (rh - cs.Height) / 2, cs.Width, cs.Height);
                r.Label.Bounds = new Rectangle(inX, ry + 1, r.Ctl.Left - inX - S(8), rh - 2);   // keep dividers visible
            }
            b.Card.Dividers = dividers.ToArray();
            b.Card.Invalidate();
        }

        const int HeaderH = 26;
        int HeaderY => S(14);

        /// Header row, right to left: GPU-mode chip, power chip, total, GPU and CPU readings, then the title.
        /// If it does not fit (long English texts, large fonts), the title goes first (the reading labels carry
        /// meaning, e.g. "CPU+GPU" vs whole system), then the labels.
        void LayoutHeader()
        {
            int pad = S(Pad), h = S(HeaderH), cy = HeaderY, g = S(6);
            _chipGpu.Size = new Size(_chipGpu.PreferredWidth, h);
            _chipPower.Size = new Size(_chipPower.PreferredWidth, h);
            _chipGpu.Location = new Point(Width - pad - _chipGpu.Width, cy);
            _chipPower.Location = new Point(_chipGpu.Left - g - _chipPower.Width, cy);

            var ms = new[] { _mCpu, _mGpu, _mTotal };
            int Need(bool labels)
            {
                foreach (var m in ms) m.ShowLabel = labels;
                return ms.Sum(m => m.PreferredWidth) + g * (ms.Length - 1);
            }
            int right = _chipPower.Left - g;
            int withTitle = right - (pad + _hTitle.PreferredSize.Width + S(10)), noTitle = right - pad;
            bool title;
            if (Need(true) <= withTitle) title = true;
            else if (Need(true) <= noTitle) title = false;
            else title = Need(false) <= withTitle;
            // without the title, the readings share the leftover width so the row spans the panel evenly
            int spare = title ? 0 : Math.Max(0, noTitle - (ms.Sum(m => m.PreferredWidth) + g * (ms.Length - 1)));
            int x = right;
            for (int i = ms.Length - 1; i >= 0; i--)
            {
                int w = ms[i].PreferredWidth + spare / ms.Length + (i == 0 ? spare % ms.Length : 0);
                ms[i].Bounds = new Rectangle(x - w, cy, w, h);
                ms[i].Invalidate();
                x -= w + g;
            }
            _hTitle.Visible = title;
            _hTitle.Location = new Point(pad - S(1), cy + (h - _hTitle.PreferredSize.Height) / 2);
        }

        void PlaceTiles(Card card, Tile[] tiles, int x, int y, int w)
        {
            int inset = S(8), gap = S(6), n = tiles.Length;
            card.Bounds = new Rectangle(x, y, w, S(TileH) + 2 * inset);
            int tw = (w - 2 * inset - (n - 1) * gap) / n;
            for (int i = 0; i < n; i++) tiles[i].Bounds = new Rectangle(inset + i * (tw + gap), inset, tw, S(TileH));
        }

        // ------------------------------------------------------------------ theme
        void ApplyTheme()
        {
            _t = Theme.Load();
            BackColor = _t.Bg;
            _hTitle.ForeColor = _t.Fg; _hTitle.BackColor = _t.Bg;
            foreach (var c in new Owner[] { _chipPower, _chipGpu, _mCpu, _mGpu, _mTotal }) c.T = _t;
            foreach (var l in new[] { _secPerf, _secGpu, _status }) { l.ForeColor = _t.Sub; l.BackColor = _t.Bg; }
            foreach (var c in new[] { _cardPerf, _cardGpu }) c.T = _t;
            foreach (var t in _pt.Concat(_gt)) t.T = _t;
            foreach (var (label, card) in _sections.Values) { label.ForeColor = _t.Sub; label.BackColor = _t.Bg; card.T = _t; }
            foreach (var r in _rows.Values)
            {
                r.Label.ForeColor = _t.Fg; r.Label.BackColor = _t.Card;
                if (r.Ctl is IThemed th) th.T = _t;
            }
            _link.BackColor = _t.Bg;
            _link.LinkColor = _link.ActiveLinkColor = _t.Accent;
            ApplyDwm();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Win11) return;                                    // DWM draws the rounded border
            using var p = new Pen(_t.CardBorder);
            e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }

        // ------------------------------------------------------------------ state -> view
        public void UpdateView()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(UpdateView)); return; }

            bool busy = _app.Busy;
            var power = _app.Power;
            bool geekOk = Its.GeekAvailable;
            for (int i = 0; i < 4; i++)
            {
                _pt[i].Selected = power == PowerModes[i];
                _pt[i].Enabled = !busy && (PowerModes[i] != PowerMode.Geek || geekOk);
                _pt[i].Invalidate();
            }
            _tip.SetToolTip(_pt[3], geekOk ? T("极客：最高功耗（仅接电源）", "Geek: maximum power (AC only)") : T("极客模式只能在接电源时使用", "Geek mode is only available on AC power"));

            var gi = _app.GpuInfo;
            for (int i = 0; i < 3; i++)
            {
                _gt[i].Selected = gi.Mode == GpuModes[i];
                _gt[i].Enabled = !busy;
                _gt[i].Invalidate();
            }

            // header chips
            UpdatePowerChip();
            string pend = _app.PendingText();
            _chipGpu.Text = _app.GpuText() + (pend != null ? " → " + pend : "");
            int gIdx = Array.IndexOf(GpuModes, gi.Mode);
            _chipGpu.Glyph = gIdx >= 0 ? GpuGlyphs[gIdx] : Glyph.Chip;
            _chipPower.Invalidate(); _chipGpu.Invalidate();

            // feature rows
            bool layout = false;
            foreach (var f in FeatureRegistry.All)
            {
                var st = _app.FeatureStateOf(f);
                if (st.Supported != true) { if (_rows.TryGetValue(f.Id, out var gone) && gone.Shown) layout = true; continue; }
                bool isNew = !_rows.ContainsKey(f.Id);
                var r = EnsureRow(f);
                if (isNew || !r.Shown) layout = true;
                bool wantSeg = f.Kind == FeatureKind.Choice && f.Options.Length <= 4;
                if (f.Kind == FeatureKind.Choice && (wantSeg != (r.Ctl is Segmented) || (r.Ctl is Segmented sg0 && sg0.Options.Length != f.Options.Length)))
                { BuildControl(r); ApplyTheme(); layout = true; }

                bool usable = !st.Busy && st.Unavailable == null;
                switch (r.Ctl)
                {
                    case Toggle t: t.On = st.Value == 1; t.Unknown = st.Value < 0; t.Enabled = usable; break;
                    case Segmented sg: sg.Selected = st.Value; sg.Enabled = usable; break;
                    case Drop d: d.Text = st.Value >= 0 && st.Value < f.Options.Length ? f.Options[st.Value] : "—"; d.Enabled = usable; break;
                }
                // exists but unusable right now (e.g. 超级分辨率 in dGPU direct): greyed out, the reason as tooltip
                r.Label.ForeColor = st.Unavailable != null ? _t.Disabled : _t.Fg;
                string tip = st.Unavailable ?? f.Tip;
                if (tip != r.TipNow) { r.TipNow = tip; _tip.SetToolTip(r.Label, tip); _tip.SetToolTip(r.Ctl, tip); }
                r.Ctl.Invalidate();
            }
            if (layout) Relayout();
            else LayoutHeader();   // chips may have changed width

            string line = "";
            if (!Its.Supported) line = T("没找到联想 ITS 服务，性能模式不可用", "Lenovo ITS service not found; performance modes are unavailable");
            if (busy) line = Join(line, T("切换中…", "Switching…"));
            if (!Program.IsAdmin) line = Join(line, T("显卡和 BIOS 项需要管理员权限（点显卡按钮可提权）", "GPU and BIOS settings need administrator rights (click a GPU button to elevate)"));
            else if (gi.Error != null) line = Join(line, T("显卡：", "GPU: ") + gi.Error);
            else if (!gi.RunningKnown) line = Join(line, T("内屏未点亮，显卡按 BIOS 显示", "Internal display off; GPU mode shown from BIOS"));
            if (pend != null) line = Join(line, T("重启后显卡切到「" + pend + "」", "GPU switches to " + pend + " after a restart"));
            if (line == "") line = T("Ctrl+Alt+Shift+T 随时重新打开触摸板", "Ctrl+Alt+Shift+T turns the touchpad back on at any time");
            _status.Text = line;

            _link.Visible = pend != null;
            if (pend != null)
            {
                var sz = TextRenderer.MeasureText(line, _status.Font);
                _link.Location = new Point(Math.Min(_status.Left + sz.Width + S(6), Width - S(Pad) - _link.Width), _status.Top);
                _link.BringToFront();
            }
        }

        static string Join(string a, string b) => a == "" ? b : a + " · " + b;

        public void ShowNearCursor()
        {
            ApplyTheme();
            UpdateView();
            Relayout();
            var c = Cursor.Position;
            var wa = Screen.FromPoint(c).WorkingArea;
            int m = S(8);
            int x = Math.Max(wa.Left + m, Math.Min(c.X - Width / 2, wa.Right - Width - m));
            _anchorBottom = c.Y >= wa.Top + wa.Height / 2;
            int y = Math.Max(wa.Top + m, _anchorBottom ? wa.Bottom - Height - m : wa.Top + m);
            if (AnimationsEnabled())
            {   // rise from the taskbar side and fade in, like the Windows 11 flyouts
                _animTo = y;
                _animFrom = y + (_anchorBottom ? S(AnimRise) : -S(AnimRise));
                Opacity = 0;
                Location = new Point(x, _animFrom);
                _animStart = Environment.TickCount;
                _anim.Start();
            }
            else Location = new Point(x, y);
            Show();
            Activate();
            SetForegroundWindow(Handle);
        }

        void AnimStep()
        {
            double t = Math.Min(1.0, (Environment.TickCount - _animStart) / (double)AnimMs);
            double e = 1 - Math.Pow(1 - t, 3);                      // position: ease-out cubic
            Top = (int)Math.Round(_animFrom + (_animTo - _animFrom) * e);
            if (t >= 1) { _anim.Stop(); Top = _animTo; Opacity = 1; }   // back to a normal (non-layered) window
            else Opacity = Math.Max(0.01, 1 - (1 - t) * (1 - t));   // fade: ease-out quadratic, spans the whole rise
        }

        /// Honours "Animation effects" in Windows settings (SPI_GETCLIENTAREAANIMATION).
        static bool AnimationsEnabled()
        {
            bool on = true;
            try { SystemParametersInfo(0x1042, 0, ref on, 0); } catch { }
            return on;
        }

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, ref bool value, uint winIni);

#if TBMINI_DEV
        /// Test hook: draw the panel (current state) into a PNG without showing it on screen.
        public void RenderTo(string path)
        {
            ApplyTheme();
            UpdateView();
            Relayout();
            Location = new Point(-20000, -20000);
            _sensing = true;                       // no async poll racing the synchronous one below
            Show();
            Sensors.Begin();
            Sensors.Read();
            System.Threading.Thread.Sleep(1000);
            var d = Sensors.Read();
            if (Environment.GetEnvironmentVariable("TBMINI_FAKE_TEMPS") == "1")   // layout preview of the admin+PawnIO state
            {
                if (double.IsNaN(d.CpuTemp)) d.CpuTemp = 67;
                if (double.IsNaN(d.ChargeW)) d.ChargeW = 45;
            }
            ShowSensors(d);
            Application.DoEvents();
            using var bmp = new Bitmap(Width, Height);
            DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
            Hide();
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
#endif
    }

    // ===================================================================================== controls
    interface IThemed { Theme T { get; set; } }

    abstract class Owner : Control, IThemed
    {
        public Theme T { get; set; } = Theme.Load();
        public float Ui = 1f;
        protected bool Hover;
        MouseButtons _down;

        protected Owner()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
        }

        protected int S(float v) => (int)Math.Round(v * Ui);
        protected override void OnMouseEnter(EventArgs e) { Hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = e.Button; base.OnMouseDown(e); }
        protected override void OnClick(EventArgs e) { if (_down == MouseButtons.Left) base.OnClick(e); }

        protected Graphics Prep(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Parent?.BackColor ?? T.Bg);
            return g;
        }

        protected static void Fill(Graphics g, RectangleF r, float radius, Color c)
        {
            using var p = Draw.RoundRect(r, radius);
            using var b = new SolidBrush(c);
            g.FillPath(b, p);
        }

        protected static void Stroke(Graphics g, RectangleF r, float radius, Color c, float w = 1f)
        {
            using var p = Draw.RoundRect(r, radius);
            using var pen = new Pen(c, w);
            g.DrawPath(pen, p);
        }

        protected void DrawGlyph(Graphics g, string glyph, float size, Rectangle r, Color c)
        {
            using var f = new Font(Theme.IconFont, size * Ui, GraphicsUnit.Pixel);
            TextRenderer.DrawText(g, glyph, f, r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    /// Rounded settings card; draws row dividers.
    sealed class Card : Panel, IThemed
    {
        Theme _t = Theme.Load();
        public Theme T { get => _t; set { _t = value; BackColor = value.Card; Invalidate(); } }
        public float Ui = 1f;
        public int[] Dividers = new int[0];

        public Card()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = _t.Card;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? _t.Bg);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var p = Draw.RoundRect(r, 8 * Ui))
            {
                using (var b = new SolidBrush(_t.Card)) g.FillPath(b, p);
                using (var pen = new Pen(_t.CardBorder)) g.DrawPath(pen, p);
            }
            using var dp = new Pen(_t.Divider);
            foreach (int y in Dividers) g.DrawLine(dp, 14 * Ui, y, Width - 14 * Ui, y);
        }
    }

    sealed class Tile : Owner
    {
        public string Glyph;
        public Color Accent = Color.SteelBlue;
        public bool Selected;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prep(e);
            Color bg = Selected ? Accent : Hover && Enabled ? T.CtlHover : T.Ctl;
            Color fg = !Enabled && !Selected ? T.Disabled : Selected ? Theme.On(Accent) : T.Fg;
            Fill(g, new RectangleF(0, 0, Width, Height), 6 * Ui, bg);
            if (!Selected) Stroke(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 6 * Ui, Theme.Mix(T.CardBorder, bg, 0.3f));
            int gh = S(26);
            DrawGlyph(g, Glyph, 18, new Rectangle(0, S(9), Width, gh), Selected ? fg : (Enabled ? Accent : T.Disabled));
            // shrink long labels (e.g. English "Performance") to fit instead of cutting them off
            var font = Font;
            int avail = Width - S(8), tw = TextRenderer.MeasureText(g, Text, font).Width;
            if (tw > avail) font = new Font(Font.FontFamily, Math.Max(7f, Font.Size * avail / tw), Font.Style);
            TextRenderer.DrawText(g, Text, font, new Rectangle(0, S(9) + gh, Width, Height - S(9) - gh - S(4)), fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            if (!ReferenceEquals(font, Font)) font.Dispose();
        }
    }

    sealed class Segmented : Owner
    {
        public string[] Options = new string[0];
        public int Selected = -1;
        public event Action<int> Picked;
        int _hoverIdx = -1;

        int IndexAt(int x) => Options.Length == 0 ? -1 : Math.Max(0, Math.Min(Options.Length - 1, x * Options.Length / Math.Max(1, Width)));

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = IndexAt(e.X);
            if (i != _hoverIdx) { _hoverIdx = i; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hoverIdx = -1; base.OnMouseLeave(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && Enabled && ClientRectangle.Contains(e.Location)) Picked?.Invoke(IndexAt(e.X));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prep(e);
            float r = Height / 2f;
            Fill(g, new RectangleF(0, 0, Width, Height), r, T.Track);
            int n = Math.Max(1, Options.Length);
            float w = (float)Width / n, inset = 2.5f * Ui;
            for (int i = 0; i < Options.Length; i++)
            {
                var cell = new RectangleF(i * w + inset, inset, w - 2 * inset, Height - 2 * inset);
                bool sel = i == Selected;
                if (sel) Fill(g, cell, cell.Height / 2f, Enabled ? T.Accent : T.Disabled);
                else if (i == _hoverIdx && Enabled) Fill(g, cell, cell.Height / 2f, T.CtlHover);
                Color fg = sel ? Theme.On(T.Accent) : Enabled ? T.Fg : T.Disabled;
                TextRenderer.DrawText(g, Options[i], Font, Rectangle.Round(cell), fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }
    }

    sealed class Toggle : Owner
    {
        public bool On, Unknown;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prep(e);
            var r = new RectangleF(1, 1, Width - 2, Height - 2);
            float knob;
            if (On && !Unknown)
            {
                Color c = Enabled ? (Hover ? Theme.Mix(T.Accent, Color.White, 0.08f) : T.Accent) : T.Disabled;
                Fill(g, r, r.Height / 2, c);
                knob = r.Height - 8 * Ui;
                using var kb = new SolidBrush(Theme.On(c));
                g.FillEllipse(kb, r.Right - 4 * Ui - knob, r.Y + (r.Height - knob) / 2, knob, knob);
            }
            else
            {
                if (Hover && Enabled) Fill(g, r, r.Height / 2, T.CtlHover);
                Stroke(g, r, r.Height / 2, Enabled ? T.Sub : T.Disabled, 1.2f * Ui);
                knob = r.Height - 10 * Ui;
                using var kb = new SolidBrush(Enabled ? T.Sub : T.Disabled);
                g.FillEllipse(kb, r.X + 5 * Ui, r.Y + (r.Height - knob) / 2, knob, knob);
            }
        }
    }

    sealed class Drop : Owner
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prep(e);
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Fill(g, r, 5 * Ui, Hover && Enabled ? T.CtlHover : T.Ctl);
            Stroke(g, r, 5 * Ui, T.CardBorder);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(S(10), 0, Width - S(34), Height), Enabled ? T.Fg : T.Disabled,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            DrawGlyph(g, TBMini.Glyph.Chevron, 10, new Rectangle(Width - S(26), 0, S(18), Height), T.Sub);
        }
    }

    sealed class Chip : Owner
    {
        public string Glyph = "";

        public Chip() { Cursor = Cursors.Default; }

        public int PreferredWidth => S(10) + S(16) + S(4) + TextRenderer.MeasureText(Text ?? "", Font).Width + S(8);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prep(e);
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Fill(g, r, r.Height / 2f, T.Card);
            Stroke(g, r, r.Height / 2f, T.CardBorder);
            DrawGlyph(g, Glyph, 13, new Rectangle(S(9), 0, S(16), Height), T.Sub);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(S(28), 0, Width - S(30), Height), T.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }

    /// Header bubble with a live reading: glyph, label, then temperature and power right-aligned.
    /// Same height and outline as the status chips; its width comes from templates, not the live text,
    /// so it does not jitter as the numbers change.
    sealed class Metric : Owner
    {
        public string Glyph = "", Label = "", Temp = "", Value = "—";
        public Font ValueFont;
        public double Warm = 80, Hot = 95, TempC = double.NaN;
        public bool Muted, ShowLabel = true;
        public bool ReserveTemp;                       // keep room for a temperature
        public string[] Templates = { "188 W" };       // widest value texts expected

        const TextFormatFlags F = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;

        public Metric() { Cursor = Cursors.Default; }

        int Measure(string s, Font f) => TextRenderer.MeasureText(s, f, Size.Empty, F).Width;

        public int PreferredWidth
        {
            get
            {
                var vf = ValueFont ?? Font;
                int w = S(8) + S(14) + S(5);
                if (ShowLabel && Label != "") w += Measure(Label, Font);
                w += ReserveTemp ? S(10) + Measure("88°C", vf) + S(10) : S(8);   // the temperature sits centred between label and power
                int vw = Measure(Value, vf);
                foreach (var t in Templates) vw = Math.Max(vw, Measure(t, vf));
                return w + vw + S(10);
            }
        }

        public void Set(double tempC, string temp, string value, bool muted)
        {
            if (TempC.Equals(tempC) && Temp == temp && Value == value && Muted == muted) return;
            TempC = tempC; Temp = temp; Value = value; Muted = muted;
            Invalidate();
        }

        Color TempColor()
        {
            if (double.IsNaN(TempC) || TempC < Warm) return T.Fg;
            bool hot = TempC >= Hot;
            return T.Dark ? (hot ? Color.FromArgb(0xF8, 0x71, 0x71) : Color.FromArgb(0xFB, 0xBF, 0x24))
                          : (hot ? Color.FromArgb(0xDC, 0x26, 0x26) : Color.FromArgb(0xD9, 0x77, 0x06));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prep(e);
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Fill(g, r, r.Height / 2f, T.Card);
            Stroke(g, r, r.Height / 2f, T.CardBorder);
            DrawGlyph(g, Glyph, 13, new Rectangle(S(7), 0, S(16), Height), T.Accent);
            int labelEnd = S(27);
            if (ShowLabel && Label != "")
            {
                TextRenderer.DrawText(g, Label, Font, new Rectangle(S(27), 0, Width, Height), T.Sub, F | TextFormatFlags.Left);
                labelEnd += TextRenderer.MeasureText(g, Label, Font, Size.Empty, F).Width;
            }
            var vf = ValueFont ?? Font;
            int right = Width - S(10);
            int vw = TextRenderer.MeasureText(g, Value, vf, Size.Empty, F).Width;
            TextRenderer.DrawText(g, Value, vf, new Rectangle(right - vw, 0, vw + S(2), Height), Muted ? T.Sub : T.Fg, F | TextFormatFlags.Left);
            if (Temp != "")
            {
                // centred in the space between the label and the power value, so the two numbers read separately
                int tw = TextRenderer.MeasureText(g, Temp, vf, Size.Empty, F).Width;
                int valueStart = right - Math.Max(vw, Templates.Max(t => TextRenderer.MeasureText(g, t, vf, Size.Empty, F).Width));
                int tx = (labelEnd + valueStart - tw) / 2;
                tx = Math.Max(labelEnd + S(6), Math.Min(tx, right - vw - S(6) - tw));
                TextRenderer.DrawText(g, Temp, vf, new Rectangle(tx, 0, tw + S(2), Height), TempColor(), F | TextFormatFlags.Left);
            }
        }
    }

    /// Dark colours for the dropdown menus.
    sealed class DarkMenuColors : ProfessionalColorTable
    {
        readonly Theme _t;
        public DarkMenuColors(Theme t) { _t = t; UseSystemColors = false; }
        public override Color ToolStripDropDownBackground => _t.Card;
        public override Color ImageMarginGradientBegin => _t.Card;
        public override Color ImageMarginGradientMiddle => _t.Card;
        public override Color ImageMarginGradientEnd => _t.Card;
        public override Color MenuBorder => _t.CardBorder;
        public override Color MenuItemBorder => _t.CtlHover;
        public override Color MenuItemSelected => _t.CtlHover;
        public override Color CheckBackground => _t.Ctl;
        public override Color CheckSelectedBackground => _t.CtlHover;
        public override Color CheckPressedBackground => _t.CtlHover;
        public override Color SeparatorDark => _t.Divider;
        public override Color SeparatorLight => _t.Divider;
    }
}
