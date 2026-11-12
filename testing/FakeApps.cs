// FakeBoard 전체화면 앱(vi, top)과 중첩 셸. vi 는 실측(부록 A S0-10) 바이트열을 따른다.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace UartHub.Testing
{
    public sealed partial class FakeBoard
    {
        /// <summary>vi: ESC[?1049h ESC[999;999H ESC[6n → CSI H 로 그리기. :q/:q!/:wq. ESC 뒤 window 안의 키는 시퀀스로 삼킨다.</summary>
        internal int RunVi(FakeShell sh, string path)
        {
            Interlocked.Increment(ref viSessions);
            string prev = phase;
            phase = "vi";
            SetTty(Tty.Raw);
            try
            {
                var lines = new List<StringBuilder>();
                var content = path != null ? FsRead(path) : null;
                if (content != null && content.Length > 0)
                    foreach (var l in Encoding.UTF8.GetString(content).TrimEnd('\n').Split('\n')) lines.Add(new StringBuilder(l));
                if (lines.Count == 0) lines.Add(new StringBuilder());
                bool dirty = false;
                int row = 0;
                string name = path ?? "[No Name]";

                void Status() => Emit("\x1b[24;1H\x1b[K- " + name + " " + (row + 1) + "/" + lines.Count + " 100%\x1b[" + (row + 1) + ";1H");
                void Redraw()
                {
                    var sb = new StringBuilder("\x1b[H\x1b[J");
                    for (int r = 1; r <= 23; r++)
                    {
                        if (r - 1 < lines.Count) { if (lines[r - 1].Length > 0) sb.Append("\x1b[" + r + ";1H").Append(lines[r - 1].ToString(0, Math.Min(80, lines[r - 1].Length))); }
                        else sb.Append("\x1b[" + r + ";1H~");
                    }
                    sb.Append("\x1b[1;1H");
                    Emit(sb.ToString());
                    Status();
                }
                bool Save()
                {
                    if (path == null) return false;
                    var text = string.Join("\n", lines.Select(l => l.ToString())) + "\n";
                    FsWrite(path, Encoding.UTF8.GetBytes(text), false);
                    dirty = false;
                    return true;
                }

                Emit("\x1b[?1049h\x1b[999;999H" + (Options.ViCprQuery ? "\x1b[6n" : ""));
                Redraw();
                bool insert = false;
                StringBuilder ex = null;
                while (true)
                {
                    var ib = NextByte().Value;
                    byte b = ib.B;
                    bool esc = false;
                    if (b == 0x1b)
                    {
                        string seq = ReadEscape(ib.T);
                        if (seq == null) esc = true;
                        else { if (seq[0] == '?') Emit("\a"); continue; }      // 방향키·CPR 응답·알 수 없는 시퀀스
                    }
                    if (ex != null)
                    {
                        if (esc) { ex = null; Status(); continue; }
                        if (b == 0x0d || b == 0x0a)
                        {
                            string c = ex.ToString().Trim();
                            ex = null;
                            if (c == "q")
                            {
                                if (dirty) { Emit("\x1b[24;1H\x1b[KNo write since last change (:quit! overrides)\x1b[" + (row + 1) + ";1H"); continue; }
                                break;
                            }
                            if (c == "q!") break;
                            if (c == "w") { Save(); Status(); continue; }
                            if (c == "wq" || c == "x") { Save(); break; }
                            Emit("\x1b[24;1H\x1b[K'" + c + "' is not implemented\x1b[" + (row + 1) + ";1H");
                            continue;
                        }
                        if (b == 0x7f || b == 0x08) { if (ex.Length > 0) { ex.Length--; Emit("\b \b"); } else { ex = null; Status(); } continue; }
                        if (b >= 0x20) { ex.Append((char)b); EmitBytes(new[] { b }); }
                        continue;
                    }
                    if (insert)
                    {
                        if (esc) { insert = false; Status(); continue; }
                        if (b == 0x0d) { lines.Insert(row + 1, new StringBuilder()); row++; dirty = true; Interlocked.Increment(ref viInserted); Emit("\r\n"); continue; }
                        if (b == 0x7f || b == 0x08) { if (lines[row].Length > 0) { lines[row].Length--; dirty = true; Emit("\b \b"); } continue; }
                        if (b >= 0x20 || b == 0x09) { lines[row].Append((char)b); dirty = true; Interlocked.Increment(ref viInserted); EmitBytes(new[] { b }); }
                        continue;
                    }
                    if (esc) { Emit("\a"); continue; }
                    switch ((char)b)
                    {
                        case ':': ex = new StringBuilder(); Emit("\x1b[24;1H\x1b[K:"); break;
                        case 'i': case 'a': case 'A': case 'I':
                            insert = true;
                            Emit("\x1b[24;1H\x1b[K-- INSERT --\x1b[" + (row + 1) + ";" + (lines[row].Length + 1) + "H");
                            break;
                        case 'o': lines.Insert(row + 1, new StringBuilder()); row++; dirty = true; insert = true; Redraw(); break;
                        case 'j': if (row < lines.Count - 1) { row++; Status(); } break;
                        case 'k': if (row > 0) { row--; Status(); } break;
                        case 'x': if (lines[row].Length > 0) { lines[row].Length--; dirty = true; Redraw(); } break;
                        default: break;
                    }
                }
                Emit("\x1b[24;1H\x1b[K\x1b[?1049l");
                return 0;
            }
            finally
            {
                SetTty(Tty.Canonical);
                phase = prev;
            }
        }

        /// <summary>top: CSI H/J 로 화면을 주기적으로 다시 그린다. q 또는 ^C 로 종료.</summary>
        internal int RunTop()
        {
            string prev = phase;
            phase = "top";
            SetTty(Tty.Raw);
            try
            {
                int tick = 0;
                while (true)
                {
                    tick++;
                    Emit("\x1b[H\x1b[J" +
                         "Mem: 183520K used, 812344K free, 0K shrd, 1204K buff, 45232K cached\r\n" +
                         "CPU:   3% usr   1% sys   0% nic  95% idle   0% io   0% irq   0% sirq\r\n" +
                         "Load average: 0.12 0.10 0.05 1/123 " + (400 + tick) + "\r\n" +
                         "\x1b[7m  PID  PPID USER     STAT   VSZ %VSZ CPU %CPU COMMAND\x1b[0m\r\n" +
                         "  412     1 root     S    61236   6%   2   2% /usr/bin/myapp\r\n" +
                         "  455     1 root     S    48212   5%   1   1% /usr/bin/myrecord\r\n" +
                         (400 + tick) + "   301 root     R     2944   0%   0   0% top");
                    double until = NowMs + Options.TopIntervalMs;
                    bool quit = false;
                    while (true)
                    {
                        double left = until - NowMs;
                        if (left <= 0) break;
                        var k = NextByte(left);
                        if (k == null) break;
                        if (k.Value.B == (byte)'q' || k.Value.B == 0x03) { quit = true; break; }
                    }
                    if (quit) break;
                }
                Emit("\r\n");
                return 0;
            }
            finally
            {
                SetTty(Tty.Canonical);
                phase = prev;
            }
        }

        /// <summary>stdin 이 tty 인 `sh`: 같은 tty 에서 중첩 대화형 셸(foreign prompt 시험용). exit 하면 부모로 돌아간다.</summary>
        internal int NestedInteractive(FakeShell child)
        {
            try { InteractiveLoop(child, true); }
            catch (ExitShellException e) { SetTty(Tty.Canonical); return e.Code; }
            return 0;
        }

        /// <summary>백그라운드 작업의 sleep: SIGINT·리셋 플래그를 소비하지 않는다.</summary>
        internal void BackgroundSleep(double ms)
        {
            double until = NowMs + ms;
            while (NowMs < until)
            {
                lock (inLock) { if (powerOff) throw new PowerOffException(); }
                Thread.Sleep((int)Math.Max(1, Math.Min(20, until - NowMs)));
            }
        }
    }
}
