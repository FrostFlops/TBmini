using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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
    }

    // ===================================================================================== theme
    sealed class Theme
    {
        public bool Dark;
        public bool Glass;                      // acrylic backdrop + translucent surfaces (Windows 11 22H2+, transparency on)
        public Color Bg, Card, CardBorder, Divider, Fg, Sub, Ctl, CtlHover, Track, Accent, Disabled;
        public Color AccentFg;                  // accent used for text and icons (fills use Accent)
        public Color Hi, HiLow;                 // surface edge: light catching the top of the glass, fading downwards
        public Color MenuBg, FrameBorder;       // opaque menu background; DWM window border
        public Color Tint = Color.Transparent;  // frost over the acrylic backdrop, keeps text readable on busy backgrounds
        public float Radius => Glass ? 12f : 8f;
#if TBMINI_DEV
        public static bool? ForceDark;   // screenshot hook only
        public static bool? ForceGlass;  // screenshot hook only
#endif

        public static string IconFont { get; } =
            new InstalledFontCollection().Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

        public static Theme Load()
        {
            bool dark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int v && v == 0;
#if TBMINI_DEV
            if (ForceDark.HasValue) dark = ForceDark.Value;
#endif
            var t = new Theme { Dark = dark, Glass = TBMini.Glass.Supported && TBMini.Glass.TransparencyOn };
#if TBMINI_DEV
            if (ForceGlass.HasValue) t.Glass = ForceGlass.Value;
#endif
            if (dark)
            {
                t.Bg = C(0x1F1F1F); t.Card = C(0x2A2A2A); t.CardBorder = C(0x363636); t.Divider = C(0x333333);
                t.Fg = C(0xF3F3F3); t.Sub = C(0x9D9D9D); t.Ctl = C(0x353535); t.CtlHover = C(0x404040);
                t.Track = C(0x1C1C1C); t.Disabled = C(0x666666);
                t.MenuBg = C(0x2C2C2C); t.FrameBorder = C(0x3A3A3A);
            }
            else
            {
                t.Bg = C(0xF3F3F3); t.Card = C(0xFFFFFF); t.CardBorder = C(0xE3E3E3); t.Divider = C(0xEDEDED);
                t.Fg = C(0x1B1B1B); t.Sub = C(0x6B6B6B); t.Ctl = C(0xF2F2F2); t.CtlHover = C(0xE8E8E8);
                t.Track = C(0xEBEBEB); t.Disabled = C(0xA8A8A8);
                t.MenuBg = C(0xF9F9F9); t.FrameBorder = C(0xD6D6D6);
            }
            t.Hi = t.HiLow = t.CardBorder;
            if (t.Glass)
            {
                // translucent surfaces over the acrylic backdrop; the form itself paints nothing (GDI black = see-through)
                t.Bg = Color.Black;
                t.Tint = dark ? A(0x161616, 112) : A(0xF6F6F6, 110);
                if (dark)
                {
                    t.Card = A(0xFFFFFF, 24); t.Divider = A(0xFFFFFF, 24); t.Ctl = A(0xFFFFFF, 18); t.CtlHover = A(0xFFFFFF, 40);
                    t.Track = A(0x000000, 70); t.Sub = A(0xFFFFFF, 196); t.Disabled = A(0xFFFFFF, 92);
                    t.Hi = A(0xFFFFFF, 90); t.HiLow = A(0xFFFFFF, 16); t.CardBorder = A(0xFFFFFF, 36);
                }
                else
                {
                    t.Card = A(0xFFFFFF, 155); t.Divider = A(0x000000, 20); t.Ctl = A(0xFFFFFF, 130); t.CtlHover = A(0xFFFFFF, 215);
                    t.Track = A(0x000000, 18); t.Sub = A(0x000000, 178); t.Disabled = A(0x000000, 84);
                    t.Hi = A(0xFFFFFF, 245); t.HiLow = A(0xFFFFFF, 95); t.CardBorder = A(0x000000, 22);
                }
            }
            var acc = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null);
            Color a = acc is int abgr ? Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF) : C(0x0F6CBD);
            t.Accent = t.Glass ? Vivid(a, dark) : dark ? Mix(a, Color.White, 0.25f) : a;
            t.AccentFg = t.Glass && dark ? Mix(t.Accent, Color.White, 0.3f) : t.Accent;   // accent as text/icon colour: readable on dark glass
            return t;
        }

        /// Richer version of a colour for glass, where the translucent backdrop lowers contrast: more saturation,
        /// kept bright on dark glass and slightly deepened on light glass.
        public static Color Vivid(Color c, bool dark)
        {
            float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
            float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            float v = max, s = max <= 0 ? 0 : (max - min) / max, h = 0;
            if (max > min)
            {
                float d = max - min;
                h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
                h *= 60;
            }
            s = Math.Min(1f, s * 1.25f + 0.08f * Math.Min(1f, s / 0.15f));   // greys stay neutral (no red tint from h = 0)
            v = dark ? Math.Max(v, 0.88f) : Math.Min(v, 0.90f);
            float cc = v * s, x = cc * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - cc;
            float rr, gg, bb;
            if (h < 60) { rr = cc; gg = x; bb = 0; } else if (h < 120) { rr = x; gg = cc; bb = 0; } else if (h < 180) { rr = 0; gg = cc; bb = x; }
            else if (h < 240) { rr = 0; gg = x; bb = cc; } else if (h < 300) { rr = x; gg = 0; bb = cc; } else { rr = cc; gg = 0; bb = x; }
            return Color.FromArgb(c.A, (int)Math.Round((rr + m) * 255), (int)Math.Round((gg + m) * 255), (int)Math.Round((bb + m) * 255));
        }

        static Color C(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        static Color A(int rgb, int alpha) => Color.FromArgb(alpha, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

        public static Color Mix(Color a, Color b, float t) =>
            Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        /// Like Mix, but alpha blends too (glass colours are translucent); t is clamped to 0..1.
        public static Color MixA(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb((int)Math.Round(a.A + (b.A - a.A) * t), (int)Math.Round(a.R + (b.R - a.R) * t),
                                  (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        public static Color Fade(Color c, float k) => Color.FromArgb((int)Math.Round(c.A * Math.Max(0f, Math.Min(1f, k))), c);

        /// Blend a -> b round the colour wheel (shortest way; saturation and brightness blend linearly), so the colours
        /// in between stay vivid: green -> orange passes yellow instead of olive. A grey end takes the other's hue.
        public static Color HueMix(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            if (t <= 0f) return a;
            if (t >= 1f) return b;
            ToHsv(a, out float ha, out float sa, out float va);
            ToHsv(b, out float hb, out float sb, out float vb);
            if (sa < 0.05f) ha = hb; else if (sb < 0.05f) hb = ha;
            float dh = hb - ha;
            if (dh > 180f) dh -= 360f; else if (dh < -180f) dh += 360f;
            float hh = ha + dh * t; if (hh < 0) hh += 360f; else if (hh >= 360f) hh -= 360f;
            return FromHsv(hh, sa + (sb - sa) * t, va + (vb - va) * t, (int)Math.Round(a.A + (b.A - a.A) * t));
        }

        static void ToHsv(Color c, out float h, out float s, out float v)
        {
            float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
            float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            v = max; s = max <= 0 ? 0 : d / max; h = 0;
            if (d > 0)
            {
                h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
                h *= 60;
            }
        }

        static Color FromHsv(float h, float s, float v, int alpha)
        {
            float cc = v * s, x = cc * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - cc;
            float r, g, b;
            if (h < 60) { r = cc; g = x; b = 0; } else if (h < 120) { r = x; g = cc; b = 0; } else if (h < 180) { r = 0; g = cc; b = x; }
            else if (h < 240) { r = 0; g = x; b = cc; } else if (h < 300) { r = x; g = 0; b = cc; } else { r = cc; g = 0; b = x; }
            int R(float u) => Math.Max(0, Math.Min(255, (int)Math.Round((u + m) * 255)));
            return Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), R(r), R(g), R(b));
        }

        /// Surface edge: on glass a gradient from Hi (top) to HiLow (bottom), like light on the rim; otherwise a plain border.
        public static void Edge(Graphics g, RectangleF r, float radius, Theme t, float ui, float k = 1f)
        {
            using var p = Draw.RoundRect(r, radius);
            if (!t.Glass) { using var pen0 = new Pen(Fade(t.CardBorder, k)); g.DrawPath(pen0, p); return; }
            using var b = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), Fade(t.Hi, k), Fade(t.HiLow, k), 90f);
            using var pen = new Pen(b, Math.Max(1f, ui * 0.9f));
            g.DrawPath(pen, p);
        }

        /// White or near-black text, whichever has the higher WCAG contrast on c (0.203 = luminance where both are equal).
        public static Color On(Color c)
        {
            static double Lin(int u) { double x = u / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
            double l = 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
            return l > 0.203 ? Color.FromArgb(0x1B, 0x1B, 0x1B) : Color.White;
        }
    }

    static class Glyph
    {
        public const string Eco = "", Sparkle = "", Fast = "", Flame = "";
        public const string Hybrid = "", Chip = "", Monitor = "";
        public const string Battery = "", Charging = "", Plug = "", Keyboard = "", Palette = "", Speaker = "";
        public const string Chevron = "";

        public const string Game = "\uE7FC", Bolt = "\uE945", Adapt = "\uE895";   // Adapt = Sync arrows

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

        readonly GlassLabel _hTitle;
        readonly Chip _chipPower, _chipGpu;
        readonly Metric _mCpu, _mGpu, _mTotal;
        readonly Timer _sensorTimer = new Timer { Interval = 1500 };
        bool _sensing;
        readonly Timer _anim = new Timer { Interval = 10 };   // pop-up: short slide + fade
        int _animFrom, _animTo, _animStart;
        const int AnimMs = 450, AnimRise = 56;
        bool _animFade;
        IntPtr _powerNotify;
        readonly GlassLabel _secPerf, _secGpu;
        readonly Card _cardPerf, _cardGpu;
        readonly Tile[] _pt = new Tile[4];
        readonly Tile _autoTile;
        readonly Tile[] _perfTiles;
        readonly Tile[] _gt = new Tile[3];
        readonly GlassLabel _status;
        readonly GlassLabel _link;
        readonly Dictionary<string, Row> _rows = new Dictionary<string, Row>();
        readonly Dictionary<string, (GlassLabel label, Card card)> _sections = new Dictionary<string, (GlassLabel, Card)>();

        bool _menuOpen, _anchorBottom;
        string _geekTip;
        public int HiddenAt;   // Environment.TickCount when the panel last auto-hid

        sealed class Row
        {
            public Feature F;
            public GlassLabel Label;
            public Control Ctl;          // Toggle / Segmented / Drop
            public bool Shown;           // own flag: Control.Visible reads false while the form is hidden
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

            _hTitle = new GlassLabel { Font = _title, AutoFit = true, Text = "TBMini" };
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
            // auto adjust: saver on battery or a charger that can't keep up, performance on USB-C, geek on the full-power adapter
            _autoTile = new Tile { Glyph = Glyph.Adapt, Accent = Color.FromArgb(0x06, 0xB6, 0xD4), Font = _tileText, Ui = _s };
            _autoTile.Click += (s, e) => _app.SetAutoPower(!_app.AutoPower);
            _cardPerf.Controls.Add(_autoTile);
            _perfTiles = _pt.Concat(new[] { _autoTile }).ToArray();
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

            _status = new GlassLabel { Font = _small };
            _link = new GlassLabel { Font = _small, AutoFit = true, Link = true, Visible = false };
            _link.Click += (s, e) => { Hide(); _app.RestartNow(); };
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
            _tip.SetToolTip(_pt[1], T("智能：按负载自动调节", "Smart: adjusts to the load"));
            _tip.SetToolTip(_pt[2], T("性能：高功耗释放", "Performance: higher power limits"));
            _autoTile.Text = T("自动调节", "Auto adjust");
            // short lines: a tooltip shows each one as it is, so keep it a glanceable mapping
            _tip.SetToolTip(_autoTile, T("自动调节\n"
                                         + "电池 / 充电器功率不足 → 省电\n"
                                         + "USB-C 充电器 → 性能\n"
                                         + "原装适配器 → 极客\n"
                                         + "只在电源变化时切换，手动选模式会关闭它",
                                         "Auto adjust\n"
                                         + "Battery / charger can't keep up → Saver\n"
                                         + "USB-C charger → Performance\n"
                                         + "Full-power adapter → Geek\n"
                                         + "Switches only when power changes; picking a mode turns it off"));
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
            // keep the active (live acrylic) look while a dropdown menu has the focus; the panel hides on real deactivation
            if (m.Msg == Glass.WM_NCACTIVATE && _t.Glass) m.WParam = (IntPtr)1;
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

        GlassLabel SectionLabel(string text) => new GlassLabel { Font = _sec, AutoFit = true, Text = text };

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

        bool _glassApplied;

        void ApplyDwm()
        {
            if (!IsHandleCreated) return;
            Glass.Apply(Handle, _t);
            if (_glassApplied != _t.Glass) { _glassApplied = _t.Glass; Invalidate(true); }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (_t.Glass) Glass.Frost(e.Graphics, ClientRectangle, _t);    // the acrylic backdrop shows through, frosted
            else base.OnPaintBackground(e);
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
            r = new Row { F = f, Label = new GlassLabel { Text = f.Label, Font = _row } };
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
            var f = r.F;
            if (f.Kind == FeatureKind.Toggle)
            {
                var t = new Toggle { Ui = _s };
                t.Click += (s, e) => { t.Unknown = false; t.On = !t.On; _app.SetFeature(f, -2); };   // flip now, re-synced after the write
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
            var m = new ContextMenuStrip();
            for (int i = 0; i < f.Options.Length; i++)
            {
                int ii = i;
                var it = new ToolStripMenuItem(f.Options[i]) { Checked = st.Value == i, Enabled = !st.Busy };
                it.Click += (s, e) => _app.SetFeature(f, ii);
                m.Items.Add(it);
            }
            GlassMenu.Style(m, _t);
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
                if (band == 0) { left.Add(TileBlock(_secPerf, _cardPerf, _perfTiles)); left.Add(TileBlock(_secGpu, _cardGpu, _gt)); }
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
            public GlassLabel Title; public Card Card;
            public Tile[] Tiles;               // tile block
            public List<Row> Rows;             // feature section block
            public int CardH;                  // natural card height
        }

        Block TileBlock(GlassLabel title, Card card, Tile[] tiles) =>
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
        /// between blocks, or, for a single feature section, into taller rows (at most double height).
        void PlaceColumn(List<Block> blocks, int x, int y, int bandH, int colW)
        {
            if (blocks.Count == 0) return;
            int extra = bandH - ColumnHeight(blocks);
            int stretch = blocks.Count == 1 && blocks[0].Rows != null
                ? Math.Min(extra, blocks[0].Rows.Count * S(RowH)) : 0;     // up to double-height rows, so the pair ends level
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
                // only as tall as the text, centred: a full-height label would paint over the card's rim and the dividers
                int lh = Math.Min(rh - S(8), _row.Height + S(6));
                r.Label.Bounds = new Rectangle(inX, ry + (rh - lh) / 2, r.Ctl.Left - inX - S(8), lh);
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
            _hTitle.ForeColor = _t.Fg; _hTitle.T = _t;
            foreach (var c in new Owner[] { _chipPower, _chipGpu, _mCpu, _mGpu, _mTotal }) c.T = _t;
            foreach (var l in new[] { _secPerf, _secGpu, _status }) { l.ForeColor = _t.Sub; l.T = _t; }
            foreach (var c in new[] { _cardPerf, _cardGpu }) c.T = _t;
            foreach (var t in _perfTiles.Concat(_gt)) t.T = _t;
            foreach (var (label, card) in _sections.Values) { label.ForeColor = _t.Sub; label.T = _t; card.T = _t; }
            foreach (var r in _rows.Values)
            {
                r.Label.ForeColor = _t.Fg; r.Label.T = _t;
                if (r.Ctl is IThemed th) th.T = _t;
            }
            _link.ForeColor = _t.AccentFg; _link.T = _t;
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
            var power = _app.ShownPower;                              // a mode being switched to shows right away
            bool geekOk = Its.GeekAvailable, auto = _app.AutoPower;
            // while a switch runs the tiles stay as they are (Locked ignores clicks) instead of greying out mid-transition
            Tile.SelectIn(_pt, auto ? -1 : Array.IndexOf(PowerModes, power));
            for (int i = 0; i < 4; i++)
            {
                _pt[i].Ring = power == PowerModes[i] && auto;          // the mode auto adjust is applying right now
                _pt[i].Enabled = PowerModes[i] != PowerMode.Geek || geekOk;
                _pt[i].Locked = busy;
                _pt[i].Invalidate();
            }
            _autoTile.Selected = auto; _autoTile.Enabled = Its.Supported; _autoTile.Locked = busy; _autoTile.Invalidate();
            string geekTip = geekOk ? T("极客：最高功耗（需原装大功率适配器）", "Geek: maximum power (needs the full-power adapter)") : Its.GeekUnavailableText();
            if (geekTip != _geekTip) { _geekTip = geekTip; _tip.SetToolTip(_pt[3], geekTip); }

            var gi = _app.GpuInfo;
            Tile.SelectIn(_gt, Array.IndexOf(GpuModes, gi.Mode));
            for (int i = 0; i < 3; i++)
            {
                _gt[i].Locked = busy;
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
                if (f.Kind == FeatureKind.Choice && (wantSeg != (r.Ctl is Segmented) || (r.Ctl is Segmented sg0 && !sg0.Options.SequenceEqual(f.Options))))
                { BuildControl(r); ApplyTheme(); layout = true; }

                // while a write runs, keep showing the value the user just picked (the control moved there on click)
                bool busyRow = st.Busy;
                switch (r.Ctl)
                {
                    case Toggle t: if (!busyRow) { t.On = st.Value == 1; t.Unknown = st.Value < 0; } t.Locked = busyRow; break;
                    case Segmented sg: if (!busyRow) sg.Selected = st.Value; sg.Locked = busyRow; break;
                    case Drop d: if (!busyRow) d.Text = st.Value >= 0 && st.Value < f.Options.Length ? f.Options[st.Value] : "—"; d.Locked = busyRow; break;
                }
                r.Ctl.Invalidate();
            }
            if (layout) Relayout();
            else LayoutHeader();   // chips may have changed width

            string line = "";
            if (!Its.Supported) line = T("没找到联想 ITS 服务，性能模式不可用", "Lenovo ITS service not found; performance modes are unavailable");
            if (busy) line = Join(line, T("切换中…", "Switching…"));
            // a disabled Geek tile shows no tooltip, so say why here when the charger is the reason
            if (Its.GeekSupported && !geekOk && !busy && Charger.Limited(Charger.Current)) line = Join(line, Its.GeekUnavailableText());
            if (!Program.IsAdmin) line = Join(line, T("显卡和 BIOS 项需要管理员权限（点显卡按钮可提权）", "GPU and BIOS settings need administrator rights (click a GPU button to elevate)"));
            else if (gi.Error != null) line = Join(line, T("显卡：", "GPU: ") + gi.Error);
            else if (!gi.RunningKnown) line = Join(line, T("内屏未点亮，显卡按 BIOS 显示", "Internal display off; GPU mode shown from BIOS"));
            if (pend != null) line = Join(line, T("重启后显卡切到「" + pend + "」", "GPU switches to " + pend + " after a restart"));
            if (line == "") line = T("Ctrl+Alt+Shift+T 随时重新打开触摸板", "Ctrl+Alt+Shift+T turns the touchpad back on at any time");
            _status.Text = line;

            _link.Visible = pend != null;
            if (pend != null)
            {
                int lw = Gx.Width(line, _status.Font);
                _link.Location = new Point(Math.Min(_status.Left + lw + S(6), Width - S(Pad) - _link.Width), _status.Top + (_status.Height - _link.Height) / 2);
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
                _animFade = !_t.Glass;
                if (_animFade) Opacity = 0;
                Location = new Point(x, _animFrom);
                _animStart = Environment.TickCount;
                _anim.Start();
            }
            else Location = new Point(x, y);
            Show();
            Activate();
            SetForegroundWindow(Handle);
            if (_t.Glass) { Glass.Renew(Handle, _t); Glass.ShowActive(this); }   // DWM drops the backdrop of a re-shown window
        }

        void AnimStep()
        {
            double t = Math.Min(1.0, (Environment.TickCount - _animStart) / (double)AnimMs);
            double e = 1 - Math.Pow(1 - t, 3);                      // position: ease-out cubic
            Top = (int)Math.Round(_animFrom + (_animTo - _animFrom) * e);
            if (t >= 1) { _anim.Stop(); Top = _animTo; if (_animFade) Opacity = 1; }   // back to a normal (non-layered) window
            else if (_animFade) Opacity = Math.Max(0.01, 1 - (1 - t) * (1 - t));     // fade: ease-out quadratic, spans the whole rise
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
        bool _locked;

        /// Busy (a switch is running): looks normal but ignores clicks, so a transition is not interrupted by greying out.
        public bool Locked
        {
            get => _locked;
            set { if (_locked == value) return; _locked = value; Cursor = value ? Cursors.AppStarting : Cursors.Hand; Invalidate(); }
        }

        /// Hover highlight only when the control can actually be used.
        protected bool Hovered => Hover && Enabled && !_locked;
        protected static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
        /// Animate only what the user can see; otherwise jump straight to the end state.
        protected bool CanAnimate => IsHandleCreated && Visible && Animator.Enabled;

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
        protected override void OnClick(EventArgs e) { if (_down == MouseButtons.Left && !_locked) base.OnClick(e); }
        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected Graphics Prep(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Glass.Backdrop(g, this, T);
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

        /// Glass edge: bright where light hits the top, fading downwards (a plain border on the opaque theme).
        protected void Edge(Graphics g, RectangleF r, float radius) => Theme.Edge(g, r, radius, T, Ui);

        /// Glossy sheen on a coloured (selected) surface; k fades it (0..1) while a transition is under way.
        protected void Sheen(Graphics g, RectangleF r, float radius, float k = 1f)
        {
            if (!T.Glass || k <= 0 || r.Height < 2) return;
            using var p = Draw.RoundRect(r, radius);
            using var b = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height / 2 + 2), Color.FromArgb((int)(70 * Clamp01(k)), 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f);
            using var clip = g.Clip;
            g.SetClip(new RectangleF(r.X, r.Y, r.Width, r.Height / 2), CombineMode.Intersect);
            g.FillPath(b, p);
            g.Clip = clip;
        }

        protected void DrawGlyph(Graphics g, string glyph, float size, Rectangle r, Color c) => Gx.Glyph(g, glyph, size * Ui, r, c, T);

        protected void DrawText(Graphics g, string s, Font f, RectangleF r, Color c,
                            StringAlignment h = StringAlignment.Near, StringAlignment v = StringAlignment.Center, bool ellipsis = false)
            => Gx.Text(g, s, f, r, c, T, h, v, ellipsis);
    }

    /// Rounded settings card; draws row dividers.
    sealed class Card : Panel, IThemed
    {
        Theme _t = Theme.Load();
        public Theme T { get => _t; set { _t = value; BackColor = value.Glass ? Color.Black : value.Card; Invalidate(); } }
        public float Ui = 1f;
        public int[] Dividers = new int[0];

        public Card()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = _t.Glass ? Color.Black : _t.Card;
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (_t.Glass) Glass.Frost(g, ClientRectangle, _t); else g.Clear(Parent?.BackColor ?? _t.Bg);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var p = Draw.RoundRect(r, _t.Radius * Ui))
            using (var b = new SolidBrush(_t.Card))
                g.FillPath(b, p);
            Theme.Edge(g, r, _t.Radius * Ui, _t, Ui);
            using var dp = new Pen(_t.Divider);
            foreach (int y in Dividers) g.DrawLine(dp, 14 * Ui, y, Width - 14 * Ui, y);
        }
    }

    sealed class Tile : Owner, IAnimated
    {
        public string Glyph;
        public Color Accent = Color.SteelBlue;
        public bool Ring;                              // outlined in its colour: the mode auto adjust is applying
        bool _sel;
        // selection: the accent floods in from where the tile was pressed (or its centre) and drains back out
        Spring _fill = new Spring(122f, 0.66f, 0.002f);   // ~0.6 s; speed scales with sqrt(K)
        // press: the tile squishes in and springs back with a little wobble
        Spring _press = new Spring(700f, 0.9f, 0.0005f);
        PointF _origin, _pressPt;
        int? _pressedAt;              // when this tile was last pressed (TickCount); null: never

        public Tile() { _press.Snap(1f); }

        /// Selects index sel (-1: none) in a group of tiles. The colour leaves the old tile through the side that faces
        /// the new one and enters the new tile from the side that faces the old one, so it reads as one liquid moving across.
        public static void SelectIn(Tile[] group, int sel)
        {
            int old = Array.FindIndex(group, t => t._sel);
            if (old == sel) return;
            if (old >= 0 && sel >= 0)
            {
                var a = group[old]; var b = group[sel];
                a._flowTo = b.Center(a.Parent); b._flowFrom = a.Center(b.Parent);
                a._mixNext = b.Accent;   // the colour leaving the old tile turns into the new one on its way out
            }
            for (int i = 0; i < group.Length; i++) group[i].Selected = i == sel;
        }

        PointF? _flowFrom, _flowTo;   // the other tile's centre (parent coordinates) for the next select / deselect
        Color? _mixNext, _mix;        // the other tile's colour for the next / the running transfer
        bool _wipe;                   // draining after a transfer: a straight front recedes to the exit side ("]")

        /// The selected colour as a soft top-to-bottom gradient (lighter above, deeper below) instead of a flat block.
        Color ShadeTop(Color accent) => Theme.Mix(accent, Color.White, T.Dark ? 0.16f : 0.20f);
        static Color ShadeBottom(Color accent) => Theme.Mix(accent, Color.Black, 0.10f);

        LinearGradientBrush Shade(Color accent) =>
            new LinearGradientBrush(new RectangleF(0, -1, Math.Max(1, Width), Math.Max(1, Height) + 2), ShadeTop(accent), ShadeBottom(accent), 90f);

        int[] _px = new int[0];

        /// Flow axis of a transfer: the unit vector from the tile's centre out through _origin (the side facing the other
        /// tile), and the tile's extent along it. False when _origin is the centre (no transfer direction).
        bool FlowAxis(out float ux, out float uy, out float span)
        {
            float w = Math.Max(1, Width), h = Math.Max(1, Height);
            ux = _origin.X - w / 2f; uy = _origin.Y - h / 2f;
            float l = (float)Math.Sqrt(ux * ux + uy * uy);
            if (l <= 0.5f) { ux = uy = 0; span = 1; return false; }
            ux /= l; uy /= l;
            span = Math.Max(1f, Math.Abs(ux) * w + Math.Abs(uy) * h);
            return true;
        }

        /// The flood for one frame: Shade's gradient (plus the glass sheen, faded in with q), opaque inside and fading to
        /// clear over `feather` pixels at its edge. Premultiplied ARGB, written directly (~0.3 ms per tile).
        /// Its shape is a circle of radius `outer` around _origin, or, for the draining tile of a transfer, `band`: a
        /// rectangle with rounded corners (radius bandR) docked to the exit side. The draining liquid (the tail of the
        /// stream) also turns into the new tile's colour along the flow: a linear gradient, the new colour at the exit
        /// side and the old colour at the far side, growing with m = 1 - q. The mix goes round the colour wheel
        /// (green -> yellow -> orange), not straight through RGB, which would turn muddy in the middle.
        Bitmap FloodBitmap(Color accent, Color? other, float m, float outer, float feather, float q, RectangleF? band, float bandR)
        {
            int w = Math.Max(1, Width), h = Math.Max(1, Height);
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                int stride = data.Stride / 4, n = stride * h;
                if (_px.Length < n) _px = new int[n];
                float ox = _origin.X, oy = _origin.Y;
                bool axis = FlowAxis(out float ux, out float uy, out float span);
                bool mix = other.HasValue && m > 0.002f && axis;
                // along the band's own axis when there is one, so the gradient runs straight across the tile
                if (band.HasValue && axis)
                {
                    if (Math.Abs(ux) >= Math.Abs(uy)) { ux = Math.Sign(ux); uy = 0; span = w; }
                    else { ux = 0; uy = Math.Sign(uy); span = h; }
                }
                const int L = 256;                                  // accent -> other, LUT[0] = accent (fine enough: no bands)
                if (_lut.Length != L + 1) _lut = new Color[L + 1];
                for (int i = 0; i <= L; i++) _lut[i] = mix ? Theme.HueMix(accent, other.Value, (float)i / L) : accent;
                // Shade + sheen are linear in the colour: row colour = c * P(y) + Q(y) per channel
                float lift = T.Dark ? 0.16f : 0.20f, deepen = 0.10f;
                float sheenTop = T.Glass ? 70f / 255f * q : 0f, half = h / 2f;
                float inner = outer - feather, inv = 1f / Math.Max(0.5f, feather);
                float outer2 = outer * outer, inner2 = inner > 0 ? inner * inner : -1f, invSpan = 1f / span;
                // band as a rounded box: centre, half size minus the corner radius
                RectangleF bb = band ?? RectangleF.Empty;
                float bcx = bb.X + bb.Width / 2, bcy = bb.Y + bb.Height / 2;
                float bhx = Math.Max(0f, bb.Width / 2 - bandR), bhy = Math.Max(0f, bb.Height / 2 - bandR);
                Color c0 = _lut[0];
                for (int y = 0; y < h; y++)
                {
                    float t = (y + 0.5f) / h;
                    float sh = y < half ? sheenTop * (1f - y / half) : 0f;          // white sheen over the top half (glass)
                    float p = ((1 - lift) * (1 - t) + (1 - deepen) * t) * (1 - sh);
                    float qq = 255f * lift * (1 - t) * (1 - sh) + 255f * sh;
                    int solid = (255 << 24) | ((int)(c0.R * p + qq) << 16) | ((int)(c0.G * p + qq) << 8) | (int)(c0.B * p + qq);
                    float py = y + 0.5f, dy = py - oy, dy2 = dy * dy;
                    int row = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        float px = x + 0.5f, dx = px - ox;
                        float a;
                        if (band.HasValue)
                        {
                            // signed distance to the rounded band (negative inside); the edge sits at half alpha
                            float qx = Math.Abs(px - bcx) - bhx, qy = Math.Abs(py - bcy) - bhy;
                            float ex = Math.Max(qx, 0f), ey = Math.Max(qy, 0f);
                            float sd = (float)Math.Sqrt(ex * ex + ey * ey) + Math.Min(Math.Max(qx, qy), 0f) - bandR;
                            a = 0.5f - sd * inv;
                            if (a <= 0f) { _px[row + x] = 0; continue; }
                            if (a > 1f) a = 1f;
                        }
                        else
                        {
                            float d2 = dx * dx + dy2;
                            if (d2 >= outer2) { _px[row + x] = 0; continue; }
                            a = d2 <= inner2 ? 1f : Math.Min(1f, (outer - (float)Math.Sqrt(d2)) * inv);
                        }
                        Color c = c0;
                        if (mix)
                        {
                            // distance behind the exit side, measured along the flow: 0 there, span at the far side
                            float behind = Math.Max(0f, -(dx * ux + dy * uy));
                            float k = m * (1f - behind * invSpan);
                            if (k > 0f) c = _lut[Math.Min(L, (int)(k * L + 0.5f))];
                        }
                        if (a >= 1f && !mix) { _px[row + x] = solid; continue; }
                        float cr = c.R * p + qq, cg = c.G * p + qq, cb = c.B * p + qq;
                        int A = (int)(a * 255f + 0.5f);
                        _px[row + x] = (A << 24) | ((int)(cr * a) << 16) | ((int)(cg * a) << 8) | (int)(cb * a);
                    }
                }
                Marshal.Copy(_px, 0, data.Scan0, n);
            }
            finally { bmp.UnlockBits(data); }
            return bmp;
        }

        Color[] _lut = new Color[0];

        PointF Center(Control space)
        {
            var p = space == Parent ? Location : (space != null ? space.PointToClient(Parent.PointToScreen(Location)) : Location);
            return new PointF(p.X + Width / 2f, p.Y + Height / 2f);
        }

        /// Where the line from this tile's centre towards other (parent coordinates) leaves the tile, in local coordinates.
        PointF EdgeToward(PointF other)
        {
            float dx = other.X - (Left + Width / 2f), dy = other.Y - (Top + Height / 2f);
            if (Math.Abs(dx) < 0.5f && Math.Abs(dy) < 0.5f) return new PointF(Width / 2f, Height / 2f);
            float s = Math.Min(Math.Abs(dx) > 0.01f ? Width / 2f / Math.Abs(dx) : float.MaxValue, Math.Abs(dy) > 0.01f ? Height / 2f / Math.Abs(dy) : float.MaxValue);
            return new PointF(Width / 2f + dx * s, Height / 2f + dy * s);
        }

        public bool Selected
        {
            get => _sel;
            set
            {
                if (_sel == value) return;
                _sel = value;
                float to = value ? 1f : 0f;
                var hint = value ? _flowFrom : _flowTo;
                _mix = hint != null ? _mixNext : null;
                _wipe = !value && hint != null;
                _flowFrom = _flowTo = null; _mixNext = null;
                if (!CanAnimate) { _fill.Snap(to); Invalidate(); return; }
                // origin: towards the other tile of a transfer; else where it was just pressed; else the centre
                if (hint is PointF h) _origin = EdgeToward(h);
                else if (value && _pressedAt is int at && unchecked(Environment.TickCount - at) <= 3000) _origin = _pressPt;
                else _origin = new PointF(Width / 2f, Height / 2f);
                _fill.Target = to;
                Animator.Run(this);
            }
        }

        public bool Tick(float dt)
        {
            if (IsDisposed) return false;
            bool a = _fill.Step(dt), b = _press.Step(dt);
            Invalidate();
            return a || b;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Enabled || Locked) return;
            _pressPt = e.Location; _pressedAt = Environment.TickCount;   // a running flood keeps its origin
            if (!CanAnimate) return;
            _press.K = 900f; _press.Zeta = 0.9f; _press.Target = 0.93f;
            Animator.Run(this);
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); Release(); }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); Release(); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (!Visible) _press.Snap(1f); }

        void Release()
        {
            if (_press.Target == 1f) return;
            _press.K = 380f; _press.Zeta = 0.36f; _press.Target = 1f;   // let go: bounce back
            if (CanAnimate) Animator.Run(this); else _press.Snap(1f);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prep(e);
            Color accent = T.Glass ? Theme.Vivid(Accent, T.Dark) : Accent;   // glass lowers contrast: richer colours
            float f = _fill.X, q = Clamp01(f);
            float z = Math.Min(1f, _press.X);                                 // overshoot above 1 would leave the tile's bounds
            if (z < 0.9995f)
            {
                g.TranslateTransform(Width / 2f, Height / 2f); g.ScaleTransform(z, z); g.TranslateTransform(-Width / 2f, -Height / 2f);
            }
            float rad = (T.Glass ? 10 : 6) * Ui;
            var box = new RectangleF(0, 0, Width, Height);
            Color on = Theme.On(accent);
            // fully flooded (incl. the spring's overshoot): the anti-aliased gradient fill it has at rest
            bool full = f >= 0.999f, partial = !full && f > 0.001f;
            if (full) using (var gb = Shade(accent)) using (var tp = Draw.RoundRect(box, rad)) g.FillPath(gb, tp);
            else Fill(g, box, rad, Hovered ? T.CtlHover : T.Ctl);

            using var tilePath = Draw.RoundRect(box, rad);
            using var flood = new GraphicsPath();      // where the content switches to the on-accent colour
            if (partial)
            {
                // the liquid: the tile's gradient inside a circle whose rim is feathered, so its front is soft, not a
                // hard disc. The opaque core reaches the farthest corner exactly at f = 1, so the hand-over to the
                // rest fill is seamless.
                float feather = 20 * Ui, outer = 0;
                bool hasAxis = FlowAxis(out float ux, out float uy, out float span), wipe = _wipe && hasAxis;
                RectangleF? band = null;
                float bandR = 0;
                if (wipe)
                {
                    // the tail: a band with the tile's own rounded corners, docked to the exit side, that shrinks towards
                    // it as it drains (like a smaller tile sliding out). Edge as crisp as the tile's, just softened.
                    feather = 3 * Ui;
                    bool horiz = Math.Abs(ux) >= Math.Abs(uy);
                    float extent = horiz ? Width : Height, d = (extent + feather) * f;
                    band = horiz ? (ux > 0 ? new RectangleF(Width - d, 0, d, Height) : new RectangleF(0, 0, d, Height))
                                 : (uy > 0 ? new RectangleF(0, Height - d, Width, d) : new RectangleF(0, 0, Width, d));
                    bandR = Math.Max(0.5f, Math.Min(rad, Math.Min(band.Value.Width, band.Value.Height) / 2));
                    using (var bp = Draw.RoundRect(band.Value, bandR)) flood.AddPath(bp, false);
                }
                else
                {
                    float dx = Math.Max(_origin.X, Width - _origin.X), dy = Math.Max(_origin.Y, Height - _origin.Y);
                    outer = ((float)Math.Sqrt(dx * dx + dy * dy) + feather) * f + 0.5f;
                    float mid = Math.Max(0.5f, outer - feather / 2);
                    flood.AddEllipse(_origin.X - mid, _origin.Y - mid, 2 * mid, 2 * mid);
                }
                // computed per pixel into a texture that then fills the rounded tile: an exact radial alpha ramp (no
                // banding) over the vertical gradient, and smooth tile edges, which a clip region (always aliased) would not give
                Color? other = !_sel && _mix is Color oc ? (T.Glass ? Theme.Vivid(oc, T.Dark) : oc) : (Color?)null;
                using var bmp = FloodBitmap(accent, other, 1 - q, outer, feather, q, band, bandR);
                using var tb = new TextureBrush(bmp, WrapMode.Clamp);
                g.FillPath(tb, tilePath);
            }
            if (full) Sheen(g, box, rad);

            // rim / auto-adjust ring fade out as the colour comes in (no on/off step when the spring overshoots)
            float rim = 1 - q;
            if (rim > 0.01f)
            {
                if (Ring && Enabled && !_sel) Stroke(g, new RectangleF(Ui, Ui, Width - 2 * Ui, Height - 2 * Ui), rad - Ui, Theme.Fade(accent, rim), 2f * Ui);
                else Theme.Edge(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), rad, T, Ui, rim);
            }

            // shrink long labels (e.g. English "Performance") to fit instead of cutting them off
            var font = Font;
            int avail = Width - S(8), tw = Gx.Width(Text, font);
            if (tw > avail) font = new Font(Font.FontFamily, Math.Max(7f, Font.Size * avail / tw), Font.Style);
            int gh = S(26);
            var glyphBox = new Rectangle(0, S(9), Width, gh);
            var textBox = new RectangleF(0, S(9) + gh, Width, Height - S(9) - gh - S(4));
            Color fgOff = Enabled ? T.Fg : T.Disabled, glyphOff = Enabled ? accent : T.Disabled;
            // glyph and label in their normal colours outside the liquid and in the on-accent colour inside it:
            // the colour change travels across the content with the flood (the two never overlap, so no halo)
            if (!full)
            {
                var st = g.Save();
                if (partial) g.SetClip(flood, CombineMode.Exclude);
                DrawGlyph(g, Glyph, 18, glyphBox, glyphOff);
                DrawText(g, Text, font, textBox, fgOff, StringAlignment.Center, StringAlignment.Near, ellipsis: true);
                g.Restore(st);
            }
            if (full || partial)
            {
                var st = g.Save();
                if (partial) g.SetClip(flood, CombineMode.Intersect);
                DrawGlyph(g, Glyph, 18, glyphBox, on);
                DrawText(g, Text, font, textBox, on, StringAlignment.Center, StringAlignment.Near, ellipsis: true);
                g.Restore(st);
            }
            if (!ReferenceEquals(font, Font)) font.Dispose();
        }
    }

    sealed class Segmented : Owner, IAnimated
    {
        public string[] Options = new string[0];
        public event Action<int> Picked;
        int _sel = -1, _hoverIdx = -1;
        // the selection pill's left and right edges, each on its own spring: the edge in front moves first and the
        // one behind follows, so the pill stretches towards the new option like a drop of liquid and settles there
        Spring _l = new Spring(300f, 0.75f, 0.05f), _r = new Spring(300f, 0.75f, 0.05f);
        bool _placed;

        public int Selected
        {
            get => _sel;
            set
            {
                if (_sel == value) return;
                int old = _sel;
                _sel = value;
                if (_sel < 0 || _sel >= Options.Length) { _placed = false; Invalidate(); return; }
                Cell(_sel, out float a, out float b);
                if (old < 0 || !_placed || !CanAnimate) { _l.Snap(a); _r.Snap(b); _placed = true; Invalidate(); return; }
                if (_sel > old) { Lead(ref _r); Trail(ref _l); } else { Lead(ref _l); Trail(ref _r); }
                _l.Target = a; _r.Target = b;
                Animator.Run(this);
            }
        }

        static void Lead(ref Spring s) { s.K = 575f; s.Zeta = 0.58f; }    // quick, overshoots a little
        static void Trail(ref Spring s) { s.K = 245f; s.Zeta = 0.8f; }    // lags behind, then catches up

        void Cell(int i, out float x0, out float x1)
        {
            float w = (float)Width / Math.Max(1, Options.Length), inset = 2.5f * Ui;
            x0 = i * w + inset; x1 = (i + 1) * w - inset;
        }

        public bool Tick(float dt)
        {
            if (IsDisposed) return false;
            bool a = _l.Step(dt), b = _r.Step(dt);
            Invalidate();
            return a || b;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_sel >= 0 && _sel < Options.Length) { Cell(_sel, out float a, out float b); _l.Snap(a); _r.Snap(b); _placed = true; }
        }

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
            if (e.Button != MouseButtons.Left || !Enabled || Locked || !ClientRectangle.Contains(e.Location)) return;
            int i = IndexAt(e.X);
            if (i < 0) return;
            Selected = i;          // move right away; the owner re-syncs from the real state when the write finishes
            Picked?.Invoke(i);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prep(e);
            float rTrack = Height / 2f;
            Fill(g, new RectangleF(0, 0, Width, Height), rTrack, T.Track);
            int n = Options.Length;
            if (n == 0) return;
            float w = (float)Width / n, inset = 2.5f * Ui, cellH = Height - 2 * inset;

            var fonts = new Font[n];
            for (int i = 0; i < n; i++)
            {
                // shrink a label that does not fit its segment (e.g. English "Dynamic") instead of cutting it off
                float avail = w - 2 * inset - 4 * Ui, tw = Gx.Width(Options[i], Font);
                fonts[i] = tw > avail ? new Font(Font.FontFamily, Math.Max(6.5f, Font.Size * avail / tw), Font.Style) : Font;
            }
            RectangleF CellRect(int i) => new RectangleF(i * w + inset, inset, w - 2 * inset, cellH);

            bool moving = _l.Moving || _r.Moving;
            if (_hoverIdx >= 0 && _hoverIdx != _sel && Hovered && !moving) Fill(g, CellRect(_hoverIdx), cellH / 2f, T.CtlHover);
            Color fg = Enabled ? T.Fg : T.Disabled;
            for (int i = 0; i < n; i++) DrawText(g, Options[i], fonts[i], CellRect(i), fg, StringAlignment.Center, StringAlignment.Center, ellipsis: true);

            if (_sel >= 0 && _sel < n && _placed)
            {
                float x0 = Math.Max(inset * 0.5f, _l.X), x1 = Math.Min(Width - inset * 0.5f, _r.X);
                float cellW = w - 2 * inset, stretch = Math.Max(0f, (x1 - x0) / cellW - 1f);
                float h = cellH * (1f - 0.16f * Math.Min(1f, stretch));   // stretched thin, like a moving drop
                if (x1 - x0 < h) { float c = (x0 + x1) / 2; x0 = c - h / 2; x1 = c + h / 2; }
                var pill = new RectangleF(x0, (Height - h) / 2f, x1 - x0, h);
                Color pc = Enabled ? T.Accent : T.Disabled;
                Fill(g, pill, h / 2f, pc);
                Sheen(g, pill, h / 2f);
                // the labels again, in the on-accent colour, clipped to the pill: the colour flows with it
                using var path = Draw.RoundRect(pill, h / 2f);
                var saved = g.Save();
                g.SetClip(path, CombineMode.Intersect);
                Color on = Theme.On(pc);
                for (int i = 0; i < n; i++)
                {
                    var cr = CellRect(i);
                    if (cr.Right > pill.Left && cr.Left < pill.Right)
                        DrawText(g, Options[i], fonts[i], cr, on, StringAlignment.Center, StringAlignment.Center, ellipsis: true);
                }
                g.Restore(saved);
            }
            foreach (var f in fonts) if (!ReferenceEquals(f, Font)) f.Dispose();
        }
    }

    sealed class Toggle : Owner, IAnimated
    {
        bool _on, _unknown, _painted;
        // 0 = off, 1 = on; underdamped, so the knob arrives with a small squash against the end
        Spring _p = new Spring(375f, 0.55f, 0.002f);

        public bool On { get => _on; set { if (_on == value) return; _on = value; Retarget(); } }
        public bool Unknown { get => _unknown; set { if (_unknown == value) return; _unknown = value; Retarget(); } }

        void Retarget()
        {
            float to = _on && !_unknown ? 1f : 0f;
            if (_p.Target == to && !_p.Moving) return;
            // until the toggle has been on screen once, a new value is where it already is, not a switch: no animation
            if (!_painted || !CanAnimate) { _p.Snap(to); Invalidate(); return; }
            _p.Target = to;
            Animator.Run(this);
        }

        public bool Tick(float dt)
        {
            if (IsDisposed) return false;
            bool m = _p.Step(dt);
            Invalidate();
            return m;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prep(e);
            _painted = true;
            var r = new RectangleF(1, 1, Width - 2, Height - 2);
            float p = _p.X, q = Clamp01(p), rad = r.Height / 2;

            Color accent = Enabled ? (Hovered ? Theme.Mix(T.Accent, Color.White, 0.08f) : T.Accent) : T.Disabled;
            Color offFill = Hovered ? T.CtlHover : (T.Glass ? T.Track : Color.Transparent);
            Color offLine = Enabled ? T.Sub : T.Disabled;
            // the track floods with the accent as the knob travels
            Fill(g, r, rad, Theme.MixA(offFill.A == 0 ? Color.FromArgb(0, accent) : offFill, accent, q));
            if (Enabled) Sheen(g, r, rad, q);
            if (q < 1) Stroke(g, r, rad, Theme.Fade(offLine, 1 - q), 1.2f * Ui);

            // knob: small and grey when off, larger and on-accent coloured when on
            float dOff = r.Height - 10 * Ui, dOn = r.Height - 8 * Ui, d = dOff + (dOn - dOff) * q;
            float cOff = r.X + 5 * Ui + dOff / 2, cOn = r.Right - 4 * Ui - dOn / 2;
            float cx = cOff + (cOn - cOff) * p;
            // jelly: stretched along the way it moves, in proportion to speed, and a little flatter
            float speed = Math.Abs(_p.V) * (cOn - cOff) / Math.Max(1f, r.Height);
            float st = Math.Min(0.55f, speed * 0.045f);
            float kw = d * (1 + st), kh = d * (1 - st * 0.3f);
            // past either end (overshoot): squash against the wall instead of leaving the track
            float lo = r.X + 3 * Ui, hi = r.Right - 3 * Ui;
            if (cx + kw / 2 > hi) { kw = Math.Max(d * 0.72f, hi - (cx - kw / 2)); kh = d * (1 + (d - kw) / d * 0.35f); cx = hi - kw / 2; }
            if (cx - kw / 2 < lo) { kw = Math.Max(d * 0.72f, (cx + kw / 2) - lo); kh = d * (1 + (d - kw) / d * 0.35f); cx = lo + kw / 2; }
            kh = Math.Min(kh, r.Height - 2 * Ui);
            var knob = new RectangleF(cx - kw / 2, r.Y + (r.Height - kh) / 2, kw, kh);
            Fill(g, knob, Math.Min(kw, kh) / 2, Theme.MixA(offLine, Theme.On(accent), q));
        }
    }

    sealed class Drop : Owner
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prep(e);
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            float rad = (T.Glass ? 8 : 5) * Ui;
            Fill(g, r, rad, Hovered ? T.CtlHover : T.Ctl);
            Edge(g, r, rad);
            DrawText(g, Text, Font, new RectangleF(S(10), 0, Width - S(34), Height), Enabled ? T.Fg : T.Disabled, ellipsis: true);
            DrawGlyph(g, TBMini.Glyph.Chevron, 10, new Rectangle(Width - S(26), 0, S(18), Height), T.Sub);
        }
    }

    sealed class Chip : Owner
    {
        public string Glyph = "";

        public Chip() { Cursor = Cursors.Default; }

        public int PreferredWidth => S(10) + S(16) + S(4) + Gx.Width(Text ?? "", Font) + S(10);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prep(e);
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Fill(g, r, r.Height / 2f, T.Card);
            Edge(g, r, r.Height / 2f);
            DrawGlyph(g, Glyph, 13, new Rectangle(S(9), 0, S(16), Height), T.Sub);
            DrawText(g, Text, Font, new RectangleF(S(28), 0, Width - S(30), Height), T.Fg);
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

        public Metric() { Cursor = Cursors.Default; }

        static int Measure(string s, Font f) => Gx.Width(s, f);

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
            Edge(g, r, r.Height / 2f);
            DrawGlyph(g, Glyph, 13, new Rectangle(S(7), 0, S(16), Height), T.AccentFg);
            int labelEnd = S(27);
            if (ShowLabel && Label != "")
            {
                DrawText(g, Label, Font, new RectangleF(S(27), 0, Width, Height), T.Sub);
                labelEnd += Measure(Label, Font);
            }
            var vf = ValueFont ?? Font;
            int right = Width - S(10);
            int vw = Measure(Value, vf);
            DrawText(g, Value, vf, new RectangleF(right - vw, 0, vw + S(4), Height), Muted ? T.Sub : T.Fg);
            if (Temp != "")
            {
                // centred in the space between the label and the power value, so the two numbers read separately
                int tw = Measure(Temp, vf);
                int valueStart = right - Math.Max(vw, Templates.Max(t => Measure(t, vf)));
                int tx = (labelEnd + valueStart - tw) / 2;
                tx = Math.Max(labelEnd + S(6), Math.Min(tx, right - vw - S(6) - tw));
                DrawText(g, Temp, vf, new RectangleF(tx, 0, tw + S(4), Height), TempColor());
            }
        }
    }
}
