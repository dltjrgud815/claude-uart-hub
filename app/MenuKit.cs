// 트레이 메뉴 모양(UI_COPY.md "메뉴 모양", 2026-11-06): Claude 데스크톱 앱의 트레이 메뉴 계열.
//   어둡게·밝게는 Windows 앱 테마를 따른다(메뉴를 열 때마다 다시 읽음, Palette 는 창과 같은 벌). 아이콘·체크 표시·그러데이션 없음.
//   줄 높이 32px(정보 줄 28px) · 왼쪽 여백 16px · 맑은 고딕 9.5pt · 하위 메뉴 화살표 = 회색 꺾쇠 · 마우스를 올린 줄 = 양옆 4px 안쪽의 둥근 사각형.
//   주황(#E67240)은 제목 줄의 별과 연결된 포트의 점에만 쓴다. 선택 표시(지금 고른 터미널)는 글자색 점.
//   모서리: 윈도 11 은 DWM 이 둥글게 깎고 테두리·그림자를 그린다(DWMWA_WINDOW_CORNER_PREFERENCE). 윈도 10 은 네모 + 1px 테두리 그대로.
// 크기: 기본 ToolStripDropDownMenu 는 줄마다 제 너비를 쓰고(늘이지 않음) 메뉴 너비는 내부 계산으로 정한다. 그래서 줄(MenuRow·MenuSep)은 모두
//   같은 너비(그 메뉴에서 가장 긴 줄, MenuRenderer.RowWidth)를 내고, 메뉴(MenuRoot·MenuDropDown)는 그 너비 + 위아래 4px 를 낸다. 글·점·별·꺾쇠는 MenuRenderer 가 그린다.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace UartHub.App
{
    /// <summary>메뉴 줄 종류: 누르는 줄 · 제목(별 + 굵게) · 정보(회색, 누를 수 없음) · 연결된 포트(주황 점 + 회색).</summary>
    enum RowKind { Action, Title, Info, Port }

    /// <summary>
    /// 메뉴 한 벌의 모양(색·글꼴·배율). 주 메뉴와 하위 메뉴가 같은 것을 나눠 쓴다. 색은 Refresh 때(주 메뉴를 열 때) Windows 앱 테마로 다시 정한다.
    /// </summary>
    sealed class MenuStyle : IDisposable
    {
        public Palette Pal = Palette.Light;
        /// <summary>render-ui 가 화면 배율을 흉내 낼 때만 1 이 아니다.</summary>
        public readonly float Zoom;
        public readonly Font Font, Bold;
        public readonly MenuRenderer Renderer;
        /// <summary>DWM 이 모서리를 둥글게 깎았다(윈도 11) — 테두리도 DWM 이 그리므로 렌더러는 그리지 않는다.</summary>
        public bool Rounded;
        /// <summary>render-ui: DWM 을 부르지 않고 테두리도 그리지 않는다(그림에서 윈도 11 모서리를 따로 흉내 낸다).</summary>
        public bool Preview;

        public MenuStyle(float zoom = 1f)
        {
            Zoom = zoom <= 0 ? 1f : zoom;
            Font = Theme.Ui(9.5f * Zoom);
            Bold = Theme.Ui(9.5f * Zoom, FontStyle.Bold);
            Renderer = new MenuRenderer(this);
            Refresh();
        }

        /// <summary>Windows 앱 테마를 다시 읽는다.</summary>
        public void Refresh() => Pal = Theme.Current;

        /// <summary>논리 픽셀(96 DPI) → 이 메뉴의 픽셀 배율.</summary>
        public float Scale(ToolStrip ts) => (ts?.DeviceDpi ?? 96) / 96f * Zoom;

        /// <summary>
        /// 드롭다운(주 메뉴·하위 메뉴)에 모양을 입힌다: 렌더러 · 여백 칸 없음 · 열 때마다 테마 색과 DWM 모서리·테두리.
        /// root = 주 메뉴(열 때 테마를 다시 읽는다).
        /// </summary>
        public void Attach(ToolStripDropDown dd, bool root = false)
        {
            dd.Renderer = Renderer;
            if (dd is ToolStripDropDownMenu m) { m.ShowImageMargin = false; m.ShowCheckMargin = false; }
            dd.Font = Font;
            dd.ShowItemToolTips = true;
            dd.DropShadowEnabled = true;
            Apply(dd);
            dd.HandleCreated += (s, e) => Chrome(dd);
            dd.Opening += (s, e) =>
            {
                if (root) Refresh();
                Apply(dd);
                Chrome(dd);
                dd.Invalidate();
            };
        }

        void Apply(ToolStripDropDown dd)
        {
            dd.BackColor = Pal.MenuBack;
            dd.ForeColor = Pal.Text;
        }

        void Chrome(ToolStripDropDown dd)
        {
            if (Preview || !dd.IsHandleCreated) return;
            if (Dwm.Round(dd.Handle)) Rounded = true;
            Dwm.Border(dd.Handle, Pal.Border);
        }

        public void Dispose()
        {
            Font.Dispose();
            Bold.Dispose();
        }
    }

    /// <summary>메뉴 줄. 높이·너비를 직접 정한다(글 너비 + 왼쪽 16px + 점·별 자리 + 오른쪽 여백·꺾쇠 자리).</summary>
    class MenuRow : ToolStripMenuItem
    {
        public readonly RowKind Kind;
        /// <summary>정보 줄을 포트 줄의 글 머리에 맞춰 들여 쓴다("외 N개").</summary>
        public bool Indent;
        /// <summary>Checked 일 때 점을 주황으로(연결된 COM). 아니면 글자색 점(지금 고른 터미널).</summary>
        public bool AccentMark;
        /// <summary>포트 줄의 점 색을 테마에 맞춰 바꾼다(에이징 세션 줄: 기록 중 초록 · 출력 없음 빨강). null = 주황(연결된 포트).</summary>
        public Func<Palette, Color> DotColor;

        public MenuRow(string text, RowKind kind = RowKind.Action) : base(text)
        {
            Kind = kind;
            if (kind != RowKind.Action)
            {
                Enabled = false;                                             // 누를 수 없음(클릭·키보드 선택 없음)
                AccessibleRole = AccessibleRole.StaticText;
            }
        }

        public override bool CanSelect => Kind == RowKind.Action && Enabled && base.CanSelect;

        /// <summary>이 줄만 놓고 본 너비: 왼쪽 16px + 점·별 자리 + 글 + 오른쪽 여백(하위 메뉴가 있으면 꺾쇠 자리).</summary>
        public int Natural(MenuStyle st, float k)
        {
            var f = Kind == RowKind.Title ? st.Bold : st.Font;
            int tw = string.IsNullOrEmpty(Text) ? 0 : TextRenderer.MeasureText(Text, f, new Size(int.MaxValue, int.MaxValue), MenuRenderer.Measure).Width;
            return (int)Math.Round(MenuRenderer.Left * k) + MenuRenderer.Lead(this, k) + tw + (int)Math.Round((HasDropDownItems ? MenuRenderer.ArrowZone : MenuRenderer.Right) * k);
        }

        public override Size GetPreferredSize(Size constrainingSize)
        {
            if (!(Owner?.Renderer is MenuRenderer r)) return base.GetPreferredSize(constrainingSize);
            float k = r.Style.Scale(Owner);
            return new Size(r.RowWidth(Owner), (int)Math.Round((Kind == RowKind.Info || Kind == RowKind.Port ? 28 : 32) * k));
        }

        MenuDropDown own;

        /// <summary>하위 메뉴도 같은 너비 규칙(MenuDropDown).</summary>
        protected override ToolStripDropDown CreateDefaultDropDown() => own = new MenuDropDown(this);

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) { own?.Dispose(); own = null; }
        }
    }

    /// <summary>주 메뉴: 너비 = 줄 너비, 위아래 4px.</summary>
    sealed class MenuRoot : ContextMenuStrip
    {
        public override Size GetPreferredSize(Size proposedSize) => MenuRenderer.Box(this, base.GetPreferredSize(proposedSize));
        public override Rectangle DisplayRectangle => MenuRenderer.Inner(this, base.DisplayRectangle);
    }

    /// <summary>하위 메뉴: 주 메뉴와 같은 규칙.</summary>
    sealed class MenuDropDown : ToolStripDropDownMenu
    {
        public MenuDropDown(ToolStripItem owner) { OwnerItem = owner; }
        public override Size GetPreferredSize(Size proposedSize) => MenuRenderer.Box(this, base.GetPreferredSize(proposedSize));
        public override Rectangle DisplayRectangle => MenuRenderer.Inner(this, base.DisplayRectangle);
    }

    /// <summary>가는 구분선 줄(높이 9px).</summary>
    sealed class MenuSep : ToolStripSeparator
    {
        public override Size GetPreferredSize(Size constrainingSize)
        {
            if (!(Owner?.Renderer is MenuRenderer r)) return base.GetPreferredSize(constrainingSize);
            float k = r.Style.Scale(Owner);
            return new Size(r.RowWidth(Owner), (int)Math.Round(9 * k));
        }
    }

    /// <summary>메뉴 그리기(ToolStripRenderer 를 처음부터 — Professional 의 그러데이션·이미지 여백을 쓰지 않는다).</summary>
    sealed class MenuRenderer : ToolStripRenderer
    {
        public const float Left = 16, Right = 24, ArrowZone = 40, Inset = 4, Radius = 5, Dot = 6, DotZone = 14, StarSize = 16, StarGap = 8;
        public const TextFormatFlags Measure = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        const TextFormatFlags Draw = Measure | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;

        public readonly MenuStyle Style;
        public MenuRenderer(MenuStyle style) { Style = style; }

        /// <summary>한 메뉴의 줄 너비: 보이는 줄 중 가장 긴 것(최소 주 메뉴 232px · 하위 메뉴 180px).</summary>
        public int RowWidth(ToolStrip ts)
        {
            float k = Style.Scale(ts);
            int w = (int)Math.Round((ts is ContextMenuStrip ? 232 : 180) * k);
            foreach (ToolStripItem it in ts.Items)
                if (it.Available && it is MenuRow r) w = Math.Max(w, r.Natural(Style, k));
            return w;
        }

        static int Edge(ToolStrip ts) => ts.Renderer is MenuRenderer r ? (int)Math.Round(2 * r.Style.Scale(ts)) : 0;

        /// <summary>메뉴 크기: 너비 = 줄 너비, 높이 = 기본 계산 + 위아래 덧댄 여백(Inner).</summary>
        public static Size Box(ToolStrip ts, Size baseSize)
            => ts.Renderer is MenuRenderer r ? new Size(r.RowWidth(ts), baseSize.Height + 2 * Edge(ts)) : baseSize;

        /// <summary>줄을 놓는 칸: 기본(위아래 2px)에 2px 씩 더해 위아래 4px.</summary>
        public static Rectangle Inner(ToolStrip ts, Rectangle r)
        {
            int e = Edge(ts);
            return new Rectangle(r.X, r.Y + e, r.Width, Math.Max(0, r.Height - 2 * e));
        }

        /// <summary>글 앞 자리: 제목 = 별 + 간격, 포트 = 점, "외 N개" = 점 자리, 누르는 줄 = 같은 메뉴에 선택 점이 하나라도 있으면 점 자리.</summary>
        public static int Lead(ToolStripItem it, float k)
        {
            int P(float v) => (int)Math.Round(v * k);
            var row = it as MenuRow;
            switch (row?.Kind ?? RowKind.Action)
            {
                case RowKind.Title: return P(StarSize + StarGap);
                case RowKind.Port: return P(DotZone);
                case RowKind.Info: return row.Indent ? P(DotZone) : 0;
            }
            bool marks = it.Owner != null && it.Owner.Items.OfType<ToolStripMenuItem>().Any(x => x.Available && x.Checked);
            return marks ? P(DotZone) : 0;
        }

        static bool Hot(ToolStripItem it)
        {
            if (!it.Enabled || (it is MenuRow r && r.Kind != RowKind.Action)) return false;
            return it.Selected || it.Pressed || (it is ToolStripMenuItem m && m.HasDropDownItems && m.DropDown.Visible);
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(Style.Pal.MenuBack);
            e.Graphics.FillRectangle(b, new Rectangle(Point.Empty, e.ToolStrip.Size));
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (Style.Rounded || Style.Preview) return;                                      // 윈도 11 은 DWM 이 테두리를 그린다
            using var p = new Pen(Style.Pal.Border);
            e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }
        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var it = e.Item;
            if (!Hot(it)) return;
            float k = Style.Scale(e.ToolStrip);
            var g = e.Graphics;
            var r = new RectangleF(Inset * k, Math.Max(1f, k), it.Width - 2 * Inset * k, it.Height - 2 * Math.Max(1f, k));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(r, Radius * k))
            using (var b = new SolidBrush(Style.Pal.Hover))
                g.FillPath(b, path);
            g.SmoothingMode = SmoothingMode.None;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var it = e.Item;
            var g = e.Graphics;
            var pal = Style.Pal;
            float k = Style.Scale(e.ToolStrip);
            int P(float v) => (int)Math.Round(v * k);
            var row = it as MenuRow;
            var kind = row?.Kind ?? RowKind.Action;
            int x = P(Left), h = it.Height;
            float cy = h / 2f;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (kind == RowKind.Title)
            {
                var star = Theme.Star(P(StarSize));
                g.DrawImage(star, x, (h - star.Height) / 2, star.Width, star.Height);
            }
            else if (kind == RowKind.Port || (it is ToolStripMenuItem mi && mi.Checked && kind == RowKind.Action))
            {
                Color c = row?.DotColor != null ? row.DotColor(pal) : kind == RowKind.Port || (row?.AccentMark ?? false) ? Palette.Accent : pal.Text;
                float d = Dot * k;
                using var b = new SolidBrush(c);
                g.FillEllipse(b, x + P(1), cy - d / 2f, d, d);
            }
            g.SmoothingMode = SmoothingMode.None;
            x += Lead(it, k);
            int right = it.Width - P(it is ToolStripMenuItem m2 && m2.HasDropDownItems ? ArrowZone : Right) + P(8);
            var font = kind == RowKind.Title ? Style.Bold : Style.Font;
            var color = kind == RowKind.Title ? pal.Text : kind != RowKind.Action || !it.Enabled ? pal.Muted : pal.Text;
            TextRenderer.DrawText(g, it.Text, font, new Rectangle(x, 0, Math.Max(1, right - x), h), color, Draw);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            var it = e.Item;
            if (it == null) return;
            float k = Style.Scale(it.Owner);
            float cx = it.Width - (Left + 4) * k, cy = it.Height / 2f, a = 3.5f * k;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(Style.Pal.Muted, Math.Max(1.2f, 1.3f * k)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(p, new[] { new PointF(cx - a * 0.5f, cy - a), new PointF(cx + a * 0.5f, cy), new PointF(cx - a * 0.5f, cy + a) });
            g.SmoothingMode = SmoothingMode.None;
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var it = e.Item;
            float k = Style.Scale(e.ToolStrip);
            int x0 = (int)Math.Round(12 * k), x1 = it.Width - (int)Math.Round(12 * k), y = it.Height / 2;
            using var b = new SolidBrush(Style.Pal.Sep);
            e.Graphics.FillRectangle(b, x0, y, Math.Max(1, x1 - x0), Math.Max(1, (int)Math.Round(k)));
        }
    }
}
