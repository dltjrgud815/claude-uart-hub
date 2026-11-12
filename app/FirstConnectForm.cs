// 첫 연결 질문 창(계획 T3 UI 쪽, A2-1 — on_demand 가 꺼져 있을 때만): pending 케이블 하나에 창 하나. 비모달 · 맨 위(WS_EX_TOPMOST) · 포커스를 뺏지 않음(ShowWithoutActivation).
//   [예]    → 코어에 `cable decide <id> board`(포트 배정·COM 열기 → 아이콘)
//   [아니오] → `cable decide <id> ignore`(포트는 배정되지만 COM 을 열지 않음 = released — 다른 프로그램이 이 COM 을 씀)
//   닫기   → 무응답(pending 유지 — 트레이 제목 "확인 필요 n", COM 포트 ▸ [답하기…])
//   응답: exit 0 → 닫음 · 106(이미 정해짐·config.json 이 제외) → 안내 후 닫음 · 102(케이블이 보이지 않음 — 뽑힘·허브 재시작) → 안내 후 닫음
//         · 응답 없음(허브가 꺼짐·재시작 중) → 창을 그대로 두고 "잠시 뒤 다시" 안내(버튼 다시 켬).
// 모양은 다른 창과 같은 제목 줄·본문(UiKit). 코어 호출은 트레이가 백그라운드에서 하고(Decide 콜백), 결과는 트레이 Timer(UI 스레드)가 넘겨준다.
using System;
using System.Drawing;
using System.Windows.Forms;

namespace UartHub.App
{
    sealed class FirstConnectForm : HubDialog
    {
        readonly string id;
        readonly Action<string, bool, Action<string>> decide;
        readonly Button yes, no;
        readonly TextBlock status;
        readonly int slot;
        bool quiet;

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x8;                                      // WS_EX_TOPMOST(TopMost 속성은 SetWindowPos 로 활성화될 수 있어 쓰지 않음)
                return cp;
            }
        }

        /// <param name="slot">이미 떠 있는 질문 창 수(겹치지 않게 위로 쌓는다).</param>
        public FirstConnectForm(CableRow row, int slot, Action<string, bool, Action<string>> decide, float zoom = 1f) : base("UART 허브 · 새 케이블", zoom)
        {
            id = row.Cable;
            this.decide = decide;
            this.slot = slot;
            ContentW = 400;
            StartPosition = FormStartPosition.Manual;
            SuspendLayout();
            string com = row.Com ?? "COM?";
            AddText(new UiText
            {
                Head = com + ": 보드 케이블인가요?",
                Body = "예: 허브가 이 COM 포트를 열어 기록하고 바탕화면에 아이콘을 만듭니다.\n"
                       + "아니오: 허브가 열지 않습니다. 다른 프로그램이 이 COM 포트를 쓸 수 있습니다.\n"
                       + "답은 기억합니다. 창을 닫으면 나중에 트레이 메뉴의 COM 포트에서 답할 수 있습니다.",
                Note = (string.IsNullOrEmpty(row.Description) ? "" : row.Description + " · ") + "일련번호 " + id,
            });
            status = AddText("", () => FBody, Pal.Bad, 10);
            yes = AddButton("예", () => Answer(true));
            no = AddButton("아니오", () => Answer(false));
            ResumeLayout(false);
            Relayout();
            Load += (s, e) => Place();
        }

        void Place()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - Px(16), Math.Max(wa.Top, wa.Bottom - (Height + Px(12)) * (slot + 1) - Px(4)));
        }

        void Say(string text, Color color)
        {
            status.ForeColor = color;
            status.Text = text;
            SetShown(status, !string.IsNullOrEmpty(text));
            Relayout();
            if (Visible) Place();
        }

        void Answer(bool board)
        {
            yes.Enabled = no.Enabled = false;
            Say("보내는 중…", Pal.Muted);
            decide(id, board, OnResult);
        }

        /// <summary>코어 응답(트레이 UI 스레드에서 불림).</summary>
        void OnResult(string line)
        {
            if (IsDisposed) return;
            var (code, err) = Tray.Parse(line);
            if (code == 0) { CloseQuietly(); return; }
            if (line == null || code < 0)
            {
                Say("허브가 응답하지 않습니다. 다시 켜는 중일 수 있으니 잠시 뒤 다시 눌러 주세요.", Pal.Bad);
                yes.Enabled = no.Enabled = true;
                return;
            }
            if (code == 106) Say("이미 정해진 케이블입니다(" + (err ?? "다른 곳에서 답함") + "). 트레이 메뉴의 COM 포트에서 바꿀 수 있습니다.", Pal.Bad);
            else if (code == 102) Say("케이블이 보이지 않습니다. 뽑혔거나 허브가 다시 켜졌습니다. 다시 꽂으면 다시 묻습니다.", Pal.Bad);
            else Say("처리하지 못했습니다(exit " + code + "): " + err, Pal.Bad);
            var t = new Timer { Interval = 4000 };
            t.Tick += (s, e) => { t.Dispose(); CloseQuietly(); };
            t.Start();
        }

        /// <summary>판정이 끝나 닫는다(트레이가 다른 곳에서 정해진 것을 봤을 때도).</summary>
        public void CloseQuietly()
        {
            if (quiet || IsDisposed) return;
            quiet = true;
            Close();
        }
    }
}
