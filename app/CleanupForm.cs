// 미연결 UART 정리 창(트레이 설정 ▸ 미연결 UART 정리…, UI_COPY.md "미연결 UART 정리", 2026-11-06): 지금 PC 에 꽂혀 있지 않은데 포트 번호 기록이 남은 UART 를
// 한 줄씩(체크 상자 — 처음엔 모두 체크) 보여 주고, [기록 지우기]로 체크한 것의 기록만 지운다. 목록 읽기·지우기는 트레이가 백그라운드에서
// 감독(Supervisor.RememberedCables·ForgetCables)에 시키고, 결과 풍선("UART 기록 N개를 지웠습니다.")도 트레이가 띄운다.
//   기본 버튼(처음 포커스·Enter·Esc) = [취소]. [기록 지우기]는 되돌릴 수 없는 동작이라 주황이 아니고, 체크한 것이 없으면 꺼진다.
//   목록이 비었으면 "정리할 UART가 없습니다." + [닫기], 허브가 꺼졌거나 답이 없으면 그 글 + [닫기].
//   화면 글에 "잊기"·forget 을 쓰지 않는다(코어 명령 이름일 뿐).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace UartHub.App
{
    sealed class CleanupForm : HubDialog
    {
        public const string Title = "미연결 UART 정리";
        readonly Action<List<string>, Action> remove;
        readonly CheckList list;
        readonly Button go, cancel;
        readonly TextBlock status;
        bool quiet;

        /// <param name="rows">꽂혀 있지 않은 기억된 UART(null = 읽지 못함 → error).</param>
        /// <param name="remove">(체크한 id, 끝나면 부를 것) — 트레이가 백그라운드에서 지우고 풍선을 띄운 뒤 끝을 알린다.</param>
        public CleanupForm(IReadOnlyList<RememberedCable> rows, string error, Action<List<string>, Action> remove, float zoom = 1f) : base(Title, zoom)
        {
            this.remove = remove;
            ContentW = 420;
            SuspendLayout();
            if (rows == null)
            {
                AddHead(error ?? "허브가 응답하지 않습니다.");
                cancel = AddButton("닫기", Close);
            }
            else if (rows.Count == 0)
            {
                AddHead("정리할 UART가 없습니다.");
                cancel = AddButton("닫기", Close);
            }
            else
            {
                AddHead("PC에서 빠져 있는 UART 기록입니다.");
                AddBody("지금 PC에 꽂혀 있지 않은 UART의 포트 번호 기록만 지웁니다. 꽂혀 있는 UART는 연결 여부와 상관없이 여기에 나오지 않습니다. "
                        + "나중에 다시 꽂으면 새 UART로 보고 새 번호를 받습니다.");
                list = Bind(new CheckList(this), () => FBody);
                foreach (var r in rows)
                {
                    var row = new CheckRow(this, r.Id, Line(r), r.Id, () => FBody, () => FNote);
                    row.CheckedChanged += (s, e) => Armed();
                    list.Add(row);
                }
                AddRow(list, 14, list.Arrange);
                status = AddNote("", 10);
                go = AddButton("기록 지우기", Go);
                cancel = AddButton("취소", Close);
            }
            AcceptButton = cancel;                                           // Enter·Esc·처음 포커스 = 취소(닫기)
            CancelButton = cancel;
            ActiveControl = cancel;
            ResumeLayout(false);
            Relayout();
            Shown += (s, e) => cancel.Focus();
        }

        /// <summary>"포트 7002 · EVB-B2 · COM5" — 모르는 칸은 뺀다.</summary>
        static string Line(RememberedCable r)
        {
            var parts = new List<string>();
            if (r.Port > 0) parts.Add("포트 " + r.Port.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(r.Board)) parts.Add(r.Board.Trim());
            if (!string.IsNullOrWhiteSpace(r.Com)) parts.Add(r.Com.Trim());
            return parts.Count > 0 ? string.Join(" · ", parts) : r.Id;
        }

        void Armed()
        {
            if (go != null) go.Enabled = list.Rows.Any(r => r.Checked);
        }

        void Go()
        {
            var ids = list.Rows.Where(r => r.Checked).Select(r => r.Id).ToList();
            if (ids.Count == 0) return;
            go.Enabled = cancel.Enabled = false;
            foreach (var r in list.Rows) r.Enabled = false;
            status.Text = "지우는 중…";
            SetShown(status, true);
            Relayout();
            remove(ids, CloseQuietly);
        }

        /// <summary>트레이가 닫는다(지우기가 끝남).</summary>
        public void CloseQuietly()
        {
            if (quiet || IsDisposed) return;
            quiet = true;
            Close();
        }

        protected override void BeforeSnapshot() { if (AcceptButton is Button b) b.NotifyDefault(true); }
    }

    /// <summary>체크 줄 묶음: 줄이 MaxRows 를 넘으면 세로로 넘긴다(어두운 테마면 스크롤 막대도 어둡게).</summary>
    sealed class CheckList : Panel
    {
        readonly HubDialog owner;
        public readonly List<CheckRow> Rows = new List<CheckRow>();
        const int MaxRows = 5;

        public CheckList(HubDialog owner)
        {
            this.owner = owner;
            AutoScroll = true;
            BackColor = owner.Pal.Body;
            TabStop = false;
            if (owner.Pal.Dark) HandleCreated += (s, e) => Dwm.DarkScrollbars(Handle, true);
        }

        public void Add(CheckRow r)
        {
            Rows.Add(r);
            Controls.Add(r);
            r.TabIndex = Rows.Count - 1;
        }

        /// <summary>줄을 너비 w 에 맞춰 놓고, 보일 높이(최대 MaxRows 줄)를 돌려준다(HubDialog 의 본문 줄 재기).</summary>
        public int Arrange(int w)
        {
            int gap = owner.Px(2), total = 0, max = 0;
            for (int i = 0; i < Rows.Count; i++)
            {
                int h = Rows[i].Measure() + (i > 0 ? gap : 0);
                total += h;
                if (i < MaxRows) max += h;
            }
            int rw = total > max ? w - SystemInformation.VerticalScrollBarWidth - owner.Px(4) : w, y = AutoScrollPosition.Y;
            foreach (var r in Rows)
            {
                int h = r.Measure();
                r.SetBounds(0, y, rw, h);
                y += h + gap;
            }
            return max;
        }
    }

    /// <summary>
    /// 체크 줄: 둥근 체크 상자(체크 = 글자색 바탕 + 바탕색 체크 — 진행 단계의 끝난 원과 같은 말) · 굵지 않은 첫 줄 · 작은 회색 둘째 줄.
    /// 줄 어디를 눌러도, Space 로도 바뀐다. 마우스를 올리면 둥근 사각형 바탕.
    /// </summary>
    sealed class CheckRow : Control
    {
        readonly HubDialog owner;
        readonly Func<Font> main, small;
        public readonly string Id;
        readonly string sub;
        bool on = true, hover;
        public event EventHandler CheckedChanged;

        public CheckRow(HubDialog owner, string id, string line, string sub, Func<Font> main, Func<Font> small)
        {
            this.owner = owner;
            this.main = main;
            this.small = small;
            this.sub = sub;
            Id = id;
            Text = line;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            BackColor = owner.Pal.Body;
            AccessibleName = line + ", " + sub;
        }

        [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Checked
        {
            get => on;
            set
            {
                if (on == value) return;
                on = value;
                Invalidate();
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public int Measure() => owner.Px(7) + Wrap.LineHeight(main()) + owner.Px(1) + Wrap.LineHeight(small()) + owner.Px(7);

        protected override AccessibleObject CreateAccessibilityInstance() => new Acc(this);

        sealed class Acc : ControlAccessibleObject
        {
            readonly CheckRow row;
            public Acc(CheckRow row) : base(row) { this.row = row; }
            public override AccessibleRole Role => AccessibleRole.CheckButton;
            public override AccessibleStates State => base.State | (row.Checked ? AccessibleStates.Checked : AccessibleStates.None);
            public override string DefaultAction => row.Checked ? "선택 해제" : "선택";
            public override void DoDefaultAction() { if (row.Enabled) row.Checked = !row.Checked; }
        }

        void Toggle() { if (Enabled) Checked = !on; }

        protected override void OnClick(EventArgs e) { base.OnClick(e); Focus(); Toggle(); }
        protected override bool IsInputKey(Keys k) => k == Keys.Space || base.IsInputKey(k);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space) { e.Handled = true; Toggle(); }
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var p = owner.Pal;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var whole = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            if (hover && Enabled)
                using (var path = Theme.Round(whole, owner.Px(6)))
                using (var b = new SolidBrush(p.Hover))
                    g.FillPath(b, path);
            if (Focused && ShowFocusCues)
                using (var path = Theme.Round(whole, owner.Px(6)))
                using (var pen = new Pen(p.Muted, 1f))
                    g.DrawPath(pen, path);
            float s = owner.Px(16), bx = owner.Px(10), by = (Height - s) / 2f;
            var box = new RectangleF(bx, by, s, s);
            Color ink = Enabled ? p.Text : p.Muted;
            if (on)
            {
                using (var path = Theme.Round(box, owner.Px(4)))
                using (var b = new SolidBrush(ink))
                    g.FillPath(b, path);
                using var w = new Pen(p.Body, Math.Max(1.5f, owner.Px(1.8f))) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                float cx = bx + s / 2f, cy = by + s / 2f;
                g.DrawLines(w, new[] { new PointF(cx - s * 0.24f, cy + s * 0.01f), new PointF(cx - s * 0.06f, cy + s * 0.19f), new PointF(cx + s * 0.25f, cy - s * 0.17f) });
            }
            else
                using (var path = Theme.Round(RectangleF.Inflate(box, -0.5f, -0.5f), owner.Px(4)))
                using (var pen = new Pen(p.Muted, Math.Max(1f, owner.Px(1.2f))))
                    g.DrawPath(pen, path);
            g.SmoothingMode = SmoothingMode.None;
            int tx = (int)(bx + s) + owner.Px(12), top = owner.Px(7), lh = Wrap.LineHeight(main());
            var back = hover && Enabled ? p.Hover : BackColor;
            const TextFormatFlags f = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.Left | TextFormatFlags.Top;
            TextRenderer.DrawText(g, Text, main(), new Rectangle(tx, top, Width - tx - owner.Px(8), lh), Enabled ? p.Text : p.Muted, back, f);
            TextRenderer.DrawText(g, sub, small(), new Rectangle(tx, top + lh + owner.Px(1), Width - tx - owner.Px(8), Wrap.LineHeight(small())), p.Muted, back, f);
        }
    }
}
