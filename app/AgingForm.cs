// 에이징 시작 창(트레이 COM 포트 ▸ COMx ▸ 에이징 시작…, UI_COPY.md "에이징 시작", 부록 C.12): 세션 이름을 받는다.
//   기본 이름 = <보드>_<yyyyMMdd-HHmm>(보드를 모르면 COM). 이름은 폴더 이름이 되므로 아래 회색 줄에 실제 폴더(AgingNames 로 다듬은 이름)를 바로 보여 준다.
//   [시작](주황 — 안전한 진행, Enter) · [취소](Esc). 쓸 수 있는 글자가 없으면 [시작]이 꺼진다. 시작은 트레이가 백그라운드에서 코어 session start 로 하고,
//   결과 풍선("에이징을 시작했습니다. …")도 트레이가 띄운다. 창은 COM 포트를 열거나 닫지 않는다.
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace UartHub.App
{
    sealed class AgingForm : HubDialog
    {
        public static string Title => L.T("에이징 시작", "Start aging");
        readonly Action<string> start;
        readonly InputBox input;
        readonly TextBlock folder;
        readonly Button go, cancel;
        readonly string agingRoot;
        readonly DateTime at;

        /// <param name="who">"EVB-A1 (7001)" 처럼 보드와 포트(IconName).</param>
        /// <param name="agingRoot">세션 폴더의 뿌리(&lt;기준&gt;\에이징).</param>
        /// <param name="start">[시작]: 다듬기 전 이름(코어가 다시 다듬는다).</param>
        public AgingForm(string who, string defaultName, string agingRoot, DateTime at, Action<string> start, float zoom = 1f) : base(Title, zoom)
        {
            this.start = start; this.agingRoot = agingRoot; this.at = at;
            ContentW = 420;
            SuspendLayout();
            AddHead(L.T(Josa.EulReul(who) + " 에이징할까요?", "Start aging on " + who + "?"));
            AddBody(L.T("끝낼 때까지 이 포트의 콘솔 로그와 커널 패닉, Oops, 앱 오류 이벤트를 아래 폴더에 남깁니다. 끝내면 요약도 같이 저장합니다.",
                        "Until you end it, this port's console log and kernel panic, Oops and app error events are saved in the folder below. Ending it also saves a summary."));
            AddText(L.T("이름", "Name"), () => FBold, Pal.Text, 14);
            input = Bind(new InputBox(this, defaultName), () => FBody);
            AddRow(input, 6, input.Measure);
            folder = AddNote("", 8);
            go = AddButton(L.T("시작", "Start"), Go, primary: true);
            cancel = AddButton(L.T("취소", "Cancel"), Close);
            AcceptButton = go;                                               // Enter = 시작, Esc = 취소
            CancelButton = cancel;
            input.Box.TextChanged += (s, e) => Changed();
            Changed();
            ActiveControl = input.Box;
            ResumeLayout(false);
            Relayout();
            Shown += (s, e) => { input.Box.Focus(); input.Box.SelectAll(); };
        }

        void Changed()
        {
            string n = AgingNames.Sanitize(input.Box.Text);
            go.Enabled = n != null;
            folder.Text = n == null ? L.T("폴더 이름으로 쓸 수 있는 글자를 넣어 주세요.", "Enter characters that can be used in a folder name.") : L.T("폴더: ", "Folder: ") + Path.Combine(agingRoot ?? "", AgingNames.FolderName(n, at));
            SetShown(folder, true);                                          // 빈 글로 만든 줄은 숨겨져 있다(AddText)
            Relayout();
        }

        void Go()
        {
            if (AgingNames.Sanitize(input.Box.Text) == null) return;
            string n = input.Box.Text;
            Close();
            start(n);
        }

        protected override void BeforeSnapshot() { if (AcceptButton is Button b) b.NotifyDefault(true); }
    }

    /// <summary>
    /// 한 줄 입력 칸: 1px 테두리(Pal.Border) 안에 테두리 없는 TextBox(본문 바탕·글 색 — 두 테마 모두). 높이는 고정 px 가 아니라 글꼴 높이 기준
    /// (WellText 처럼 TextRenderer 로 잰 "Ag가" 한 줄 + 여백) — render-ui 의 150% 흉내·실제 150% DPI·다른 배율 모니터로 옮김(글꼴 다시 입힘)에서 글자가 잘리지 않게.
    /// </summary>
    sealed class InputBox : Panel
    {
        readonly HubDialog owner;
        readonly Panel inner;
        public readonly TextBox Box;

        public InputBox(HubDialog owner, string text)
        {
            this.owner = owner;
            BackColor = owner.Pal.Border;
            TabStop = false;
            Box = new TextBox { BorderStyle = BorderStyle.None, AutoSize = false, BackColor = owner.Pal.Body, ForeColor = owner.Pal.Text, Text = text ?? "", AccessibleName = L.T("이름", "Name"), MaxLength = 200 };
            inner = new Panel { BackColor = owner.Pal.Body, TabStop = false };
            inner.Controls.Add(Box);
            Controls.Add(inner);
            Resize += (s, e) => Arrange();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            Box.Font = Font;                                                 // 글꼴이 바뀌면(DPI 바뀜 포함) 칸 안 배치도 다시
            Arrange();
        }

        /// <summary>글 한 줄 높이(글꼴 기준): "Ag가" 를 잰 높이 + 위아래 2px(배율 반영).</summary>
        int LineH() => TextRenderer.MeasureText("Ag가", Box.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Height + owner.Px(4);

        void Arrange()
        {
            inner.SetBounds(1, 1, Math.Max(0, Width - 2), Math.Max(0, Height - 2));
            int bh = Math.Min(LineH(), Math.Max(0, inner.Height));
            Box.SetBounds(owner.Px(8), Math.Max(0, (inner.Height - bh) / 2), Math.Max(0, inner.Width - owner.Px(16)), bh);
        }

        /// <summary>HubDialog 본문 줄 재기: 글 한 줄 + 위아래 여백(배율 반영).</summary>
        public int Measure(int w) => LineH() + owner.Px(12);
    }
}
