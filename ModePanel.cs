using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

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
        static readonly string[] LeftSections = { "电池", "电源" };

        readonly TrayApp _app;
        readonly float _s;
        Theme _t = Theme.Load();
        readonly Font _title, _sec, _row, _small, _tileText;
        readonly ToolTip _tip = new ToolTip { InitialDelay = 350, AutoPopDelay = 15000 };

        readonly Label _hTitle;
        readonly Chip _chipPower, _chipGpu;
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
        }

        int S(float v) => (int)Math.Round(v * _s);
        const int ColW = 340, Pad = 16, Gap = 12, RowH = 40, TileH = 62;

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
            Width = S(2 * ColW + Gap + 2 * Pad);

            _hTitle = new Label { Text = "TBMini", Font = _title, AutoSize = true };
            _chipPower = new Chip { Font = _small, Ui = _s };
            _chipGpu = new Chip { Font = _small, Ui = _s, Glyph = Glyph.Chip };
            Controls.AddRange(new Control[] { _hTitle, _chipPower, _chipGpu });

            _secPerf = SectionLabel("性能模式");
            _cardPerf = new Card { Ui = _s };
            for (int i = 0; i < 4; i++)
            {
                var m = PowerModes[i];
                var t = new Tile { Text = Its.Name(m), Glyph = Glyph.ForPower(m), Accent = Draw.ModeColor(m), Font = _tileText, Ui = _s };
                t.Click += (s, e) => _app.SetPower(m);
                _pt[i] = t; _cardPerf.Controls.Add(t);
            }
            _secGpu = SectionLabel("显卡模式");
            _cardGpu = new Card { Ui = _s };
            for (int i = 0; i < 3; i++)
            {
                var g = GpuModes[i];
                var t = new Tile { Text = Gpu.Name(g), Glyph = GpuGlyphs[i], Accent = GpuColors[i], Font = _tileText, Ui = _s };
                t.Click += (s, e) => _app.SetGpu(g);
                _gt[i] = t; _cardGpu.Controls.Add(t);
            }
            _tip.SetToolTip(_pt[0], "省电：低功耗、安静");
            _tip.SetToolTip(_pt[1], "智能：按负载自动调节");
            _tip.SetToolTip(_pt[2], "性能：高功耗释放");
            _tip.SetToolTip(_gt[0], "混合：内屏走核显，独显按需工作（切入/切出独显直连要重启）");
            _tip.SetToolTip(_gt[1], "集显：混合模式下关掉独显，不用重启；独显被占用时不会生效");
            _tip.SetToolTip(_gt[2], "独显直连：内屏直接接独显（MUX），需要重启");
            Controls.AddRange(new Control[] { _secPerf, _cardPerf, _secGpu, _cardGpu });

            _status = new Label { Font = _small, AutoSize = false };
            _link = new LinkLabel { Text = "立即重启", Font = _small, AutoSize = true, Visible = false };
            _link.LinkClicked += (s, e) => { Hide(); _app.RestartNow(); };
            Controls.AddRange(new Control[] { _status, _link });

            Deactivate += (s, e) => { if (!_menuOpen) { HiddenAt = Environment.TickCount; Hide(); } };
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
            ApplyTheme();
            Relayout();
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

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ApplyDwm(); }

        // ------------------------------------------------------------------ feature rows
        Row EnsureRow(Feature f)
        {
            if (_rows.TryGetValue(f.Id, out var r)) return r;
            if (!_sections.TryGetValue(f.Section, out var sec))
            {
                sec = (SectionLabel(f.Section), new Card { Ui = _s });
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

            // header
            _hTitle.Location = new Point(x0 - S(1), S(12));
            int chipH = S(26), cy = S(14);
            _chipGpu.Size = new Size(_chipGpu.PreferredWidth, chipH);
            _chipPower.Size = new Size(_chipPower.PreferredWidth, chipH);
            _chipGpu.Location = new Point(Width - pad - _chipGpu.Width, cy);
            _chipPower.Location = new Point(_chipGpu.Left - S(8) - _chipPower.Width, cy);
            int top = S(56);

            // left column: modes
            int yL = top;
            yL = PlaceTiles(_secPerf, _cardPerf, _pt, x0, yL, colW);
            yL = PlaceTiles(_secGpu, _cardGpu, _gt, x0, yL, colW);
            int yR = top;

            // feature sections (registry order), left/right columns
            foreach (var section in FeatureRegistry.All.Select(f => f.Section).Distinct())
            {
                if (!_sections.TryGetValue(section, out var sec)) continue;
                var rows = FeatureRegistry.All.Where(f => f.Section == section && _rows.ContainsKey(f.Id)).Select(f => _rows[f.Id]).ToList();
                foreach (var r in rows)
                {
                    r.Shown = _app.FeatureStateOf(r.F).Supported == true;
                    r.Label.Visible = r.Shown; r.Ctl.Visible = r.Shown;
                }
                var shown = rows.Where(r => r.Shown).ToList();
                bool left = LeftSections.Contains(section);
                sec.label.Visible = sec.card.Visible = shown.Count > 0;
                if (shown.Count == 0) continue;
                int x = left ? x0 : x1;
                int y = left ? yL : yR;
                sec.label.Location = new Point(x + S(2), y);
                y += S(24);
                int rowH = S(RowH), inX = S(14);
                sec.card.Bounds = new Rectangle(x, y, colW, shown.Count * rowH);
                var dividers = new List<int>();
                for (int i = 0; i < shown.Count; i++)
                {
                    var r = shown[i];
                    int ry = i * rowH;
                    if (i > 0) dividers.Add(ry);
                    Size cs = r.Ctl is Toggle ? new Size(S(40), S(20)) : new Size(S(200), S(28));
                    if (r.Ctl is Segmented sg && sg.Options.Length <= 2) cs.Width = S(150);
                    r.Ctl.Bounds = new Rectangle(colW - inX - cs.Width, ry + (rowH - cs.Height) / 2, cs.Width, cs.Height);
                    r.Label.Bounds = new Rectangle(inX, ry + 1, r.Ctl.Left - inX - S(8), rowH - 2);   // keep dividers visible
                }
                sec.card.Dividers = dividers.ToArray();
                sec.card.Invalidate();
                y = sec.card.Bottom + S(14);
                if (left) yL = y; else yR = y;
            }

            int bottom = Math.Max(yL, yR);
            _status.Bounds = new Rectangle(x0 + S(2), bottom - S(4), Width - 2 * pad, S(22));
            int h = _status.Bottom + S(10);

            var wa = Screen.FromPoint(Visible ? Location : Cursor.Position).WorkingArea;
            int max = wa.Height - S(16);
            if (h > max) { AutoScroll = true; AutoScrollMinSize = new Size(0, h); Height = max; }
            else { AutoScroll = false; Height = h; }
            if (Visible)
            {
                int m = S(8);
                int t = _anchorBottom ? wa.Bottom - m - Height : Math.Min(Top, wa.Bottom - m - Height);
                Top = Math.Max(wa.Top + m, t);
            }
            Invalidate();
        }

        int PlaceTiles(Label sec, Card card, Tile[] tiles, int x, int y, int w)
        {
            sec.Location = new Point(x + S(2), y);
            y += S(24);
            int inset = S(8), gap = S(6), n = tiles.Length;
            card.Bounds = new Rectangle(x, y, w, S(TileH) + 2 * inset);
            int tw = (w - 2 * inset - (n - 1) * gap) / n;
            for (int i = 0; i < n; i++) tiles[i].Bounds = new Rectangle(inset + i * (tw + gap), inset, tw, S(TileH));
            return card.Bottom + S(14);
        }

        // ------------------------------------------------------------------ theme
        void ApplyTheme()
        {
            _t = Theme.Load();
            BackColor = _t.Bg;
            _hTitle.ForeColor = _t.Fg; _hTitle.BackColor = _t.Bg;
            foreach (var c in new[] { _chipPower, _chipGpu }) c.T = _t;
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
            _tip.SetToolTip(_pt[3], geekOk ? "极客：最高功耗（仅接电源）" : "极客模式只能在接电源时使用");

            var gi = _app.GpuInfo;
            for (int i = 0; i < 3; i++)
            {
                _gt[i].Selected = gi.Mode == GpuModes[i];
                _gt[i].Enabled = !busy;
                _gt[i].Invalidate();
            }

            // header chips
            var ps = SystemInformation.PowerStatus;
            bool ac = _app.OnAc;
            string pct = ps.BatteryLifePercent <= 1f ? $"{(int)Math.Round(ps.BatteryLifePercent * 100)}%" : "";
            _chipPower.Glyph = ac ? Glyph.Charging : Glyph.BatteryLevel(ps.BatteryLifePercent);
            _chipPower.Text = (ac ? "接电源" : "电池") + (pct != "" ? " · " + pct : "");
            string pend = _app.PendingText();
            _chipGpu.Text = _app.GpuText() + (pend != null ? " → " + pend : "");
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

                switch (r.Ctl)
                {
                    case Toggle t: t.On = st.Value == 1; t.Unknown = st.Value < 0; t.Enabled = !st.Busy; break;
                    case Segmented sg: sg.Selected = st.Value; sg.Enabled = !st.Busy; break;
                    case Drop d: d.Text = st.Value >= 0 && st.Value < f.Options.Length ? f.Options[st.Value] : "—"; d.Enabled = !st.Busy; break;
                }
                r.Ctl.Invalidate();
            }
            if (layout) Relayout();
            else
            {   // chips may have changed width
                int pad = S(Pad);
                _chipGpu.Width = _chipGpu.PreferredWidth; _chipPower.Width = _chipPower.PreferredWidth;
                _chipGpu.Left = Width - pad - _chipGpu.Width; _chipPower.Left = _chipGpu.Left - S(8) - _chipPower.Width;
            }

            string line = "";
            if (!Its.Supported) line = "没找到联想 ITS 服务，性能模式不可用";
            if (busy) line = Join(line, "切换中…");
            if (!Program.IsAdmin) line = Join(line, "显卡和 BIOS 项需要管理员权限（点显卡按钮可提权）");
            else if (gi.Error != null) line = Join(line, "显卡：" + gi.Error);
            else if (!gi.RunningKnown) line = Join(line, "内屏未点亮，显卡按 BIOS 显示");
            if (pend != null) line = Join(line, "重启后显卡切到「" + pend + "」");
            if (line == "") line = "Ctrl+Alt+Shift+T 随时重新打开触摸板";
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
            int y = _anchorBottom ? wa.Bottom - Height - m : wa.Top + m;
            Location = new Point(x, Math.Max(wa.Top + m, y));
            Show();
            Activate();
            SetForegroundWindow(Handle);
        }

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

#if TBMINI_DEV
        /// Test hook: draw the panel (current state) into a PNG without showing it on screen.
        public void RenderTo(string path)
        {
            ApplyTheme();
            UpdateView();
            Relayout();
            Location = new Point(-20000, -20000);
            Show();
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
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, S(9) + gh, Width, Height - S(9) - gh - S(4)), fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
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
