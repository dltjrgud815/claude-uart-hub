// 설치기 화면(계획 T2, UI_COPY.md "처음 시작"·"켜는 중 → 끝"·"새 버전으로 바꾸기"): 확인 창 1개 + 진행 창. Installer(Core, WinForms 없음)가 IInstallUi 로 부른다.
// 설치 작업은 백그라운드 스레드에서 돌고, 이 클래스가 UI 스레드(STA, Application.Run)에서 창을 다룬다.
//   Confirm  → 모달 확인 창(머리 · 본문 · 요약 줄 · 자세히 ▸ · [진행][취소]). 이 창은 한 번뿐이다. Enter 는 UiText.OkIsDefault 일 때만 진행, 아니면 Enter·Esc = 취소.
//   Steps·Stage → 진행 창의 단계 3개(허브 켜기 → Claude 연결 정보 넣기 → 확인). install.log 줄(Detail)은 "자세히" 안에만 쌓는다.
//   Progress → 단계가 없는 예전 흐름(install·update)만 한 줄 상태로. 확인 전에는 쌓기만 하고, 확인 뒤 진행 창을 보인다.
//   Finish   → 완료면 머리·본문 + "5초 뒤 저절로 닫힙니다" 뒤 닫힘, 실패면 붉은 머리 + 로그 파일 + [로그 폴더 열기][닫기].
// 시험 모드(C-13)·dry-run·--repair 는 이 화면을 쓰지 않는다(Installer 가 AutoInstallUi 로 대신 기록) — 시험이 사용자 화면에 창을 띄우지 않게.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace UartHub.App
{
    static class InstallForm
    {
        public static int Run(string title, Func<IInstallUi, int> work)
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => { };     // UI 스레드 예외는 대화상자 없이 버린다(설치 결과는 install.log·종료코드)
            var pump = new Control();
            _ = pump.Handle;                                  // 보이지 않는 마샬링용 컨트롤 — 진행 창을 닫아도 남는다
            var prog = new ProgressWindow(title);
            var ui = new FormUi(pump, prog);
            int rc = Installer.RcFailed;
            var th = new Thread(() =>
            {
                try { rc = work(ui); }
                catch (Exception e) { rc = Installer.RcFailed; ui.Finish(false, UiText.FromMessage(L.T("설치기 내부 오류: " + e.Message, "Installer internal error: " + e.Message), true), 0); }
                finally
                {
                    try { pump.BeginInvoke(new Action(() => { try { prog.AllowClose(); prog.Close(); } catch (Exception) { } Application.ExitThread(); })); }
                    catch (Exception) { }
                }
            }) { IsBackground = true, Name = "Installer" };
            th.Start();
            Application.Run();
            return rc;
        }
    }

    sealed class FormUi : IInstallUi
    {
        readonly Control pump;
        readonly ProgressWindow prog;
        volatile bool confirmed;

        public FormUi(Control pump, ProgressWindow p) { this.pump = pump; prog = p; }

        void Post(Action a)
        {
            try { pump.BeginInvoke(new Action(() => { if (!prog.IsDisposed) a(); })); }
            catch (Exception) { }
        }

        public bool Confirm(UiText t)
        {
            bool ok = false;
            pump.Invoke(new Action(() =>
            {
                using var f = new ConfirmWindow(t);
                ok = f.ShowDialog() == DialogResult.OK;
                if (ok) { prog.Show(); prog.Activate(); }
            }));
            confirmed = ok;
            return ok;
        }

        public void Steps(string head, params string[] names) => Post(() => prog.SetSteps(head, names));
        public void Stage(int index) => Post(() => prog.SetStage(index));
        public void Detail(string line) => Post(() => prog.AddDetail(line));

        public void Progress(string line) => Post(() =>
        {
            prog.SetStatus(line);
            if (confirmed && !prog.Visible && !prog.Finished) prog.Show();
        });

        public void Finish(bool ok, UiText t, int autoCloseMs)
        {
            var done = new ManualResetEventSlim(false);
            try
            {
                pump.BeginInvoke(new Action(() =>
                {
                    if (prog.IsDisposed) { done.Set(); return; }
                    prog.Finish(ok, t, autoCloseMs, () => done.Set());
                }));
                done.Wait();
            }
            catch (Exception) { }
        }
    }

    /// <summary>확인 창: 머리 · 본문 · 요약 줄 · 작은 줄 · 자세히 ▸ + [진행] [취소]. 진행이 안전한 동작이 아니면(OkIsDefault false) Enter·Esc·처음 포커스 = [취소].</summary>
    sealed class ConfirmWindow : HubDialog
    {
        readonly Button ok, cancel;

        public ConfirmWindow(UiText t, float zoom = 1f) : base(t.Title ?? L.T("UART 허브", "UART Hub"), zoom)
        {
            ContentW = 440;
            SuspendLayout();
            AddText(t);
            ok = AddButton(t.Ok ?? L.T("확인", "OK"), null, primary: t.OkIsDefault);  // 주황은 안전한 진행(OkIsDefault)에만 — "제거"·"바꾸기"는 테마 버튼
            ok.DialogResult = DialogResult.OK;
            cancel = AddButton(t.Cancel ?? L.T("취소", "Cancel"), null);
            cancel.DialogResult = DialogResult.Cancel;
            var safe = t.OkIsDefault ? ok : cancel;
            AcceptButton = safe;
            CancelButton = cancel;
            ActiveControl = safe;
            ResumeLayout(false);
            Relayout();
            Shown += (s, e) => { safe.Focus(); Activate(); };
        }

        protected override void BeforeSnapshot() => ((Button)AcceptButton).NotifyDefault(true);
    }

    /// <summary>
    /// 진행 창: 머리("UART 허브를 켜는 중입니다.") + 단계 목록 + 자세히(install.log 줄) + [닫기](끝난 뒤에만). 끝나면 머리·본문을 결과로 바꾸고 단계는 숨긴다.
    /// 끝난 뒤 닫기·자동 닫힘은 숨기기만 한다(마지막 정리는 InstallForm.Run).
    /// </summary>
    sealed class ProgressWindow : HubDialog
    {
        readonly TextBlock head, body, status, note;
        readonly StepList steps;
        readonly Button openLogs, close;
        readonly List<string> log = new List<string>();
        bool canClose;
        Action onClosed;
        string logPath;
        System.Windows.Forms.Timer countdown;
        [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Finished { get; private set; }

        public ProgressWindow(string title, float zoom = 1f) : base(title, zoom)
        {
            ContentW = 420;
            WellLines = 9;
            MinimizeBox = true;
            SuspendLayout();
            head = AddHead(L.T("진행 중입니다.", "Working…"));
            steps = Bind(new StepList(this), () => FBody);
            steps.Bold = () => FBold;
            AddRow(steps, 14, w => steps.Measure());
            SetShown(steps, false);
            status = AddNote("", 8);
            body = AddBody("", 8);
            note = AddNote("", 12);
            SetDetails(new string[0]);
            openLogs = AddButton(L.T("로그 폴더 열기", "Open log folder"), OpenLogs);
            SetShown(openLogs, false);
            close = AddButton(L.T("닫기", "Close"), Dismiss);
            close.Enabled = false;
            ResumeLayout(false);
            Relayout();
            FormClosing += (s, e) =>
            {
                if (canClose) return;                       // 마지막 정리(InstallForm.Run)
                e.Cancel = true;                            // 진행 중에는 닫지 않는다. 끝난 뒤의 X 는 숨기기
                if (Finished) Dismiss();
            };
        }

        public void AllowClose() => canClose = true;

        void Dismiss()
        {
            countdown?.Stop();
            Hide();
            var a = onClosed; onClosed = null;
            a?.Invoke();
        }

        void OpenLogs()
        {
            try
            {
                if (!string.IsNullOrEmpty(logPath) && File.Exists(logPath))
                    Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { "/select," + logPath } })?.Dispose();
                else if (!string.IsNullOrEmpty(logPath) && Directory.Exists(Path.GetDirectoryName(logPath)))
                    Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { Path.GetDirectoryName(logPath) } })?.Dispose();
            }
            catch (Exception) { }
        }

        public void SetSteps(string h, string[] names)
        {
            head.Text = h;
            steps.Set(names);
            SetShown(steps, names.Length > 0);
            SetShown(status, false);
            Relayout();
        }

        public void SetStage(int i) => steps.At(i);

        /// <summary>단계가 없는 흐름(예전 install·update)의 한 줄 상태.</summary>
        public void SetStatus(string line)
        {
            if (steps.Count > 0 || Finished) return;
            status.Text = line;
            SetShown(status, !string.IsNullOrEmpty(line));
            Relayout();
        }

        public void AddDetail(string line)
        {
            if (log.Count < 5000) log.Add(line);
            if (!Finished) AppendDetail(line);
        }

        public void Finish(bool ok, UiText t, int autoCloseMs, Action closed)
        {
            onClosed = closed;
            Finished = true;
            logPath = t.LogPath;
            head.Text = t.Head ?? (ok ? L.T("끝났습니다.", "Done.") : L.T("끝내지 못했습니다.", "Could not finish."));
            head.ForeColor = ok && !t.Error ? Pal.Text : Pal.Bad;
            body.Text = t.Body ?? "";
            SetShown(body, !string.IsNullOrEmpty(body.Text));
            if (ok) steps.Done(); else steps.Fail();
            SetShown(steps, false);
            SetShown(status, false);
            var d = new List<string>(t.Details);
            d.AddRange(t.Bullets);
            if (log.Count > 0) { if (d.Count > 0) d.Add(""); d.Add("── install.log ──"); d.AddRange(log); }
            SetDetails(d);
            SetShown(openLogs, !ok && !string.IsNullOrEmpty(logPath));
            close.Enabled = true;
            AcceptButton = close;
            CancelButton = close;
            note.Text = ok && autoCloseMs > 0 ? CloseIn(autoCloseMs) : "";
            SetShown(note, !string.IsNullOrEmpty(note.Text));
            Relayout();
            if (!Visible) Show();
            Activate();
            close.Focus();
            if (ok && autoCloseMs > 0)
            {
                var end = DateTime.UtcNow.AddMilliseconds(autoCloseMs);
                countdown = new System.Windows.Forms.Timer { Interval = 250 };
                countdown.Tick += (s, e) =>
                {
                    int left = (int)(end - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0) { countdown.Stop(); countdown.Dispose(); countdown = null; Dismiss(); return; }
                    string n = CloseIn(left);
                    if (n != note.Text) note.Text = n;
                };
                countdown.Start();
            }
        }

        static string CloseIn(int ms) => L.T(((ms + 999) / 1000) + "초 뒤 저절로 닫힙니다.", "Closes by itself in " + ((ms + 999) / 1000) + " s.");

        // ---------------------------------------------------------------- render-ui
        /// <summary>그림용: 끝난 모습(타이머·창 표시 없이).</summary>
        public void ShowResult(bool ok, UiText t, int autoCloseMs)
        {
            Finished = true;
            logPath = t.LogPath;
            head.Text = t.Head;
            head.ForeColor = ok ? Pal.Text : Pal.Bad;
            body.Text = t.Body ?? "";
            SetShown(body, !string.IsNullOrEmpty(body.Text));
            SetShown(steps, false);
            SetShown(status, false);
            var d = new List<string>(t.Details);
            if (log.Count > 0) { d.Add(""); d.Add("── install.log ──"); d.AddRange(log); }
            SetDetails(d);
            SetShown(openLogs, !ok && !string.IsNullOrEmpty(logPath));
            close.Enabled = true;
            AcceptButton = close;
            note.Text = ok && autoCloseMs > 0 ? CloseIn(autoCloseMs) : "";
            SetShown(note, !string.IsNullOrEmpty(note.Text));
            Relayout();
        }

        protected override void BeforeSnapshot() { if (AcceptButton is Button b && b.Enabled) b.NotifyDefault(true); }
    }
}
