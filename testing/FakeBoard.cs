// 가짜 보드(IPort). 계획 §4 S6 + 부록 A 실측을 흉내낸다.
//  - busybox ash 줄 편집(에코·CR·80열 \r\r\n 접힘·^C/^U/^D), 명령 실행 중 tty canonical 에코(type-ahead 재에코)
//  - exit → respawn(새 프롬프트), stdin 읽는 명령은 </dev/null 없으면 대기, sh -c / eval
//  - vi(ESC[?1049h ESC[999;999H ESC[6n, ESC 뒤 짧은 간격 키는 시퀀스로 삼킴), top(q 로 종료)
//  - printk([ 123.456789], ANSI 옵션)·앱 로그 혼입(프롬프트 줄에 붙음), /dev/kmsg(console_loglevel 4)
//  - reboot: 종료 메시지 → reboot: Restarting system → LDR>T###%% → (선택) U-Boot evb: 창 → > → 커널 [    0.06...]
//    → init → 프롬프트 자동 출력 없음(Enter 가 오면 프롬프트, S0-9)
//  - uudecode(begin-base64)·md5sum 있음, base64 없음. 송신 바이트 카운터(무손실 검증용).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace UartHub.Testing
{
    public enum BoardStart
    {
        /// <summary>셸이 줄 편집기에서 대기 중(프롬프트는 연결 전에 찍힘). Enter → 새 프롬프트.</summary>
        Idle,
        /// <summary>지금 콜드 부팅을 시작한다(LDR>T → 커널 → 프롬프트 없음).</summary>
        Boot,
        /// <summary>부팅이 끝났고 프롬프트를 아직 찍지 않은 상태(S0-9).</summary>
        PostBoot,
    }

    public sealed class FakeBoardOptions
    {
        public string Hostname = "SIM";
        public string NextHostname;            // 다음 부팅부터 쓸 호스트명(보드 교체 흉내)
        public int MaxChunk = 64;              // Read 1회 최대 바이트(FTDI 62B 패킷 근사)
        public int ReadTimeoutMs = 50;
        public double PacingBytesPerSec = 0;   // 0=즉시. 11520 ≈ 115200 8N1
        public int ConsoleLogLevel = 4;
        public int PrintkIntervalMs = 0;       // >0 이면 셸 상태에서 주기 printk
        public bool PrintkAnsi = false;
        public string PrintkText = "evb_sim: periodic tick";
        public int RespawnDelayMs = 30;
        public int ShutdownStepMs = 10;
        public int LoaderDelayMs = 60;         // reboot: Restarting system → LDR>T
        public int UbootWindowMs = 0;          // >0 이면 LDR>T 뒤 U-Boot autoboot 창
        public int RtosToKernelMs = 30;
        public int KernelLineIntervalMs = 2;
        public int InitDelayMs = 30;
        public bool PromptAfterBoot = false;   // 실보드: false
        public int ViEscWindowMs = 50;         // ESC 뒤 이 시간 안에 온 바이트는 시퀀스로 처리
        public bool ViCprQuery = true;         // S0-10 양성: vi 가 ESC[6n 을 보낸다
        public int TopIntervalMs = 500;
        public double SleepScale = 1.0;        // sleep N 실제 대기 배율
    }

    public sealed partial class FakeBoard : IPort
    {
        sealed class Seg { public byte[] Data; public int Pos; public double Avail; }
        public struct InByte { public byte B; public double T; }
        enum Tty { Off, LineEdit, Canonical, Raw, UBoot, BootRtos, BootKernel }

        internal sealed class SigIntException : Exception { }
        internal sealed class ExitShellException : Exception { public readonly int Code; public ExitShellException(int c) { Code = c; } }
        internal sealed class RebootException : Exception { }
        internal sealed class ResetException : Exception { }
        internal sealed class PowerOffException : Exception { }

        public FakeBoardOptions Options { get; }
        readonly Stopwatch sw = Stopwatch.StartNew();
        double NowMs => sw.Elapsed.TotalMilliseconds;

        // ---- 출력(보드 → 호스트) ----
        readonly object outLock = new object();
        readonly Queue<Seg> outQ = new Queue<Seg>();
        double lastAvailEnd;
        bool open, unplugged;
        Exception unplugEx;
        string comName = "COMSIM";
        long totalSent, totalEmitted, discarded, pending;

        // ---- 입력(호스트 → 보드) ----
        readonly object inLock = new object();
        readonly LinkedList<InByte> inQ = new LinkedList<InByte>();
        Tty tty = Tty.LineEdit;
        bool sigint, resetPending, powerOff;
        long totalReceived, breakCount;

        // ---- 보드 상태 ----
        Thread main;
        Timer printkTimer;
        long printkSeq;
        string appendAfterPrompt;
        double bootStartMs, kernelOffsetSec;
        volatile bool kernelUp = true;
        volatile string phase = "shell";
        int promptCount, bootCount, viInserted, viSessions;
        readonly List<string> kmsg = new List<string>();
        readonly BoardStart start;

        public FakeBoard(FakeBoardOptions options = null, BoardStart start = BoardStart.Idle)
        {
            Options = options ?? new FakeBoardOptions();
            Hostname = Options.Hostname;
            this.start = start;
            bootStartMs = NowMs;
            kernelOffsetSec = 100.0;              // 이미 100초 돌던 보드
            InitFs();
            main = new Thread(BoardMain) { IsBackground = true, Name = "FakeBoard-" + Hostname };
            main.Start();
            if (Options.PrintkIntervalMs > 0)
                printkTimer = new Timer(_ => PeriodicPrintk(), null, Options.PrintkIntervalMs, Options.PrintkIntervalMs);
        }

        public string Hostname { get; private set; }
        public string Phase => phase;
        public long TotalSent => Interlocked.Read(ref totalSent);
        public long TotalEmitted => Interlocked.Read(ref totalEmitted);
        public long TotalReceived => Interlocked.Read(ref totalReceived);
        public long DiscardedBytes => Interlocked.Read(ref discarded);
        public long PendingBytes { get { lock (outLock) return pending; } }
        public long BreakCount => Interlocked.Read(ref breakCount);
        public int PromptCount => Volatile.Read(ref promptCount);
        public int BootCount => Volatile.Read(ref bootCount);
        /// <summary>vi 버퍼에 삽입된 바이트 누계(“vi 버퍼에 0바이트” 검증용).</summary>
        public int ViInsertedBytes => Volatile.Read(ref viInserted);
        public int ViSessions => Volatile.Read(ref viSessions);
        public double Uptime => (NowMs - bootStartMs) / 1000.0 + kernelOffsetSec;

        public void SetComName(string com) { comName = com; }

        // ================================================================== IPort
        public string Name => comName;
        public bool IsOpen { get { lock (outLock) return open; } }

        public void Open()
        {
            lock (outLock)
            {
                if (unplugged) throw new IOException("The port '" + comName + "' does not exist.");
                if (open) throw new UnauthorizedAccessException("Access to the port '" + comName + "' is denied.");
                outQ.Clear(); pending = 0; lastAvailEnd = 0;
                open = true;
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            lock (outLock)
            {
                double deadline = NowMs + Options.ReadTimeoutMs;
                while (true)
                {
                    if (unplugged) throw unplugEx ?? new OperationCanceledException("The I/O operation has been aborted because of either a thread exit or an application request.");
                    if (!open) throw new InvalidOperationException("The port is closed.");
                    double now = NowMs;
                    double waitMs = deadline - now;
                    int limit = Math.Min(count, Options.MaxChunk);
                    int n = 0;
                    while (n < limit && outQ.Count > 0)
                    {
                        var s = outQ.Peek();
                        int ready = s.Data.Length - s.Pos;
                        if (Options.PacingBytesPerSec > 0)
                        {
                            double per = 1000.0 / Options.PacingBytesPerSec;
                            int byTime = (int)Math.Floor((now - s.Avail) / per) - s.Pos;
                            if (byTime < ready)
                            {
                                ready = Math.Max(0, byTime);
                                waitMs = Math.Min(waitMs, s.Avail + (s.Pos + ready + 1) * per - now);
                            }
                        }
                        int take = Math.Min(ready, limit - n);
                        if (take <= 0) break;
                        Buffer.BlockCopy(s.Data, s.Pos, buffer, offset + n, take);
                        s.Pos += take; n += take;
                        if (s.Pos >= s.Data.Length) outQ.Dequeue(); else break;
                    }
                    if (n > 0)
                    {
                        pending -= n;
                        Interlocked.Add(ref totalSent, n);
                        return n;
                    }
                    if (deadline - now <= 0) return 0;
                    Monitor.Wait(outLock, (int)Math.Max(1, Math.Ceiling(Math.Min(waitMs, deadline - now))));
                }
            }
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            lock (outLock)
            {
                if (unplugged) throw new IOException("The device is not connected.");
                if (!open) throw new InvalidOperationException("The port is closed.");
            }
            lock (inLock)
            {
                double t = NowMs;
                for (int i = 0; i < count; i++)
                {
                    Interlocked.Increment(ref totalReceived);
                    TtyInput(buffer[offset + i], t);
                }
                Monitor.PulseAll(inLock);
            }
        }

        public void SendBreak(int ms) { Interlocked.Increment(ref breakCount); }

        public void Close()
        {
            lock (outLock) { open = false; outQ.Clear(); pending = 0; Monitor.PulseAll(outLock); }
        }

        /// <summary>IPort.Dispose 는 연결만 끊는다(보드는 계속 동작). 보드 정지는 PowerOff.</summary>
        public void Dispose() => Close();

        // ================================================================== 케이블·전원 조작(시험용)
        /// <summary>케이블 탈착: 대기 중인 Read 가 예외(기본 OperationCanceledException, S0-6)를 던지고 미전송 출력은 사라진다.</summary>
        public void Unplug(Exception ex = null)
        {
            lock (outLock)
            {
                unplugged = true; unplugEx = ex; open = false;
                discarded += pending; outQ.Clear(); pending = 0;
                Monitor.PulseAll(outLock);
            }
        }

        public void Replug()
        {
            lock (outLock) { unplugged = false; unplugEx = null; }
        }

        /// <summary>
        /// 케이블 탈착 잡음 흉내(실보드 11-03): 호스트를 거치지 않고 보드 tty 입력에 바이트를 넣는다. 셸 줄 편집기는 보통 입력처럼 처리하고,
        /// 분리 중이면 에코는 버려진다(허브는 모른다). 호스트 송신이 아니므로 TotalReceived 에 넣지 않는다.
        /// </summary>
        public void InjectNoise(byte[] b)
        {
            lock (inLock)
            {
                double t = NowMs;
                foreach (byte x in b) TtyInput(x, t);
                Monitor.PulseAll(inLock);
            }
        }

        /// <summary>와치독식 리셋(종료 메시지 없이 즉시 재부팅).</summary>
        public void Reset()
        {
            lock (inLock) { resetPending = true; Monitor.PulseAll(inLock); }
        }

        public void PowerOff()
        {
            printkTimer?.Dispose();
            lock (inLock) { powerOff = true; Monitor.PulseAll(inLock); }
            main.Join(2000);
        }

        // ================================================================== 출력
        public void EmitBytes(byte[] b)
        {
            if (b == null || b.Length == 0) return;
            lock (outLock)
            {
                Interlocked.Add(ref totalEmitted, b.Length);
                if (!open) { discarded += b.Length; return; }
                double now = NowMs;
                double avail = now;
                if (Options.PacingBytesPerSec > 0)
                {
                    avail = Math.Max(now, lastAvailEnd);
                    lastAvailEnd = avail + b.Length * 1000.0 / Options.PacingBytesPerSec;
                }
                outQ.Enqueue(new Seg { Data = b, Pos = 0, Avail = avail });
                pending += b.Length;
                Monitor.PulseAll(outLock);
            }
        }

        public void Emit(string s) => EmitBytes(Encoding.UTF8.GetBytes(s));

        /// <summary>커널 printk 한 줄([ 123.456789] msg\r\n). ansi=true 면 SGR 로 감싼다(kernel 태그 ANSI 접두 시험용).</summary>
        public void EmitPrintk(string msg, bool ansi = false)
        {
            string line = KTime(Uptime) + " " + msg;
            lock (kmsg) { kmsg.Add(line); if (kmsg.Count > 500) kmsg.RemoveAt(0); }
            Emit(ansi ? "\x1b[1;33m" + line + "\x1b[0m\r\n" : line + "\r\n");
        }

        /// <summary>앱 로그 한 줄(셸 상태와 무관하게 현재 커서 위치에 붙는다).</summary>
        public void EmitAppLog(string text) => Emit(text + "\r\n");

        /// <summary>다음 프롬프트와 같은 덩어리로 text 를 내보낸다(프롬프트+앱 로그 같은 ms, 실보드 로그).</summary>
        public void AppendAfterNextPrompt(string text) { Interlocked.Exchange(ref appendAfterPrompt, text); }

        public static string KTime(double sec)
        {
            long us = (long)Math.Round(sec * 1e6);
            return "[" + (us / 1000000).ToString(CultureInfo.InvariantCulture).PadLeft(5) + "." + (us % 1000000).ToString("D6", CultureInfo.InvariantCulture) + "]";
        }

        void PeriodicPrintk()
        {
            if (!kernelUp || phase == "boot" || phase == "uboot" || phase == "off") return;
            EmitPrintk(Options.PrintkText + " #" + Interlocked.Increment(ref printkSeq), Options.PrintkAnsi);
        }

        /// <summary>
        /// 시험용: 참이면 보드가 입력을 무시한다(에코·실행 없음, TotalReceived 는 센다). 실보드 .raw 발췌를 EmitBytes 로 그대로 흘리면서
        /// 합성 사람 TX 를 허브에 넣는 재생 시나리오용(보드 자신의 에코가 섞이지 않게).
        /// </summary>
        public volatile bool Silent;

        // ================================================================== tty 입력 규칙(Write 스레드, inLock 보유)
        void TtyInput(byte b, double t)
        {
            if (Silent) return;
            switch (tty)
            {
                case Tty.Off:
                    break;
                case Tty.BootRtos:
                    EmitBytes(new[] { b });             // RTOS 셸 원시 에코(부록 A: ESC 가 그대로 찍힘)
                    break;
                case Tty.LineEdit:
                case Tty.Raw:
                case Tty.UBoot:
                    inQ.AddLast(new InByte { B = b, T = t });
                    break;
                case Tty.Canonical:
                case Tty.BootKernel:
                    if (b == 0x03)
                    {
                        Emit("^C");
                        inQ.Clear();
                        sigint = true;
                    }
                    else
                    {
                        EchoCanon(b);
                        inQ.AddLast(new InByte { B = b, T = t });
                    }
                    break;
            }
        }

        void EchoCanon(byte b)
        {
            if (b == 0x0d || b == 0x0a) Emit("\r\n");
            else if (b == 0x7f || b == 0x08) Emit("\b \b");
            else if (b == 0x15 || b == 0x04) { }
            else if (b == 0x09) Emit("\t");
            else if (b < 0x20) Emit("^" + (char)(b + 64));
            else EmitBytes(new[] { b });
        }

        void SetTty(Tty m)
        {
            lock (inLock)
            {
                tty = m;
                if (m == Tty.LineEdit) sigint = false;
            }
        }

        // ================================================================== 셸 스레드용 대기·입력
        void CheckFlagsLocked(bool interruptible)
        {
            if (powerOff) throw new PowerOffException();
            if (resetPending) { resetPending = false; throw new ResetException(); }
            if (interruptible && sigint) { sigint = false; throw new SigIntException(); }
        }

        internal void CheckSigint()
        {
            lock (inLock) CheckFlagsLocked(true);
        }

        /// <summary>ms 동안 대기. interruptible 이면 ^C(SIGINT)로 깨어나 SigIntException.</summary>
        internal void Sleep(double ms, bool interruptible)
        {
            double until = NowMs + ms;
            lock (inLock)
            {
                while (true)
                {
                    CheckFlagsLocked(interruptible);
                    double left = until - NowMs;
                    if (left <= 0) return;
                    Monitor.Wait(inLock, (int)Math.Max(1, Math.Min(left, 50)));
                }
            }
        }

        /// <summary>줄 편집·raw 모드 입력 1바이트(블로킹). timeoutMs 가 있으면 시간 초과 시 null.</summary>
        internal InByte? NextByte(double timeoutMs = -1)
        {
            double until = timeoutMs < 0 ? double.MaxValue : NowMs + timeoutMs;
            lock (inLock)
            {
                while (true)
                {
                    CheckFlagsLocked(false);
                    if (inQ.Count > 0) { var b = inQ.First.Value; inQ.RemoveFirst(); return b; }
                    double left = until - NowMs;
                    if (left <= 0) return null;
                    Monitor.Wait(inLock, (int)Math.Max(1, Math.Min(left, 50)));
                }
            }
        }

        /// <summary>T ≤ deadline 인 다음 바이트를 꺼낸다. deadline 까지 안 오거나 늦게 온 바이트면 null(꺼내지 않음).</summary>
        internal InByte? NextByteArrivedBy(double deadline)
        {
            lock (inLock)
            {
                while (true)
                {
                    CheckFlagsLocked(false);
                    if (inQ.Count > 0)
                    {
                        var b = inQ.First.Value;
                        if (b.T > deadline) return null;
                        inQ.RemoveFirst();
                        return b;
                    }
                    double left = deadline - NowMs;
                    if (left <= 0) return null;
                    Monitor.Wait(inLock, (int)Math.Max(1, Math.Min(left, 20)));
                }
            }
        }

        /// <summary>canonical 모드 한 줄 읽기(cat 등 stdin=tty). ^D(빈 줄) → null, ^U 줄 지움, BS 한 글자 지움.</summary>
        internal string TtyReadLine()
        {
            lock (inLock)
            {
                while (true)
                {
                    CheckFlagsLocked(true);
                    var node = inQ.First;
                    LinkedListNode<InByte> term = null;
                    for (; node != null; node = node.Next)
                    {
                        byte b = node.Value.B;
                        if (b == 0x0d || b == 0x0a || b == 0x04) { term = node; break; }
                    }
                    if (term != null)
                    {
                        var bytes = new List<byte>();
                        byte tb = 0;
                        while (true)
                        {
                            var v = inQ.First.Value; inQ.RemoveFirst();
                            if (v.B == 0x0d || v.B == 0x0a || v.B == 0x04) { tb = v.B; break; }
                            if (v.B == 0x15) bytes.Clear();
                            else if (v.B == 0x7f || v.B == 0x08) { if (bytes.Count > 0) bytes.RemoveAt(bytes.Count - 1); }
                            else bytes.Add(v.B);
                        }
                        string s = Encoding.UTF8.GetString(bytes.ToArray());
                        if (tb == 0x04) return bytes.Count == 0 ? null : s;
                        return s + "\n";
                    }
                    Monitor.Wait(inLock, 50);
                }
            }
        }

        // ================================================================== 보드 메인
        enum ShellEntry { Idle, Prompt, PostBoot }

        void BoardMain()
        {
            bool boot = start == BoardStart.Boot;
            var entry = start == BoardStart.PostBoot ? ShellEntry.PostBoot : ShellEntry.Idle;
            try
            {
                while (true)
                {
                    try
                    {
                        if (boot)
                        {
                            boot = false;
                            DoBoot();
                            entry = Options.PromptAfterBoot ? ShellEntry.Prompt : ShellEntry.PostBoot;
                        }
                        RunLoginShell(entry);
                        // 로그인 셸 종료(exit / ^D) → init 이 respawn → 새 셸이 프롬프트를 찍는다
                        phase = "respawn";
                        SetTty(Tty.Canonical);
                        Sleep(Options.RespawnDelayMs, false);
                        entry = ShellEntry.Prompt;
                    }
                    catch (RebootException)
                    {
                        boot = true;
                        try { DoShutdownMessages(); } catch (ResetException) { }
                    }
                    catch (ResetException)
                    {
                        boot = true;
                    }
                }
            }
            catch (PowerOffException) { phase = "off"; }
            catch (Exception e)
            {
                phase = "crashed";
                try { Emit("\r\nFAKEBOARD INTERNAL ERROR: " + e.GetType().Name + ": " + e.Message + "\r\n"); } catch (Exception) { }
            }
        }

        FakeShell NewLoginShell() => new FakeShell(this, "-sh", true);

        void RunLoginShell(ShellEntry mode)
        {
            var sh = NewLoginShell();
            try
            {
                if (mode == ShellEntry.PostBoot)
                {
                    // 부팅 뒤 셸은 프롬프트를 스스로 찍지 않는다(S0-9). canonical 한 줄을 받으면 실행하고 프롬프트.
                    phase = "postboot";
                    SetTty(Tty.Canonical);
                    string l = TtyReadLine();
                    if (l == null) throw new ExitShellException(0);
                    string cmd = new string(l.Where(ch => ch >= 0x20 || ch == '\t').ToArray()).Trim();
                    if (cmd.Length > 0)
                    {
                        try { sh.RunInteractiveText(cmd); }
                        catch (SigIntException) { Emit("\r\n"); sh.Last = 130; }
                    }
                    InteractiveLoop(sh, true);
                }
                else InteractiveLoop(sh, mode != ShellEntry.Idle);
            }
            catch (ExitShellException) { }
        }

        void InteractiveLoop(FakeShell sh, bool showFirstPrompt)
        {
            bool show = showFirstPrompt;
            while (true)
            {
                phase = "shell";
                string text = ReadCommandLine(sh, show);
                show = true;
                if (text == null) continue;
                phase = "running";
                SetTty(Tty.Canonical);
                try { sh.RunInteractiveText(text); }
                catch (SigIntException) { Emit("\r\n"); sh.Last = 130; }
            }
        }

        string ReadCommandLine(FakeShell sh, bool show)
        {
            string acc = null;
            bool cont = false;
            while (true)
            {
                string prompt = cont ? sh.GetVar("PS2", "> ") : sh.ExpandPs1();
                string l = LineEdit(prompt, show || cont);
                if (l == null) return null;
                acc = acc == null ? l : acc + "\n" + l;
                if (ShParser.IsIncomplete(acc)) { cont = true; continue; }
                return acc;
            }
        }

        readonly List<string> history = new List<string>();     // 줄 편집 히스토리(보드 메인 스레드 전용)
        volatile string lastKilled;
        /// <summary>시험용: 줄 편집기에서 마지막으로 ^U 가 지운 글자(없으면 null). 자동 깨우기가 남은 줄을 지웠는지 본다.</summary>
        public string LastKilled => lastKilled;

        /// <summary>
        /// busybox lineedit 근사. 프롬프트 출력 → 바이트별 에코 → CR 에서 줄 반환(커서 위치와 무관하게 줄 전체). ^C 면 null.
        /// 히스토리(최소): ESC[A 이전·ESC[B 다음 — 줄을 "\r 프롬프트 내용 ESC[J" 로 다시 그린다(실보드 busybox 형태), 끝을 넘으면 BEL.
        /// 커서(최소): ESC[D 왼쪽("\b"), ESC[C 오른쪽(그 글자 다시 찍기), ^E 줄 끝(남은 글자 찍기). 커서가 줄 끝이 아니면 삽입·지우기는
        /// 나머지를 다시 그리고 "\b" 로 돌아온다. ^U 는 busybox 처럼 커서 앞만 지운다(커서가 줄 끝이면 기존 출력과 같음: \r 프롬프트 ESC[K).
        /// </summary>
        string LineEdit(string prompt, bool show)
        {
            SetTty(Tty.LineEdit);
            if (show) EmitPrompt(prompt);
            var line = new List<byte>();
            int promptLen = VisibleLen(prompt);
            int col = promptLen;
            int cur = 0;                                            // 줄 안 커서(바이트 인덱스), 보통 line.Count
            int hist = history.Count;                               // 히스토리 위치(= Count 면 새 줄)
            string Back(int n) => new string('\b', n);
            void Out(string head, byte[] mid, string tail) => EmitBytes(Encoding.UTF8.GetBytes(head).Concat(mid).Concat(Encoding.UTF8.GetBytes(tail)).ToArray());   // 한 덩어리로
            while (true)
            {
                var ib = NextByte().Value;
                byte b = ib.B;
                if (b == 0x0d || b == 0x0a)
                {
                    // lineedit 은 개행 에코 직후 tty 를 canonical 로 되돌린다. 에코와 모드 전환을 원자적으로 해
                    // 그 사이 도착한 ^C 가 데이터로 남아 명령이 영원히 대기하는 경쟁을 막는다.
                    lock (inLock)
                    {
                        Emit("\r\n");
                        tty = Tty.Canonical;
                        if (inQ.Any(x => x.B == 0x03)) { Emit("^C"); inQ.Clear(); sigint = true; }
                    }
                    string text = Encoding.UTF8.GetString(line.ToArray());
                    if (text.Length > 0 && (history.Count == 0 || history[history.Count - 1] != text)) history.Add(text);
                    return text;
                }
                if (b == 0x03) { Emit("^C\r\n"); return null; }
                if (b == 0x04)
                {
                    if (line.Count == 0) { Emit("\r\n"); throw new ExitShellException(0); }
                    continue;
                }
                if (b == 0x15)
                {
                    if (cur > 0)
                    {
                        lastKilled = Encoding.UTF8.GetString(line.Take(cur).ToArray());
                        var rest = line.Skip(cur).ToArray();
                        line.RemoveRange(0, cur);
                        cur = 0;
                        Out("\r" + prompt, rest, "\x1b[K" + Back(rest.Length));
                        col = promptLen;
                    }
                    continue;
                }
                if (b == 0x05) { if (cur < line.Count) { EmitBytes(line.Skip(cur).ToArray()); cur = line.Count; } continue; }
                if (b == 0x7f || b == 0x08)
                {
                    if (cur == line.Count && line.Count > 0)
                    {
                        while (line.Count > 0 && (line[line.Count - 1] & 0xC0) == 0x80) line.RemoveAt(line.Count - 1);
                        if (line.Count > 0) line.RemoveAt(line.Count - 1);
                        cur = line.Count;
                        Emit("\b\x1b[K");
                        col--;
                    }
                    else if (cur > 0)
                    {
                        line.RemoveAt(cur - 1);
                        cur--;
                        var rest = line.Skip(cur).ToArray();
                        Out("\b", rest, "\x1b[K" + Back(rest.Length));
                    }
                    continue;
                }
                if (b == 0x09) { Emit("\a"); continue; }
                if (b == 0x0c) { Emit("\x1b[H\x1b[J" + prompt); EmitBytes(line.ToArray()); cur = line.Count; continue; }
                if (b == 0x1b)
                {
                    string seq = ReadEscape(ib.T);                    // 방향키 등: 좌우만 커서 이동, 나머지는 삼킨다
                    if (seq == "[D" && cur > 0) { cur--; Emit("\b"); }
                    else if (seq == "[C" && cur < line.Count) { EmitBytes(new[] { line[cur] }); cur++; }
                    else if (seq == "[A" || seq == "[B")
                    {
                        int to = seq == "[A" ? hist - 1 : hist + 1;
                        if (to < 0 || to > history.Count) { Emit("\a"); continue; }
                        hist = to;
                        var text = to == history.Count ? new byte[0] : Encoding.UTF8.GetBytes(history[to]);
                        line.Clear(); line.AddRange(text); cur = line.Count;
                        Out("\r" + prompt, text, "\x1b[J");
                        col = promptLen + text.Length;
                    }
                    continue;
                }
                if (b < 0x20) continue;
                if (cur < line.Count)
                {
                    line.Insert(cur, b);
                    cur++;
                    var rest = line.Skip(cur).ToArray();
                    Out("", new[] { b }.Concat(rest).ToArray(), Back(rest.Length));
                    continue;
                }
                line.Add(b);
                cur = line.Count;
                EmitBytes(new[] { b });
                if ((b & 0xC0) != 0x80)
                {
                    col++;
                    if (col % 80 == 0) Emit("\r\r\n");             // 80열 접힘(S0-4 실측)
                }
            }
        }

        void EmitPrompt(string prompt)
        {
            string extra = Interlocked.Exchange(ref appendAfterPrompt, null);
            Interlocked.Increment(ref promptCount);
            Emit(prompt + extra);
        }

        static int VisibleLen(string s)
        {
            int n = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\x1b' && i + 1 < s.Length && s[i + 1] == '[')
                {
                    i += 2;
                    while (i < s.Length && !(s[i] >= 0x40 && s[i] <= 0x7e)) i++;
                    continue;
                }
                n++;
            }
            return n;
        }

        /// <summary>ESC 를 받은 뒤 window 안에 도착한 바이트를 시퀀스로 읽는다. 결과: null=단독 ESC, "[A" 등 CSI/SS3, "?" 접두=알 수 없는 시퀀스.</summary>
        internal string ReadEscape(double escT)
        {
            double deadline = escT + Options.ViEscWindowMs;
            var sb = new StringBuilder();
            while (true)
            {
                var nb = NextByteArrivedBy(deadline);
                if (nb == null) break;
                byte c = nb.Value.B;
                sb.Append((char)c);
                if (sb.Length >= 2 && (sb[0] == '[' || sb[0] == 'O') && (sb[0] == 'O' || (c >= 0x40 && c <= 0x7e)))
                    return sb.ToString();
            }
            if (sb.Length == 0) return null;
            return "?" + sb;
        }

        // ================================================================== 재부팅·부팅
        void DoShutdownMessages()
        {
            phase = "shutdown";
            SetTty(Tty.Canonical);
            foreach (var m in new[] { "The system is going down NOW!", "Sent SIGTERM to all processes", "Sent SIGKILL to all processes", "Requesting system reboot" })
            {
                Emit(m + "\r\n");
                Sleep(Options.ShutdownStepMs, false);
            }
            EmitPrintk("reboot: Restarting system");
        }

        void DoBoot()
        {
            while (true)
            {
                try { DoBootOnce(); return; }
                catch (ResetException) { /* U-Boot reset 등: 처음부터 */ }
            }
        }

        void DoBootOnce()
        {
            phase = "boot";
            kernelUp = false;
            Interlocked.Increment(ref bootCount);
            SetTty(Tty.Off);
            lock (inLock) { inQ.Clear(); sigint = false; }
            Sleep(Options.LoaderDelayMs, false);
            if (Options.NextHostname != null) { Hostname = Options.NextHostname; Options.NextHostname = null; }
            ClearTmp();
            bootStartMs = NowMs;
            kernelOffsetSec = 0.0;
            SetTty(Tty.BootRtos);
            Emit("LDR>T###%%");
            if (Options.UbootWindowMs > 0) UbootWindow();
            phase = "boot";
            SetTty(Tty.BootRtos);
            Emit("> ");
            Sleep(Options.RtosToKernelMs, false);
            SetTty(Tty.BootKernel);
            kernelUp = true;
            double[] ts = { 0.062740, 0.066655, 0.081203, 0.293867, 0.305857, 0.317838, 0.323476, 1.204511, 2.511023, 3.104822 };
            string[] msg = { "unable to find tag: 0x30444957", "unable to find tag: 0x31444957", "evb_clk: init done", "",
                             " compile 10:52:46 Oct 12 2026 (kernel)", " - en(0xffffffff:0x0) hdr(0xffffffff) wide(0x0)",
                             " - sie(28:29:26:26:0:0)", "ubi0: attached mtd5 (name \"rootfs\")", "VFS: Mounted root (squashfs filesystem) readonly",
                             "Freeing unused kernel memory: 1024K" };
            for (int i = 0; i < ts.Length; i++)
            {
                string line = KTime(ts[i]) + " " + msg[i];
                lock (kmsg) kmsg.Add(line);
                Emit(line + "\r\n");
                Sleep(Options.KernelLineIntervalMs, false);
            }
            Emit(Hostname + " Linux shell...\r\n");
            Emit("Start Main App\r\n");
            Emit("\x1b[33m[amixer] audio-out = 80% (SPK Volume)\x1b[0m\r\n");
            Sleep(Options.InitDelayMs, false);
            Emit("[START APP] 1970-01-01 00:00:03.968362\r\n");
            kernelOffsetSec = Math.Max(4.0 - (NowMs - bootStartMs) / 1000.0, 0.0);
        }

        void UbootWindow()
        {
            phase = "uboot";
            SetTty(Tty.UBoot);
            Emit("\r\n\r\nU-Boot 2019.04 (sim)\r\n\r\nHit any key to stop autoboot:  1 ");
            var k = NextByte(Options.UbootWindowMs);
            if (k == null) { Emit("\b\b\b 0 \r\n"); return; }
            lock (inLock) inQ.Clear();
            Emit("\b\b\b 0 \r\n");
            while (true)
            {
                Emit("evb: ");
                var sb = new StringBuilder();
                while (true)
                {
                    byte b = NextByte().Value.B;
                    if (b == 0x0d || b == 0x0a) { Emit("\r\n"); break; }
                    if (b == 0x03) { Emit("<INTERRUPT>\r\n"); sb.Clear(); break; }
                    if (b == 0x7f || b == 0x08) { if (sb.Length > 0) { sb.Length--; Emit("\b \b"); } continue; }
                    if (b < 0x20) continue;
                    sb.Append((char)b);
                    EmitBytes(new[] { b });
                }
                string cmd = sb.ToString().Trim();
                if (cmd.Length == 0) continue;
                if (cmd == "boot" || cmd == "bootm" || cmd == "bootd") return;
                if (cmd == "reset") { Emit("resetting ...\r\n"); throw new ResetException(); }
                if (cmd == "printenv") { Emit("baudrate=115200\r\nbootcmd=evb_boot\r\nbootdelay=1\r\n"); continue; }
                if (cmd == "version") { Emit("U-Boot 2019.04 (sim)\r\n"); continue; }
                Emit("Unknown command '" + cmd.Split(' ')[0] + "' - try 'help'\r\n");
            }
        }

        // ================================================================== 커널 로그
        internal string[] KmsgSnapshot() { lock (kmsg) return kmsg.ToArray(); }

        internal void KmsgWrite(string text)
        {
            foreach (var raw in text.Split('\n'))
            {
                if (raw.Length == 0) continue;
                int level = 4;
                string msg = raw;
                if (raw.Length >= 3 && raw[0] == '<' && char.IsDigit(raw[1]) && raw[2] == '>') { level = raw[1] - '0'; msg = raw.Substring(3); }
                if (level < Options.ConsoleLogLevel) EmitPrintk(msg);
                else lock (kmsg) kmsg.Add(KTime(Uptime) + " " + msg);
            }
        }

        public bool WaitPhase(string p, int timeoutMs)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline) { if (phase == p) return true; Thread.Sleep(5); }
            return phase == p;
        }
    }
}
