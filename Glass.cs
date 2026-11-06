using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TBMini
{
    // =============================================================================================
    // "Liquid glass" look: the Windows 11 acrylic backdrop (live blur of what is behind the window, the material
    // Windows uses for its own flyouts and menus) under translucent cards. Everything drawn on top must keep
    // per-pixel alpha, so all text goes through GDI+ (GDI text turns transparent on the backdrop).
    // Needs Windows 11 22H2+ and "Transparency effects" on; otherwise the opaque theme is used.
    // =============================================================================================
    static class Glass
    {
        [StructLayout(LayoutKind.Sequential)] struct Margins { public int Left, Right, Top, Bottom; }
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref Margins m);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34,
                  DWMWA_SYSTEMBACKDROP_TYPE = 38, DWMSBT_NONE = 1, DWMSBT_TRANSIENTWINDOW = 3;
        public const int CornerRound = 2, CornerRoundSmall = 3;

        public static bool Win11 => Environment.OSVersion.Version.Build >= 22000;

        /// The system backdrop attribute exists from Windows 11 22H2 (build 22621).
        public static bool Supported => Environment.OSVersion.Version.Build >= 22621;

        /// Off when the user turned off "Transparency effects" in Windows settings.
        public static bool TransparencyOn =>
            !(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1) is int v && v == 0);

        /// Applies the window frame: rounded corners, dark/light frame, border colour, and the acrylic backdrop when
        /// glass is on (or removes it when off, e.g. after the transparency setting changed).
        public static void Apply(IntPtr hwnd, Theme t, int corner = CornerRound)
        {
            if (!Win11 || hwnd == IntPtr.Zero) return;
            int dark = t.Dark ? 1 : 0, border = t.FrameBorder.R | (t.FrameBorder.G << 8) | (t.FrameBorder.B << 16);
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, 4);
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, 4);
            if (!Supported) return;
            int backdrop = t.Glass ? DWMSBT_TRANSIENTWINDOW : DWMSBT_NONE;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, 4);
            var m = t.Glass ? new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 } : new Margins();
            DwmExtendFrameIntoClientArea(hwnd, ref m);
        }

        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        public const int WM_NCACTIVATE = 0x86;

        /// The acrylic backdrop falls back to a solid colour while a window looks inactive. Menus never become the
        /// active window, and the panel is inactive while one of its menus is open, so glass windows always keep
        /// the active look (they close when the user clicks elsewhere anyway).
        public static void KeepActiveLook(Control c, Func<bool> enabled)
        {
            void Hook(IntPtr h) { new ActiveLook(enabled).AssignHandle(h); if (enabled()) SendMessage(h, WM_NCACTIVATE, (IntPtr)1, IntPtr.Zero); }
            if (c.IsHandleCreated) Hook(c.Handle);
            c.HandleCreated += (o, e) => Hook(((Control)o).Handle);     // a recreated handle needs its own hook
        }

        /// Call after showing: marks the window active-looking even if it did not take the foreground.
        public static void ShowActive(Control c)
        {
            if (c.IsHandleCreated) SendMessage(c.Handle, WM_NCACTIVATE, (IntPtr)1, IntPtr.Zero);
        }

        sealed class ActiveLook : NativeWindow
        {
            readonly Func<bool> _enabled;
            public ActiveLook(Func<bool> enabled) { _enabled = enabled; }
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_NCACTIVATE && _enabled()) m.WParam = (IntPtr)1;
                base.WndProc(ref m);
            }
        }

        /// DWM drops the acrylic backdrop of a window that was hidden and shown again, and ignores setting the same
        /// value: switch it off and back on (frame extension too) right after the window is shown.
        public static void Renew(IntPtr hwnd, Theme t)
        {
            if (!Supported || !t.Glass || hwnd == IntPtr.Zero) return;
            int none = DWMSBT_NONE, transient = DWMSBT_TRANSIENTWINDOW;
            var zero = new Margins(); var all = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, 4);
            DwmExtendFrameIntoClientArea(hwnd, ref zero);
            DwmExtendFrameIntoClientArea(hwnd, ref all);
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref transient, 4);
        }

        /// Background of a child control: transparent on glass (the backdrop shows), plus the card tint when the
        /// control sits on a card, so it blends with the card around it.
        public static void Backdrop(Graphics g, Control c, Theme t)
        {
            if (!t.Glass) { g.Clear(c.Parent is Card ? t.Card : (c.Parent?.BackColor ?? t.Bg)); return; }
            Frost(g, c.ClientRectangle, t);
            if (c.Parent is Card)
            {
                var sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.None;   // see Frost
                using (var b = new SolidBrush(t.Card)) g.FillRectangle(b, c.ClientRectangle);
                g.SmoothingMode = sm;
            }
        }

        /// The window-level glass: transparent, then the frost tint (the same on the form, cards and every child).
        /// Filled without anti-aliasing: with it, GDI+ covers the first row and column only half, which left a faint
        /// line along the top and left edge of every control on glass.
        public static void Frost(Graphics g, Rectangle r, Theme t)
        {
            g.Clear(Color.Transparent);
            if (t.Tint.A == 0) return;
            var sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.None;
            using (var b = new SolidBrush(t.Tint)) g.FillRectangle(b, r);
            g.SmoothingMode = sm;
        }
    }

    /// GDI+ text with alpha preserved (grayscale anti-aliasing on glass, ClearType on opaque backgrounds).
    static class Gx
    {
        static readonly Graphics Measurer = Graphics.FromImage(new Bitmap(1, 1));
        static readonly object Lock = new object();

        static StringFormat Format(StringAlignment h, StringAlignment v, bool ellipsis)
        {
            var f = (StringFormat)StringFormat.GenericTypographic.Clone();
            f.Alignment = h; f.LineAlignment = v;
            f.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
            f.Trimming = ellipsis ? StringTrimming.EllipsisCharacter : StringTrimming.None;
            return f;
        }

        public static void Text(Graphics g, string s, Font font, RectangleF r, Color c, Theme t,
                                StringAlignment h = StringAlignment.Near, StringAlignment v = StringAlignment.Center, bool ellipsis = false)
        {
            if (string.IsNullOrEmpty(s)) return;
            g.TextRenderingHint = t.Glass ? TextRenderingHint.AntiAliasGridFit : TextRenderingHint.ClearTypeGridFit;
            using var f = Format(h, v, ellipsis);
            using var b = new SolidBrush(c);
            g.DrawString(s, font, b, r, f);
        }

        public static SizeF Measure(string s, Font font)
        {
            if (string.IsNullOrEmpty(s)) return SizeF.Empty;
            lock (Lock)
            {
                using var f = Format(StringAlignment.Near, StringAlignment.Near, false);
                return Measurer.MeasureString(s, font, PointF.Empty, f);
            }
        }

        public static int Width(string s, Font font) => (int)Math.Ceiling(Measure(s, font).Width);

        /// An icon-font glyph centred in r.
        public static void Glyph(Graphics g, string glyph, float px, RectangleF r, Color c, Theme t)
        {
            using var f = new Font(Theme.IconFont, px, GraphicsUnit.Pixel);
            Text(g, glyph, f, r, c, t, StringAlignment.Center, StringAlignment.Center);
        }
    }

    /// Text label drawn with GDI+ so it works on the glass backdrop. Optional link behaviour (hand, underline on hover).
    sealed class GlassLabel : Control, IThemed
    {
        Theme _t = Theme.Load();
        public Theme T { get => _t; set { _t = value; Invalidate(); } }
        public bool AutoFit;                                   // keep the size fitted to the text
        public bool Link;
        public StringAlignment Align = StringAlignment.Near;
        bool _hover;

        public GlassLabel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.Selectable, false);
        }

        public override Size GetPreferredSize(Size proposed)
        {
            var s = Gx.Measure(Text, Font);
            return new Size((int)Math.Ceiling(s.Width) + 2, (int)Math.Ceiling(Math.Max(s.Height, Font.GetHeight())) + 2);
        }

        void Fit() { if (AutoFit) Size = GetPreferredSize(Size.Empty); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Fit(); Invalidate(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Fit(); Invalidate(); }
        /// Like LinkLabel: only a left click counts (Control raises Click for right/middle button-up too).
        protected override void OnClick(EventArgs e)
        {
            if (e is MouseEventArgs m && m.Button != MouseButtons.Left) return;
            base.OnClick(e);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); if (Link) { _hover = true; Cursor = Cursors.Hand; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hover) { _hover = false; Invalidate(); } }
        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Glass.Backdrop(g, this, _t);
            if (Link && _hover)
                using (var u = new Font(Font, Font.Style | FontStyle.Underline))
                    Gx.Text(g, Text, u, ClientRectangle, ForeColor, _t, Align, StringAlignment.Center, ellipsis: true);
            else Gx.Text(g, Text, Font, ClientRectangle, ForeColor, _t, Align, StringAlignment.Center, ellipsis: true);
        }
    }

    /// Menus in the panel's theme: glass backdrop (or the opaque card colour), rounded corners, rounded hover
    /// highlight, accent check marks. Used for the tray menu, its submenus and the panel's dropdowns.
    static class GlassMenu
    {
        static readonly Font MenuFont = new Font("Microsoft YaHei UI", 9f);

        /// Per-menu state: the window hooks are attached once, the theme can change between openings.
        sealed class Hooked { public Theme T; }
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ToolStripDropDown, Hooked> State =
            new System.Runtime.CompilerServices.ConditionalWeakTable<ToolStripDropDown, Hooked>();

        /// Safe to call every time a (reused) menu is rebuilt.
        public static void Style(ToolStripDropDown menu, Theme t)
        {
            float s = menu.DeviceDpi / 96f;
            menu.Renderer = new Renderer(t, s);
            menu.Font = MenuFont;
            menu.DropShadowEnabled = !Glass.Win11;             // Windows 11 draws its own shadow for rounded windows
            menu.Padding = new Padding((int)(4 * s), (int)(5 * s), (int)(4 * s), (int)(5 * s));
            if (menu is ToolStripDropDownMenu dm) { dm.ShowImageMargin = false; dm.ShowCheckMargin = true; }

            if (State.TryGetValue(menu, out var st)) st.T = t;
            else
            {
                st = new Hooked { T = t };
                State.Add(menu, st);
                var state = st;
                menu.HandleCreated += (o, e) => Glass.Apply(((Control)o).Handle, state.T, Glass.CornerRound);
                menu.Opened += (o, e) => { if (state.T.Glass) { Glass.Renew(((Control)o).Handle, state.T); Glass.ShowActive((Control)o); } };
                Glass.KeepActiveLook(menu, () => state.T.Glass);
            }
            if (menu.IsHandleCreated) Glass.Apply(menu.Handle, t, Glass.CornerRound);
            StyleItems(menu.Items, t, s);
        }

        static void StyleItems(ToolStripItemCollection items, Theme t, float s)
        {
            foreach (ToolStripItem it in items)
            {
                if (it is ToolStripSeparator) continue;
                it.Padding = new Padding(0, (int)(5 * s), 0, (int)(5 * s));
                it.ForeColor = t.Fg;
                if (it is ToolStripMenuItem mi && mi.HasDropDownItems) Style(mi.DropDown, t);
            }
        }

        sealed class Renderer : ToolStripRenderer
        {
            readonly Theme _t; readonly float _s;
            public Renderer(Theme t, float s) { _t = t; _s = s; }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                if (_t.Glass) Glass.Frost(e.Graphics, new Rectangle(Point.Empty, e.ToolStrip.Size), _t);
                else e.Graphics.Clear(_t.MenuBg);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                if (Glass.Win11) return;                         // DWM draws the rounded border
                using var p = new Pen(_t.FrameBorder);
                e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            }

            protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected) return;
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new RectangleF(2 * _s, 0.5f * _s, e.Item.Width - 4 * _s, e.Item.Height - 1 * _s);
                using var path = Draw.RoundRect(r, 5 * _s);
                if (e.Item.Enabled) { using var b = new SolidBrush(_t.CtlHover); g.FillPath(b, path); }
                else { using var p = new Pen(_t.Divider, Math.Max(1f, _s)); g.DrawPath(p, path); }   // keyboard focus on a disabled item
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                var c = e.Item.Enabled ? _t.Fg : _t.Sub;
                var r = new RectangleF(e.TextRectangle.X, 0, e.Item.Width - e.TextRectangle.X - 8 * _s, e.Item.Height);
                Gx.Text(e.Graphics, e.Text, e.TextFont, r, c, _t, StringAlignment.Near, StringAlignment.Center, ellipsis: true);
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                var r = e.ImageRectangle;
                var box = new RectangleF(r.X, 0, r.Width, e.Item.Height);
                Gx.Glyph(e.Graphics, "", 15 * _s, box, _t.AccentFg, _t);                                  // CheckMark
                Gx.Glyph(e.Graphics, "", 15 * _s, new RectangleF(box.X + 0.6f * _s, box.Y, box.Width, box.Height), _t.AccentFg, _t);   // drawn twice: bolder
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                var r = e.ArrowRectangle;
                Gx.Glyph(e.Graphics, "", 11 * _s, new RectangleF(r.X - 4 * _s, r.Y, r.Width, r.Height), e.Item != null && e.Item.Enabled ? _t.Sub : _t.Disabled, _t);   // ChevronRight
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                float y = e.Item.Height / 2f;
                using var p = new Pen(_t.Divider, Math.Max(1f, _s * 0.75f));
                e.Graphics.DrawLine(p, 10 * _s, y, e.Item.Width - 10 * _s, y);
            }
        }
    }
}
