// /connect-uart 의 확인 창·안내 창(계획 X5 부록 B.3-4, US-112, UI_COPY.md "COM 연결 확인·안내"). 감독의 connect 흐름(src/host/ConnectFlow.cs, 백그라운드 스레드)이
// IConnectUi 로 부르면 트레이의 UI 스레드에 창을 띄우고 답을 돌려준다. 시험 모드(C-13)는 트레이가 없어 이 파일을 쓰지 않는다(TestConnectUi).
//   확인 창: 맨 앞(TopMost) · "COM8 사용 중: MobaXterm 세션" · 작은 회색 "motty.exe · pid 88688" · [끊고 연결] [취소]. 세션을 끝내는 버튼이라 실수로 누르지 않게:
//            처음 포커스·Enter(AcceptButton)·Esc 는 [취소], [끊고 연결]은 창이 뜨고 0.5초 뒤에야 켜진다. 닫기 = 취소 · 제한 시간이 지나거나 요청자가 떠나면(abort) 창을 닫고 취소.
//   안내 창: 맨 앞 · [취소] 하나 · 바닥 왼쪽 "남은 시간 1:58"(1초마다) · 흐름이 끝나면(그 프로그램이 COM 을 놓음·시간 초과·요청자가 떠남) 감독이 닫는다.
using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UartHub.App
{
    sealed class WinConnectUi : IConnectUi
    {
        readonly SynchronizationContext ui;
        public WinConnectUi(SynchronizationContext ui) { this.ui = ui; }

        public bool Confirm(UiText t, int timeoutMs, Func<bool> abort)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ConnectForm f = null;
            ui.Post(_ =>
            {
                try
                {
                    f = new ConnectForm(t, 0, r => tcs.TrySetResult(r));
                    f.Show();
                    f.Activate();
                }
                catch (Exception) { tcs.TrySetResult(false); }
            }, null);
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!tcs.Task.Wait(200))
            {
                if (DateTime.UtcNow < deadline && !(abort?.Invoke() ?? false)) continue;
                ui.Post(_ => { try { f?.CloseQuietly(); } catch (Exception) { } }, null);
                return false;
            }
            return tcs.Task.Result;
        }

        public IConnectGuide Guide(UiText t, int maxMs)
        {
            var g = new GuideHandle(ui);
            ui.Post(_ =>
            {
                try
                {
                    if (g.Closed) return;
                    g.Form = new ConnectForm(t, maxMs, r => g.Cancel());
                    g.Form.Show();
                    g.Form.Activate();
                }
                catch (Exception) { g.Cancel(); }
            }, null);
            return g;
        }

        sealed class GuideHandle : IConnectGuide
        {
            readonly SynchronizationContext ui;
            volatile bool cancelled;
            public volatile bool Closed;
            public ConnectForm Form;
            public GuideHandle(SynchronizationContext ui) { this.ui = ui; }
            public bool Cancelled => cancelled;
            public void Cancel() => cancelled = true;
            public void Close()
            {
                Closed = true;
                ui.Post(_ => { try { Form?.CloseQuietly(); } catch (Exception) { } }, null);
            }
        }
    }

    /// <summary>
    /// 맨 앞 작은 창: 머리 · 본문 · 작은 회색 줄 + [t.Ok](없으면 안내 창) [취소]. 답은 한 번만(Ok 면 true, 취소·닫기면 false). CloseQuietly 는 답 없이 닫는다.
    /// 기본 버튼(처음 포커스·Enter·Esc)은 [취소] — Ok 는 되돌릴 수 없는 동작이라 창이 뜨고 ArmMs 뒤에야 켜진다. guideMs &gt; 0 이면 바닥 왼쪽에 남은 시간.
    /// </summary>
    sealed class ConnectForm : HubDialog
    {
        const int ArmMs = 500;
        readonly Action<bool> answer;
        readonly Button yesButton, noButton;
        readonly TextBlock clock;
        readonly int guideMs;
        DateTime guideEnd;
        System.Windows.Forms.Timer tick;
        bool answered;

        public ConnectForm(UiText t, int guideMs, Action<bool> answer, float zoom = 1f) : base(t.Title ?? "UART 허브", zoom)
        {
            this.answer = answer;
            this.guideMs = guideMs;
            ContentW = 400;
            TopMost = true;
            SuspendLayout();
            AddText(t);
            if (guideMs > 0 && t.Ok == null)
            {
                clock = Bind(new TextBlock { BackColor = Pal.Footer, ForeColor = Pal.Muted, Text = "남은 시간 " + ConnectFlow.Clock(guideMs) }, () => FBody);
                SetFooterLeft(clock);
            }
            if (t.Ok != null)
            {
                yesButton = AddButton(t.Ok, () => Answer(true));
                yesButton.Enabled = false;
            }
            noButton = AddButton(t.Cancel ?? "취소", () => Answer(false));
            AcceptButton = noButton;                                         // Enter·Space(포커스) = 취소 — 세션을 끝내는 쪽은 기본이 아니다
            CancelButton = noButton;
            ActiveControl = noButton;
            ResumeLayout(false);
            Relayout();
            Shown += (s, e) =>
            {
                noButton.Focus();
                if (clock != null)
                {
                    guideEnd = DateTime.UtcNow.AddMilliseconds(this.guideMs);
                    tick = new System.Windows.Forms.Timer { Interval = 250 };
                    tick.Tick += (s2, e2) =>
                    {
                        string c = "남은 시간 " + ConnectFlow.Clock((long)Math.Max(0, (guideEnd - DateTime.UtcNow).TotalMilliseconds));
                        if (c != clock.Text) clock.Text = c;
                    };
                    tick.Start();
                }
                if (yesButton == null) return;
                var arm = new System.Windows.Forms.Timer { Interval = ArmMs };
                arm.Tick += (s2, e2) => { arm.Stop(); arm.Dispose(); if (!IsDisposed) yesButton.Enabled = true; };
                arm.Start();
            };
            FormClosed += (s, e) =>
            {
                tick?.Stop(); tick?.Dispose();
                if (!answered) { answered = true; answer(false); }
            };
        }

        void Answer(bool v)
        {
            if (answered) return;
            answered = true;
            answer(v);
            Close();
        }

        /// <summary>감독이 닫는다(답을 보내지 않음 — 흐름이 이미 끝남).</summary>
        public void CloseQuietly()
        {
            if (IsDisposed) return;
            answered = true;
            Close();
        }

        /// <summary>그림용: 남은 시간을 고정하고(타이머 없이) [끊고 연결]을 켠 모습.</summary>
        public void Pose(long leftMs, bool armed)
        {
            if (clock != null) clock.Text = "남은 시간 " + ConnectFlow.Clock(leftMs);
            if (yesButton != null) yesButton.Enabled = armed;
            Relayout();
        }

        protected override void BeforeSnapshot() => noButton.NotifyDefault(true);
    }
}
