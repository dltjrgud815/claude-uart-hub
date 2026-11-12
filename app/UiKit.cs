// 창 공통 모양(UI_COPY.md "모양", 2026-11-06): Claude 데스크톱 앱 계열. 머리띠 없이 본문 위쪽에 제목 줄(주황 별 18px + 굵은 제목) ·
// 첫 줄(12pt 굵게) · 본문(맑은 고딕 9pt) · 요약 줄(주황 점) · 작은 회색 줄 · "자세히 ▸"(접힌 고정폭 기술 정보) · 바닥 띠의 오른쪽 아래 둥근 납작 버튼.
// 색은 Windows 앱 테마(밝게·어둡게)를 따른다(Palette — 트레이 메뉴와 같은 색). 주황(#E67240)은 별·요약 점·안전한 기본 동작 버튼에만 쓴다.
// 윈도 11: DWM 으로 창 모서리를 둥글게, 어두운 테마면 제목 표시줄도 어둡게. 윈도 10 은 DWM 호출이 실패하고 그대로(네모) 둔다.
// 배치는 손으로 한다(Relayout): 글 높이는 그릴 때와 같은 TextRenderer 플래그로 재고, 픽셀 값은 Px()(= DeviceDpi/96 × Zoom)로 늘린다.
//   AutoScaleMode.Dpi 를 두되 AutoScaleDimensions 는 비워(배율 1) 이중 확대를 막고, 핸들 생성·DpiChanged(PerMonitorV2 — 다른 배율 모니터로 옮김)마다
//   글꼴을 (Zoom × DeviceDpi/시스템 DPI)로 다시 만들어 Relayout 한다. Zoom 은 render-ui 가 화면 배율을 흉내 낼 때만 1 이 아니다(150% = 1.5).
// Snapshot(): 창을 띄우지 않고(Show 없음) 화면 밖 위치에서 핸들만 만든 뒤 컨트롤마다 DrawToBitmap 으로 겹쳐 그린다(render-ui 검토용).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace UartHub.App
{
    /// <summary>
    /// 색 한 벌(밝게·어둡게). 창 본문·바닥 띠와 트레이 메뉴가 같은 벌을 쓴다. 창은 만들 때, 트레이 메뉴는 열 때마다 Theme.Current 를 다시 읽는다.
    /// </summary>
    sealed class Palette
    {
        public static readonly Color Accent = Color.FromArgb(0xE6, 0x72, 0x40);          // 별 주황
        public bool Dark;
        public Color Body, Footer, Text, Muted, Faint, Rule, Well, WellText, Link, Bad;
        /// <summary>에이징 기록 중 점(tray_rec.ico 의 초록 #2EAD5B, 어두운 테마는 바탕에서 보이게 밝게).</summary>
        public Color Rec;
        public Color MenuBack, Hover, Sep, Border;
        public Color Button, ButtonHover, ButtonPress, ButtonLine, ButtonText, ButtonOff, ButtonOffText;
        public Color AccentHover, AccentPress, OnAccent;
        public Color Caption, CaptionText, Backdrop;

        public static readonly Palette Light = new Palette
        {
            Dark = false,
            Body = Hex(0xFFFFFF), Footer = Hex(0xF7F7F7), Text = Hex(0x1F1F1F), Muted = Hex(0x6B6B6B), Faint = Hex(0xCFCFCF), Rule = Hex(0xE2E2E2),
            Well = Hex(0xF3F3F3), WellText = Hex(0x2B2B2B), Link = Hex(0x2B5A8C), Bad = Hex(0xB42318), Rec = Hex(0x2EAD5B),
            MenuBack = Hex(0xF9F9F9), Hover = Hex(0xEAEAEA), Sep = Hex(0xE2E2E2), Border = Hex(0xD4D4D4),
            Button = Hex(0xFFFFFF), ButtonHover = Hex(0xF3F3F3), ButtonPress = Hex(0xEAEAEA), ButtonLine = Hex(0xD0D0D0), ButtonText = Hex(0x1F1F1F),
            ButtonOff = Hex(0xF5F5F5), ButtonOffText = Hex(0xA3A3A3),
            AccentHover = Hex(0xD9662F), AccentPress = Hex(0xC75A28), OnAccent = Hex(0xFFFFFF),
            Caption = Hex(0xF3F3F3), CaptionText = Hex(0x1A1A1A), Backdrop = Hex(0xDCE0E5),
        };

        public static readonly Palette DarkSet = new Palette
        {
            Dark = true,
            Body = Hex(0x2B2B2B), Footer = Hex(0x202020), Text = Hex(0xF2F2F2), Muted = Hex(0x9A9A9A), Faint = Hex(0x575757), Rule = Hex(0x3D3D3D),
            Well = Hex(0x1F1F1F), WellText = Hex(0xD6D6D6), Link = Hex(0x8AB4E8), Bad = Hex(0xFF7B72), Rec = Hex(0x4CC77A),
            MenuBack = Hex(0x2B2B2B), Hover = Hex(0x3A3A3A), Sep = Hex(0x3D3D3D), Border = Hex(0x454545),
            Button = Hex(0x383838), ButtonHover = Hex(0x424242), ButtonPress = Hex(0x323232), ButtonLine = Hex(0x4A4A4A), ButtonText = Hex(0xF2F2F2),
            ButtonOff = Hex(0x303030), ButtonOffText = Hex(0x6E6E6E),
            AccentHover = Hex(0xEE7F4F), AccentPress = Hex(0xD56638), OnAccent = Hex(0xFFFFFF),
            Caption = Hex(0x202020), CaptionText = Hex(0xFFFFFF), Backdrop = Hex(0x15171A),
        };

        static Color Hex(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
    }

    static class Theme
    {
        public static Color Accent => Palette.Accent;

        /// <summary>render-ui 만 쓴다: 레지스트리 대신 이 값(true = 어둡게)으로 테마를 정한다.</summary>
        public static bool? Forced;

        /// <summary>Windows 앱 테마가 어둡게인가(HKCU ...\Themes\Personalize AppsUseLightTheme = 0). 값이 없거나 못 읽으면 밝게.</summary>
        public static bool IsDark()
        {
            if (Forced.HasValue) return Forced.Value;
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch (Exception) { return false; }
        }

        public static Palette Current => IsDark() ? Palette.DarkSet : Palette.Light;

        const string Face = "Malgun Gothic", MonoFace = "Consolas";
        static string face;
        static readonly object gate = new object();
        static Image star;
        static readonly Dictionary<int, Bitmap> stars = new Dictionary<int, Bitmap>();

        /// <summary>본문 글꼴 이름: 맑은 고딕(없으면 시스템 메시지 글꼴).</summary>
        public static string FaceName
        {
            get
            {
                lock (gate)
                {
                    if (face != null) return face;
                    try { using var f = new FontFamily(Face); face = Face; }
                    catch (ArgumentException) { face = SystemFonts.MessageBoxFont.FontFamily.Name; }
                    return face;
                }
            }
        }

        public static Font Ui(float pt, FontStyle st = FontStyle.Regular) => new Font(FaceName, pt, st, GraphicsUnit.Point);
        public static Font Mono(float pt) => new Font(MonoFace, pt, FontStyle.Regular, GraphicsUnit.Point);

        public static Stream Res(string name) => typeof(Theme).Assembly.GetManifestResourceStream(name) ?? throw new FileNotFoundException("리소스 없음: " + name);

        /// <summary>.ico 리소스에서 가장 알맞은 크기의 아이콘.</summary>
        public static Icon Icon(string name, Size size)
        {
            using var s = Res(name);
            return new Icon(s, size);
        }

        /// <summary>창 아이콘(UartHub.ico — 여러 크기를 담은 채로, 창이 작은·큰 아이콘을 고른다).</summary>
        public static Icon AppIcon()
        {
            using var s = Res("UartHub.ico");
            return new Icon(s);
        }

        /// <summary>별(star.png)을 px 크기로 — 반씩 줄여 가다 마지막에 고품질 바이큐빅(큰 그림을 한 번에 줄이면 거칠다). 크기마다 한 번 만든다.</summary>
        public static Bitmap Star(int px)
        {
            lock (gate)
            {
                if (stars.TryGetValue(px, out var b)) return b;
                if (star == null)
                {
                    using var s = Res("star.png");
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    ms.Position = 0;
                    using var raw = Image.FromStream(ms);
                    star = new Bitmap(raw);                                      // 스트림과 떨어진 사본
                }
                Image cur = star;
                bool own = false;
                while (cur.Width > px * 2)
                {
                    var half = Draw(cur, Math.Max(px * 2, cur.Width / 2), Math.Max(px * 2 * cur.Height / cur.Width, cur.Height / 2));
                    if (own) cur.Dispose();
                    cur = half; own = true;
                }
                int h = (int)Math.Round(px * (double)star.Height / star.Width);
                b = Draw(cur, px, h);
                if (own) cur.Dispose();
                return stars[px] = b;
            }
        }

        static Bitmap Draw(Image src, int w, int h)
        {
            var b = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(b);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            using var ia = new ImageAttributes();
            ia.SetWrapMode(WrapMode.TileFlipXY);                              // 가장자리 번짐 없게
            g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
            return b;
        }

        /// <summary>GDI 가 포인트 → 픽셀에 쓰는 DPI(시스템 DPI).</summary>
        public static float SystemDpi
        {
            get
            {
                try { using var g = Graphics.FromHwnd(IntPtr.Zero); return g.DpiY; }
                catch (Exception) { return 96f; }
            }
        }

        /// <summary>둥근 사각형 경로(반지름 r). 버튼·메뉴 강조·render-ui 의 창 모서리 흉내에 쓴다.</summary>
        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Max(0.1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>
    /// DWM 창 꾸밈(윈도 11 만 듣는 값은 윈도 10 에서 실패를 돌려준다 — 실패하면 그대로 둔다).
    /// 33 DWMWA_WINDOW_CORNER_PREFERENCE(2 = 둥글게) · 34 DWMWA_BORDER_COLOR · 20 DWMWA_USE_IMMERSIVE_DARK_MODE(옛 윈도 10 은 19).
    /// </summary>
    static class Dwm
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        static bool Set(IntPtr h, int attr, int v)
        {
            if (h == IntPtr.Zero) return false;
            try { return DwmSetWindowAttribute(h, attr, ref v, sizeof(int)) == 0; }
            catch (Exception) { return false; }                               // dwmapi 없음·진입점 없음
        }

        /// <summary>모서리 둥글게(윈도 11). 성공하면 true.</summary>
        public static bool Round(IntPtr h) => Set(h, 33, 2);

        /// <summary>DWM 이 그리는 1px 테두리 색(윈도 11).</summary>
        public static void Border(IntPtr h, Color c) => Set(h, 34, c.R | (c.G << 8) | (c.B << 16));

        /// <summary>제목 표시줄을 어둡게(또는 밝게).</summary>
        public static void DarkCaption(IntPtr h, bool dark)
        {
            if (!Set(h, 20, dark ? 1 : 0)) Set(h, 19, dark ? 1 : 0);
        }

        /// <summary>어두운 테마의 스크롤 막대(글 상자 등). 실패하면 그대로.</summary>
        public static void DarkScrollbars(IntPtr h, bool dark)
        {
            if (h == IntPtr.Zero) return;
            try { SetWindowTheme(h, dark ? "DarkMode_Explorer" : "Explorer", null); }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// 줄 나누기(한국어 keep-all): 띄어쓰기에서만 나누고 낱말 안(음절 사이)에서는 나누지 않는다 — GDI 의 WordBreak 는 한글 음절 사이도 끊어 "할까/요?" 가 된다.
    /// 한 줄보다 긴 낱말(경로 등)만 글자 단위로, 되도록 \ / - _ . 뒤에서 자른다. 재기와 그리기가 같은 플래그(NoPadding)를 쓴다.
    /// </summary>
    static class Wrap
    {
        public const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.Left | TextFormatFlags.Top;
        static readonly Size Inf = new Size(int.MaxValue, int.MaxValue);
        static readonly char[] Seps = { '\\', '/', '-', '_', '.', ',' };

        public static int LineHeight(Font f) => TextRenderer.MeasureText("Ag가", f, Inf, Flags).Height;
        static int W(string s, Font f) => s.Length == 0 ? 0 : TextRenderer.MeasureText(s, f, Inf, Flags).Width;

        public static List<string> Lines(string text, Font f, int width)
        {
            var res = new List<string>();
            width = Math.Max(1, width);
            foreach (var para in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                string cur = "";
                foreach (var w0 in para.Split(' '))
                {
                    string w = w0;
                    string cand = cur.Length == 0 ? w : cur + " " + w;
                    if (W(cand, f) <= width) { cur = cand; continue; }
                    if (cur.Length > 0) { res.Add(cur); cur = ""; }
                    while (w.Length > 1 && W(w, f) > width)
                    {
                        int n = w.Length - 1;
                        while (n > 1 && W(w.Substring(0, n), f) > width) n--;
                        int sep = w.LastIndexOfAny(Seps, n - 1);
                        if (sep >= n / 2 && sep > 0) n = sep + 1;
                        res.Add(w.Substring(0, n));
                        w = w.Substring(n);
                    }
                    cur = w;
                }
                res.Add(cur);
            }
            return res;
        }

        public static int Height(string text, Font f, int width) => string.IsNullOrEmpty(text) ? 0 : Lines(text, f, width).Count * LineHeight(f);

        public static void Draw(Graphics g, string text, Font f, Rectangle r, Color fore, Color back)
        {
            int lh = LineHeight(f), y = r.Top;
            foreach (var l in Lines(text, f, r.Width))
            {
                if (l.Length > 0) TextRenderer.DrawText(g, l, f, new Point(r.Left, y), fore, back, Flags);
                y += lh;
            }
        }
    }

    /// <summary>글 한 덩어리(줄바꿈은 Wrap — 띄어쓰기에서만). 잴 때와 그릴 때 같은 계산이라 잘리지 않는다.</summary>
    class TextBlock : Control
    {

        public TextBlock()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            AccessibleRole = AccessibleRole.StaticText;
        }

        public int Measure(int width) => Wrap.Height(Text, Font, width);

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            AccessibleName = Text;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e) => Wrap.Draw(e.Graphics, Text, Font, ClientRectangle, ForeColor, BackColor);
    }

    /// <summary>요약 줄: 주황 점 + 들여 쓴 글(줄이 넘치면 글 머리에 맞춰 이어진다).</summary>
    sealed class BulletList : Control
    {
        readonly HubDialog owner;
        public List<string> Items = new List<string>();

        public BulletList(HubDialog owner)
        {
            this.owner = owner;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            AccessibleRole = AccessibleRole.List;
            BackColor = owner.Pal.Body;
            ForeColor = owner.Pal.Text;
        }

        int Indent => owner.Px(16);
        int Gap => owner.Px(6);

        public int Measure(int width)
        {
            int h = 0;
            foreach (var t in Items) h += Wrap.Height(t, Font, width - Indent) + Gap;
            return Items.Count > 0 ? h - Gap : 0;
        }

        public void SetItems(IEnumerable<string> items)
        {
            Items = items.ToList();
            AccessibleName = string.Join(" / ", Items);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            int y = 0, line = Wrap.LineHeight(Font);
            float d = Math.Max(4f, owner.Px(5));
            foreach (var t in Items)
            {
                int h = Wrap.Height(t, Font, Width - Indent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(Palette.Accent)) g.FillEllipse(b, owner.Px(2), y + line / 2f - d / 2f, d, d);
                g.SmoothingMode = SmoothingMode.None;
                Wrap.Draw(g, t, Font, new Rectangle(Indent, y, Width - Indent, h), ForeColor, BackColor);
                y += h + Gap;
            }
        }
    }

    /// <summary>제목 줄(머리띠 대신): 본문 바탕 위에 주황 별 18px + 굵은 제목(한 줄, 넘치면 …).</summary>
    sealed class TitleRow : Control
    {
        readonly HubDialog owner;
        public TitleRow(HubDialog owner)
        {
            this.owner = owner;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            BackColor = owner.Pal.Body;
            ForeColor = owner.Pal.Text;
            AccessibleRole = AccessibleRole.TitleBar;
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); AccessibleName = Text; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            int s = owner.Px(18);
            var star = Theme.Star(s);
            g.DrawImage(star, 0, (Height - star.Height) / 2, star.Width, star.Height);
            int tx = s + owner.Px(8);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(tx, 0, Width - tx, Height), ForeColor, BackColor,
                                  TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>바닥 띠: 본문보다 한 단계 짙은(밝은 테마) · 어두운(어두운 테마) 바탕 + 위쪽 가는 선.</summary>
    sealed class FooterBand : Panel
    {
        readonly Palette pal;
        public FooterBand(Palette pal)
        {
            this.pal = pal;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = pal.Footer;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var p = new Pen(pal.Rule);
            e.Graphics.DrawLine(p, 0, 0, Width, 0);
        }
    }

    /// <summary>
    /// 둥근 납작 버튼(시스템 버튼은 어두운 테마에서 밝게 떠 보여서 직접 그린다). Primary = 주황 바탕 + 흰 글(안전한 기본 동작에만 —
    /// "끊고 연결"·"제거" 같은 되돌릴 수 없는 동작은 Primary 로 만들지 않는다). 그 밖 = 테마 바탕 + 가는 테두리, 기본 버튼(Enter)은 테두리를 진하게.
    /// </summary>
    sealed class FlatButton : Button
    {
        readonly HubDialog owner;
        public readonly bool Primary;
        bool hover, down, isDefault;

        public FlatButton(HubDialog owner, bool primary)
        {
            this.owner = owner;
            Primary = primary;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            BackColor = owner.Pal.Footer;
            ForeColor = primary ? owner.Pal.OnAccent : owner.Pal.ButtonText;
            Cursor = Cursors.Default;
        }

        public override void NotifyDefault(bool value) { base.NotifyDefault(value); isDefault = value; Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); down = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = owner.Pal;
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? p.Footer);
            Color fill, line, text;
            if (!Enabled) { fill = p.ButtonOff; line = p.ButtonLine; text = p.ButtonOffText; }
            else if (Primary) { fill = down ? p.AccentPress : hover ? p.AccentHover : Palette.Accent; line = fill; text = p.OnAccent; }
            else { fill = down ? p.ButtonPress : hover ? p.ButtonHover : p.Button; line = isDefault ? p.Muted : p.ButtonLine; text = p.ButtonText; }
            float r = owner.Px(6);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), r))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                using (var pen = new Pen(line, 1f)) g.DrawPath(pen, path);
            }
            if (Focused && ShowFocusCues)
                using (var path = Theme.Round(new RectangleF(2.5f, 2.5f, Width - 5f, Height - 5f), Math.Max(1f, r - 2f)))
                using (var pen = new Pen(Primary && Enabled ? p.OnAccent : p.Text, 1f))
                    g.DrawPath(pen, path);
            g.SmoothingMode = SmoothingMode.None;
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>"자세히 ▸" 펼치기: 글 + 작은 삼각형(펼치면 아래로). 마우스·Space·Enter 로 바꾼다.</summary>
    sealed class Expander : Control
    {
        readonly HubDialog owner;
        bool expanded, hover;
        public event EventHandler Toggled;

        public Expander(HubDialog owner)
        {
            this.owner = owner;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            Text = "자세히";
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleName = "자세히";
            ForeColor = owner.Pal.Link;
        }

        [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Expanded
        {
            get => expanded;
            set { expanded = value; AccessibleDescription = value ? "펼침" : "접힘"; Invalidate(); }
        }

        public Size Measure()
        {
            var t = TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            return new Size(t.Width + owner.Px(18), t.Height + owner.Px(8));
        }

        void Toggle()
        {
            Expanded = !expanded;
            Toggled?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnClick(EventArgs e) { base.OnClick(e); Focus(); Toggle(); }
        protected override bool IsInputKey(Keys k) => k == Keys.Enter || k == Keys.Space || base.IsInputKey(k);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { e.Handled = true; Toggle(); }
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using var f = hover ? new Font(Font, FontStyle.Underline) : null;
            var t = TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            int ty = (Height - t.Height) / 2;
            TextRenderer.DrawText(g, Text, f ?? Font, new Point(0, ty), ForeColor, BackColor, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            // 삼각형: 접힘 ▸ / 펼침 ▾
            float a = owner.Px(4.5f), cx = t.Width + owner.Px(8), cy = ty + t.Height / 2f + owner.Px(0.5f);
            PointF[] tri = expanded
                ? new[] { new PointF(cx - a, cy - a * 0.55f), new PointF(cx + a, cy - a * 0.55f), new PointF(cx, cy + a * 0.6f) }
                : new[] { new PointF(cx - a * 0.55f, cy - a), new PointF(cx - a * 0.55f, cy + a), new PointF(cx + a * 0.6f, cy) };
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(ForeColor)) g.FillPolygon(b, tri);
            g.SmoothingMode = SmoothingMode.None;
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, new Rectangle(0, 0, Width, Height), ForeColor, BackColor);
        }
    }

    /// <summary>
    /// 진행 단계(세로): 끝남 = 글자색 원 + 바탕색 체크 · 지금 = 주황 고리(돌아가는 호) + 굵은 글 · 남음 = 옅은 고리 + 회색 글 · 실패 = 붉은 원 + 바탕색 ×. 원 사이를 가는 선이 잇는다.
    /// </summary>
    sealed class StepList : Control
    {
        readonly HubDialog owner;
        readonly Timer spin = new Timer { Interval = 40 };
        string[] names = new string[0];
        int current = -1;
        bool failed;
        float angle;
        /// <summary>지금 단계 글꼴(굵게 — 창이 DPI 에 맞춰 다시 만든 것을 그때그때 받는다).</summary>
        public Func<Font> Bold;

        public StepList(HubDialog owner)
        {
            this.owner = owner;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            BackColor = owner.Pal.Body;
            AccessibleRole = AccessibleRole.List;
            spin.Tick += (s, e) => { angle = (angle + 12) % 360; Invalidate(); };
        }

        public int Count => names.Length;
        int Row => owner.Px(30);
        public int Measure() => names.Length * Row;

        public void Set(string[] n) { names = n ?? new string[0]; current = -1; failed = false; Changed(); }
        public void At(int i) { current = Math.Max(current, Math.Min(i, names.Length - 1)); Changed(); }
        public void Done() { current = names.Length; Changed(); }
        public void Fail() { failed = true; if (current < 0) current = 0; Changed(); }

        void Changed()
        {
            AccessibleName = string.Join(", ", names.Select((n, i) => n + " " + (i < current ? "끝남" : i == current ? (failed ? "실패" : "진행 중") : "남음")));
            bool run = current >= 0 && current < names.Length && !failed && IsHandleCreated && Visible;
            if (run) spin.Start(); else spin.Stop();
            Invalidate();
        }

        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); Changed(); }
        protected override void Dispose(bool disposing) { if (disposing) spin.Dispose(); base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var pal = owner.Pal;
            float d = owner.Px(16), cx = owner.Px(2) + d / 2f, pen = Math.Max(1.5f, owner.Px(2));
            int tx = owner.Px(30);
            for (int i = 0; i < names.Length; i++)
            {
                float top = i * Row, cy = top + Row / 2f;
                var circle = new RectangleF(cx - d / 2f, cy - d / 2f, d, d);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (i < names.Length - 1)
                    using (var ln = new Pen(i < current ? pal.Muted : pal.Faint, Math.Max(1f, owner.Px(1.5f))))
                        g.DrawLine(ln, cx, cy + d / 2f + owner.Px(3), cx, cy + Row - d / 2f - owner.Px(3));
                bool done = i < current, now = i == current;
                if (done)
                {
                    using (var b = new SolidBrush(pal.Text)) g.FillEllipse(b, circle);
                    using var w = new Pen(pal.Body, pen) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                    g.DrawLines(w, new[] { new PointF(cx - d * 0.24f, cy + d * 0.01f), new PointF(cx - d * 0.06f, cy + d * 0.19f), new PointF(cx + d * 0.25f, cy - d * 0.17f) });
                }
                else if (now && failed)
                {
                    using (var b = new SolidBrush(pal.Bad)) g.FillEllipse(b, circle);
                    using var w = new Pen(pal.Body, pen) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    float k = d * 0.2f;
                    g.DrawLine(w, cx - k, cy - k, cx + k, cy + k);
                    g.DrawLine(w, cx - k, cy + k, cx + k, cy - k);
                }
                else if (now)
                {
                    var r = RectangleF.Inflate(circle, -pen / 2f, -pen / 2f);
                    using (var ring = new Pen(Color.FromArgb(70, Palette.Accent), pen)) g.DrawEllipse(ring, r);
                    using (var arc = new Pen(Palette.Accent, pen) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(arc, r, angle - 90, 110);
                }
                else
                {
                    var r = RectangleF.Inflate(circle, -pen / 2f, -pen / 2f);
                    using var ring = new Pen(pal.Faint, pen);
                    g.DrawEllipse(ring, r);
                }
                g.SmoothingMode = SmoothingMode.None;
                var f = now ? Bold?.Invoke() ?? Font : Font;
                var col = now ? (failed ? pal.Bad : pal.Text) : done ? pal.Text : pal.Muted;
                TextRenderer.DrawText(g, names[i], f, new Rectangle(tx, (int)top, Width - tx, Row), col, BackColor,
                                      TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }
    }

    /// <summary>
    /// 공통 창: 제목 줄 · 본문 줄(위에서 아래로) · 펼치는 자세히 · 바닥 띠(왼쪽 = 자세히 또는 남은 시간, 오른쪽 = 버튼).
    /// 하위 창은 AddRow 로 본문 줄을 쌓고 AddButton 으로 버튼을 왼쪽부터 더한다. 크기·위치는 Relayout 이 정한다.
    /// 색 한 벌(Pal)은 만들 때 Windows 앱 테마로 정하고 창이 떠 있는 동안 바꾸지 않는다.
    /// </summary>
    class HubDialog : Form
    {
        public readonly float Zoom;
        public readonly Palette Pal;
        protected readonly TitleRow Header;
        protected readonly FooterBand Footer;
        readonly List<(Control C, float Gap, Func<int, int> Measure)> rows = new List<(Control, float, Func<int, int>)>();
        readonly List<Button> buttons = new List<Button>();
        protected Control FooterLeft;
        protected Panel Well;
        protected TextBox WellText;
        protected Expander More;
        protected int ContentW = 440;
        protected int WellLines = 7;
        protected Font FBody, FHead, FNote, FMono, FTitle, FBold;
        readonly List<(Control C, Func<Font> F)> fonted = new List<(Control, Func<Font>)>();

        public HubDialog(string title, float zoom = 1f)
        {
            Zoom = zoom <= 0 ? 1f : zoom;
            Pal = Theme.Current;
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.Dpi;                 // AutoScaleDimensions 비움 = 배율 1(Px 가 DPI 를 맡는다)
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Pal.Body;
            ForeColor = Pal.Text;
            KeyPreview = false;
            try { Icon = Theme.AppIcon(); } catch (Exception) { }
            MakeFonts();
            Font = FBody;
            Header = new TitleRow(this) { Text = title };
            Bind(Header, () => FTitle);
            Footer = new FooterBand(Pal);
            Controls.Add(Header);
            Controls.Add(Footer);
            // 다른 배율 모니터로 옮김(PerMonitorV2): WinForms 의 자동 확대를 막고(Cancel) 모든 처리가 끝난 뒤 글꼴·배치를 새 DPI 로 다시 만든다
            DpiChanged += (s, e) =>
            {
                e.Cancel = true;
                var at = e.SuggestedRectangle.Location;
                BeginInvoke(new Action(() => { MakeFonts(); ApplyFonts(); Relayout(); Location = at; }));
            };
            ResumeLayout(false);
        }

        /// <summary>논리 픽셀(96 DPI 기준) → 지금 창의 픽셀.</summary>
        public float S => DeviceDpi / 96f * Zoom;
        public int Px(float v) => (int)Math.Round(v * S);

        void MakeFonts()
        {
            float k = Zoom * DeviceDpi / Theme.SystemDpi;
            foreach (var f in new[] { FBody, FHead, FNote, FMono, FTitle, FBold }) f?.Dispose();
            FBody = Theme.Ui(9f * k);
            FBold = Theme.Ui(9f * k, FontStyle.Bold);
            FHead = Theme.Ui(12f * k, FontStyle.Bold);
            FNote = Theme.Ui(8.5f * k);
            FMono = Theme.Mono(8.5f * k);
            FTitle = Theme.Ui(9.5f * k, FontStyle.Bold);
        }

        void ApplyFonts()
        {
            Font = FBody;
            foreach (var (c, f) in fonted) c.Font = f();
        }

        /// <summary>글꼴을 정해 두면 DPI 가 바뀔 때 다시 입힌다.</summary>
        protected T Bind<T>(T c, Func<Font> f) where T : Control
        {
            c.Font = f();
            fonted.Add((c, f));
            return c;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Dwm.Round(Handle);                                  // 윈도 11: 둥근 모서리(윈도 10 은 실패 → 그대로)
            Dwm.DarkCaption(Handle, Pal.Dark);                  // 어두운 테마면 제목 표시줄도 어둡게
            if (Math.Abs(DeviceDpi - Theme.SystemDpi) > 0.5f) { MakeFonts(); ApplyFonts(); }
            Relayout();
        }

        // ---------------------------------------------------------------- 본문 줄
        protected void AddRow(Control c, float gap, Func<int, int> measure)
        {
            rows.Add((c, gap, measure));
            Controls.Add(c);
        }

        protected TextBlock AddText(string text, Func<Font> font, Color color, float gap)
        {
            var t = Bind(new TextBlock { Text = text ?? "", ForeColor = color, BackColor = Pal.Body }, font);
            AddRow(t, gap, t.Measure);
            if (string.IsNullOrEmpty(text)) SetShown(t, false);
            return t;
        }

        protected TextBlock AddHead(string text, bool error = false) => AddText(text, () => FHead, error ? Pal.Bad : Pal.Text, 0);
        protected TextBlock AddBody(string text, float gap = 8) => AddText(text, () => FBody, Pal.Text, gap);
        protected TextBlock AddNote(string text, float gap = 10) => AddText(text, () => FNote, Pal.Muted, gap);

        protected BulletList AddBullets(IEnumerable<string> items, float gap = 12)
        {
            var b = Bind(new BulletList(this), () => FBody);
            b.SetItems(items ?? new string[0]);
            AddRow(b, gap, b.Measure);
            if (b.Items.Count == 0) SetShown(b, false);
            return b;
        }

        /// <summary>UiText 의 머리·본문·요약 줄·작은 줄을 본문 줄로(자세히는 SetDetails).</summary>
        protected (TextBlock Head, TextBlock Body, BulletList Bullets, TextBlock Note) AddText(UiText t)
        {
            var h = AddHead(t.Head, t.Error);
            var b = AddBody(t.Body, t.Head != null ? 8 : 0);
            var l = AddBullets(t.Bullets, t.Head != null || t.Body != null ? 12 : 0);
            var n = AddNote(t.Note);
            if (t.Details.Count > 0) SetDetails(t.Details);
            return (h, b, l, n);
        }

        // ---------------------------------------------------------------- 자세히
        protected void SetDetails(IEnumerable<string> lines)
        {
            EnsureWell();
            WellText.Text = string.Join("\r\n", lines);
        }

        protected void AppendDetail(string line)
        {
            EnsureWell();
            if (WellText.TextLength > 200000) WellText.Text = WellText.Text.Substring(WellText.TextLength - 100000);
            WellText.AppendText((WellText.TextLength > 0 ? "\r\n" : "") + line);
        }

        void EnsureWell()
        {
            if (Well != null) return;
            Well = new Panel { BackColor = Pal.Well };
            SetShown(Well, false);
            WellText = Bind(new TextBox
            {
                Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Pal.Well, ForeColor = Pal.WellText,
                ScrollBars = ScrollBars.Vertical, WordWrap = true, TabStop = false, AccessibleName = "자세히",
            }, () => FMono);
            if (Pal.Dark) WellText.HandleCreated += (s, e) => Dwm.DarkScrollbars(WellText.Handle, true);
            Well.Controls.Add(WellText);
            Controls.Add(Well);
            More = Bind(new Expander(this) { BackColor = Pal.Footer }, () => FBody);
            More.Toggled += (s, e) => { SetShown(Well, More.Expanded); Relayout(); if (More.Expanded) { WellText.SelectionStart = WellText.TextLength; WellText.ScrollToCaret(); } };
            if (FooterLeft == null) SetFooterLeft(More);
        }

        /// <summary>펼침 상태를 바꾼다(render-ui).</summary>
        public void Expand(bool on)
        {
            if (More == null) return;
            More.Expanded = on;
            SetShown(Well, on);
            Relayout();
        }

        protected void SetFooterLeft(Control c)
        {
            if (FooterLeft != null && FooterLeft != c) Footer.Controls.Remove(FooterLeft);
            FooterLeft = c;
            if (c != null && !Footer.Controls.Contains(c)) Footer.Controls.Add(c);
        }

        // ---------------------------------------------------------------- 버튼
        /// <param name="primary">주황 버튼: 안전한 기본 동작에만(되돌릴 수 없는 동작·취소에는 쓰지 않는다).</param>
        protected Button AddButton(string text, Action click, bool primary = false)
        {
            var b = Bind(new FlatButton(this, primary) { Text = text, AutoSize = false }, () => primary ? FBold : FBody);
            if (click != null) b.Click += (s, e) => click();
            buttons.Add(b);
            Footer.Controls.Add(b);
            for (int i = 0; i < buttons.Count; i++) buttons[i].TabIndex = i;
            return b;
        }

        // ---------------------------------------------------------------- 배치
        public void Relayout()
        {
            SuspendLayout();
            int pad = Px(24), w = Px(ContentW), total = w + pad * 2;
            Header.SetBounds(pad, Px(18), w, Px(24));
            int y = Header.Bottom + Px(14);
            bool first = true;
            foreach (var (c, gap, measure) in rows)
            {
                if (!Own(c)) continue;
                if (!first) y += Px(gap);
                int h = measure(w);
                c.SetBounds(pad, y, w, h);
                y += h;
                first = false;
            }
            y += Px(22);
            if (Well != null && Own(Well))
            {
                int lineH = TextRenderer.MeasureText("Ag가", WellText.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Height;
                int ih = lineH * WellLines + Px(4);
                Well.SetBounds(pad, y - Px(6), w, ih + Px(16));
                WellText.SetBounds(Px(10), Px(8), w - Px(14), ih);
                y = Well.Bottom + Px(16);
            }
            int bh = Px(32), fh = bh + Px(28);
            Footer.SetBounds(0, y, total, fh);
            int x = total - pad;
            for (int i = buttons.Count - 1; i >= 0; i--)
            {
                var b = buttons[i];
                if (!Own(b)) continue;
                int bw = Math.Max(Px(88), TextRenderer.MeasureText(b.Text, b.Font).Width + Px(32));
                x -= bw;
                b.SetBounds(x, (fh - bh) / 2, bw, bh);
                x -= Px(8);
            }
            if (FooterLeft != null)
            {
                Size s = FooterLeft is Expander ex ? ex.Measure()
                       : FooterLeft is TextBlock tb ? new Size(Math.Max(1, x - pad), tb.Measure(Math.Max(1, x - pad)))
                       : FooterLeft.Size;
                FooterLeft.SetBounds(FooterLeft is Expander ? pad - Px(2) : pad, (fh - s.Height) / 2, s.Width, s.Height);
            }
            ClientSize = new Size(total, y + fh);
            ResumeLayout(false);
            Invalidate(true);
        }

        // 보임 상태는 창이 따로 기록한다: Control.Visible 은 부모(창)가 숨어 있으면 늘 false 라, 띄우기 전 배치·그림(render-ui)에서 쓸 수 없다.
        readonly HashSet<Control> off = new HashSet<Control>();

        /// <summary>이 컨트롤 자신을 보일 것인가(부모가 숨어 있어도).</summary>
        protected bool Own(Control c) => !off.Contains(c);

        /// <summary>컨트롤을 숨기거나 보인다. 숨기기·보이기는 모두 이것으로(Relayout·Snapshot 이 이 상태를 본다).</summary>
        protected void SetShown(Control c, bool on)
        {
            if (on) off.Remove(c); else off.Add(c);
            c.Visible = on;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var f in new[] { FBody, FHead, FNote, FMono, FTitle, FBold }) f?.Dispose();
            base.Dispose(disposing);
        }

        // ---------------------------------------------------------------- 그림(render-ui)
        /// <summary>
        /// 창을 띄우지 않고 그린다: 화면 밖 위치에서 핸들만 만들고(Show 없음) 컨트롤마다 DrawToBitmap 을 겹친다.
        /// 위에 테마에 맞춘 제목 표시줄 모형을 얹고, 윈도 11 처럼 바깥 모서리를 둥글게 깎아 1px 테두리를 두른다(바깥은 투명).
        /// </summary>
        public Bitmap Snapshot()
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Handles(this);
            Relayout();
            BeforeSnapshot();
            var client = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(client)) g.Clear(BackColor);
            Compose(this, client, Point.Empty);
            int cap = Px(32);
            var flat = new Bitmap(client.Width, client.Height + cap, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(flat))
            {
                using (var cb = new SolidBrush(Pal.Caption)) g.FillRectangle(cb, 0, 0, client.Width, cap);
                using (var ico = Theme.Icon("UartHub.ico", new Size(Px(16), Px(16))))
                    g.DrawIcon(ico, new Rectangle(Px(10), (cap - Px(16)) / 2, Px(16), Px(16)));
                using (var cf = new Font("Segoe UI", 9f * Zoom))
                    TextRenderer.DrawText(g, Text, cf, new Rectangle(Px(34), 0, client.Width - Px(80), cap), Pal.CaptionText, Pal.Caption,
                                          TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                using (var pen = new Pen(Pal.CaptionText, Math.Max(1f, Px(1))))
                {
                    float cx = client.Width - Px(23), cy = cap / 2f, k = Px(5);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.DrawLine(pen, cx - k, cy - k, cx + k, cy + k);
                    g.DrawLine(pen, cx - k, cy + k, cx + k, cy - k);
                    g.SmoothingMode = SmoothingMode.None;
                }
                g.DrawImage(client, 0, cap);
            }
            client.Dispose();
            var full = new Bitmap(flat.Width, flat.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(full))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                var rect = new RectangleF(0, 0, flat.Width, flat.Height);
                using (var path = Theme.Round(rect, Px(8)))
                using (var tb = new TextureBrush(flat))
                    g.FillPath(tb, path);
                using (var path = Theme.Round(new RectangleF(0.5f, 0.5f, flat.Width - 1f, flat.Height - 1f), Px(8) - 0.5f))
                using (var pen = new Pen(Pal.Border, 1f))
                    g.DrawPath(pen, path);
            }
            flat.Dispose();
            return full;
        }

        /// <summary>그리기 직전(기본 버튼 표시 등).</summary>
        protected virtual void BeforeSnapshot() { }

        static void Handles(Control c)
        {
            _ = c.Handle;
            foreach (Control k in c.Controls) Handles(k);
        }

        void Compose(Control parent, Bitmap bmp, Point origin)
        {
            for (int i = parent.Controls.Count - 1; i >= 0; i--)          // z 순서 아래부터
            {
                var c = parent.Controls[i];
                if (!c.IsHandleCreated || !Own(c) || c.Width <= 0 || c.Height <= 0) continue;
                var at = new Point(origin.X + c.Left, origin.Y + c.Top);
                c.DrawToBitmap(bmp, new Rectangle(at, c.Size));
                Compose(c, bmp, at);
            }
        }
    }
}
