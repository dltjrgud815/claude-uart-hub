// 케이블별 단일 디스크 writer(§3.1, §3.8). 큐 → .raw(바이트 원본) / .log(줄 조립·타임스탬프) / .idx(JSON lines).
// 파일명 <log_root>\YYYYMMDD\<TCP포트>_<보드명|unknown>_<HHMMSS>[_n].{log,raw,idx}
// 전환: 자정·rotate_mb 초과·보드 변경(다음 줄부터). 보드명 최초 감지가 파일 생성 rename_window_s 이내면 rename.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace UartHub
{
    internal sealed class FileSet
    {
        public string Dir, DateKey, BaseName, Board;
        public DateTime CreatedWall;
        public long CreatedMono;
        public FileStream Raw, LogFs, IdxFs;
        public StreamWriter Log, Idx;
        public long RawBytes, LogBytes;

        public string PathOf(string ext) => System.IO.Path.Combine(Dir, BaseName + ext);

        public void Flush()
        {
            Log?.Flush(); Idx?.Flush(); Raw?.Flush();
        }

        public void Close()
        {
            try { Log?.Flush(); } catch (Exception) { }
            try { Idx?.Flush(); } catch (Exception) { }
            try { Log?.Dispose(); } catch (Exception) { }
            try { Idx?.Dispose(); } catch (Exception) { }
            try { Raw?.Dispose(); } catch (Exception) { }
            Log = null; Idx = null; Raw = null; LogFs = null; IdxFs = null;
        }
    }

    public sealed class DiskWriter
    {
        static readonly string[] Exts = { ".raw", ".log", ".idx" };
        const FileShare Share = FileShare.ReadWrite | FileShare.Delete;

        readonly Cable cable;
        readonly Config cfg;
        readonly IClock clock;
        readonly HubLog hlog;
        readonly StateStore state;
        readonly ChunkQueue queue;
        readonly BoardMatcher matcher;
        readonly ManualResetEventSlim gate = new ManualResetEventSlim(true);
        readonly ManualResetEventSlim pausedAck = new ManualResetEventSlim(false);
        Thread thread;

        // 세션(attach 단위)
        bool inSession, hubStartWritten;
        long attachMono;
        string attachCom;
        bool haveRx;
        long lastRxMono;

        // 파일
        FileSet cur;
        long rawAbs;                       // .raw 에 쓴 RX 의 절대 offset 끝(오류 때 이 청크에서 못 쓴 양 계산)
        string errNote;                    // 오류 뒤 다음 새 파일 첫머리에 남길 줄(P1: 손실을 .log 에 표시)
        int errCount;
        long errLost;

        // 줄 조립
        readonly StringBuilder line = new StringBuilder();
        Decoder dec = NewDecoder();
        char[] cbuf = new char[16384];
        bool atLineStart = true;
        long lineStartOff, lineStartAbs, lineLastMono;
        DateTime lineLastWall;
        bool boardSeenInLine;

        // 보드·부팅
        volatile string board;
        bool acceptNewBoard = true;
        long lastBootMono = long.MinValue;
        bool pendingSwitch;
        string pendingReason;

        // 상태 공개
        volatile string curLog, curRaw, curIdx;
        long writtenRaw, processedAbs, errors, bootCount, attachedSeq;
        long lastBeatMono = long.MinValue;

        internal DiskWriter(Cable cable, Config cfg, IClock clock, HubLog hlog, StateStore state, ChunkQueue queue)
        {
            this.cable = cable; this.cfg = cfg; this.clock = clock; this.hlog = hlog; this.state = state; this.queue = queue;
            matcher = new BoardMatcher(cfg);
            board = state.GetBoard(cable.Id);
        }

        static Decoder NewDecoder() => new UTF8Encoding(false, false).GetDecoder();

        /// <summary>
        /// 이 프로세스의 첫 attach 에서 "*** hub start ***" 바로 다음에 쓸 줄(코어 재시작 사유·공백, §3.8). 인자 = TCP 포트.
        /// Hub 가 CableManager.StartPreamble 로 넣는다. 처리기 예외는 기록을 막지 않는다(P1).
        /// </summary>
        internal Func<int, IReadOnlyList<string>> StartPreamble;

        IReadOnlyList<string> StartPreambleLines()
        {
            var f = StartPreamble;
            if (f == null) return Array.Empty<string>();
            try { return f(cable.TcpPort) ?? (IReadOnlyList<string>)Array.Empty<string>(); }
            catch (Exception e)
            {
                hlog.Write("writer " + cable.Id + " 시작 머리 줄 실패: " + e.GetType().Name + ": " + e.Message);
                return Array.Empty<string>();
            }
        }

        public string Board => board;
        public string CurrentLogPath => curLog;
        public string CurrentRawPath => curRaw;
        public string CurrentIdxPath => curIdx;
        public long WrittenRawBytes => Interlocked.Read(ref writtenRaw);
        public long Errors => Interlocked.Read(ref errors);
        public long BootCount => Interlocked.Read(ref bootCount);
        /// <summary>마지막으로 처리한 attach 순번(Cable.AttachSeq). 같아지면 이전 세션 파일은 닫혔고 CurrentRawPath 는 새 파일이다.</summary>
        public long AttachedSeq => Interlocked.Read(ref attachedSeq);
        public bool IsIdle => queue.IsDrained;

        internal void Start()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "DiskWriter-" + cable.Id };
            thread.Start();
        }

        internal bool Join(int ms) => thread == null || thread.Join(ms);

        /// <summary>시험용: writer를 인위 정지시킨다(큐 소비 중단). 정지가 확인되면 true.</summary>
        public bool TestPause(int timeoutMs = 2000)
        {
            gate.Reset();
            return pausedAck.Wait(timeoutMs);
        }

        public void TestResume() => gate.Set();

        int testFail;
        /// <summary>시험용: 다음 데이터 청크 처리 중 IOException 을 한 번 던진다(오류 경로 검증).</summary>
        public void TestFailNextData() => Interlocked.Exchange(ref testFail, 1);

        // ------------------------------------------------------------------ 루프
        void Run()
        {
            var flushSw = Stopwatch.StartNew();
            bool dirty = false;
            while (true)
            {
                if (!gate.IsSet)
                {
                    try { cur?.Flush(); } catch (Exception) { }
                    pausedAck.Set();
                    gate.Wait();
                    pausedAck.Reset();
                }
                QItem it = queue.Dequeue(cfg.WriterWaitMs);
                bool stop = false;
                try
                {
                    if (it != null)
                    {
                        Process(it);
                        dirty = true;
                        stop = it.Kind == QKind.Stop;
                    }
                    TailFlushCheck();
                    if (it == null || stop || flushSw.ElapsedMilliseconds >= 200)
                    {
                        if (dirty) cur?.Flush();
                        dirty = false;
                        flushSw.Restart();
                        if (it == null) queue.MarkFlushed();
                    }
                    Heartbeat();
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref errors);
                    hlog.Write("writer " + cable.Id + " 오류: " + e.GetType().Name + ": " + e.Message);
                    long lost = 0;
                    if (it != null && it.Kind == QKind.Data && it.Data != null)
                    {
                        long end = it.Abs + it.Data.Length;
                        lost = end - Math.Min(end, Math.Max(it.Abs, rawAbs));   // 이 청크에서 .raw 에 못 쓴 바이트
                        Interlocked.Exchange(ref processedAbs, end);            // 다음 파일의 open abs 가 실제 다음 바이트를 가리키게
                    }
                    NoteError(e, lost);
                    SafeCloseCur();
                }
                if (stop) break;
            }
        }

        void Process(QItem it)
        {
            switch (it.Kind)
            {
                case QKind.Data: OnData(it); break;
                case QKind.Drop: OnDrop(it); break;
                case QKind.Attach: OnAttach(it); break;
                case QKind.Detach: OnDetach(it); break;
                case QKind.Event: OnEvent(it); break;
                case QKind.Stop: OnStop(it); break;
            }
        }

        // ------------------------------------------------------------------ 세션
        void OnAttach(QItem it)
        {
            if (inSession) EndSession(it.Wall, it.Mono, "*** detach (implicit) ***");
            BeginSession(it.Wall, it.Mono, it.Com);
            if (!hubStartWritten)
            {
                WriteEventLine(it.Wall, it.Mono, "*** hub start (UartHub " + BuildInfo.Version + ") ***");
                hubStartWritten = true;
                foreach (var l in StartPreambleLines()) WriteEventLine(it.Wall, it.Mono, l);
            }
            WriteEventLine(it.Wall, it.Mono, "*** attach " + it.Com + " cable=" + cable.Id + " port=" + cable.TcpPort + " baud=" + cable.Baud
                                             + (string.IsNullOrEmpty(it.Text) ? "" : " " + it.Text) + " ***");
            Interlocked.Exchange(ref attachedSeq, it.Seq);
        }

        void BeginSession(DateTime wall, long mono, string com)
        {
            inSession = true;
            attachMono = mono;
            attachCom = com;
            haveRx = false;
            line.Clear(); dec = NewDecoder(); atLineStart = true; boardSeenInLine = false;
            acceptNewBoard = true;
            lastBootMono = long.MinValue;
            pendingSwitch = false;
            board = state.GetBoard(cable.Id) ?? board;
            cur = OpenSet(wall, mono, "attach");
            UpdatePaths();
        }

        void OnDetach(QItem it)
        {
            if (!inSession && cur == null) return;
            EndSession(it.Wall, it.Mono, "*** detach " + it.Com + (string.IsNullOrEmpty(it.Text) ? "" : " (" + it.Text + ")") + " ***");
        }

        void OnStop(QItem it)
        {
            if (cur == null) return;
            EndSession(it.Wall, it.Mono, it.Text ?? "*** hub stop ***");
        }

        void EndSession(DateTime wall, long mono, string text)
        {
            FlushPartialLine();
            if (cur != null) WriteEventLine(wall, mono, text);
            SafeCloseCur();
            inSession = false;
            haveRx = false;
        }

        void OnEvent(QItem it)
        {
            if (!inSession && cur == null) return;      // 분리 중 이벤트는 기록할 파일이 없다
            WriteEventLine(it.Wall, it.Mono, it.Text);
        }

        /// <summary>부분 줄을 먼저 내보내고(줄 시작 보장) 이벤트 줄 + .idx 이벤트 기록.</summary>
        void WriteEventLine(DateTime wall, long mono, string text)
        {
            FlushPartialLine();
            EnsureFile(wall, mono);
            WriteLogLine(cur, wall, mono, text);
            Idx(cur, "{\"k\":\"event\",\"off\":" + cur.RawBytes + ",\"el\":" + El(mono) + ",\"text\":" + Json.Str(text) + "}");
        }

        // ------------------------------------------------------------------ 데이터
        void OnData(QItem it)
        {
            if (!inSession) BeginSession(it.Wall, it.Mono, cable.Com);
            if (testFail != 0 && Interlocked.Exchange(ref testFail, 0) != 0) throw new IOException("시험 주입 writer 오류");
            // 꼬리 기록은 도착 시각으로 판정한다: writer 가 늦게 깨어나도(디스크 지연) 결과가 같다.
            if (!atLineStart && it.Mono - lineLastMono >= cfg.TailFlushMs) FlushPartialLine();
            byte[] d = it.Data;
            int len = d.Length, pos = 0;
            long gap = it.Mono - (haveRx ? lastRxMono : attachMono);
            bool gapPending = gap >= cfg.GapMs;
            lastRxMono = it.Mono;
            haveRx = true;

            while (pos < len)
            {
                if (atLineStart)
                {
                    EnsureFile(it.Wall, it.Mono);
                    lineStartOff = cur.RawBytes;
                    lineStartAbs = it.Abs + pos;
                    atLineStart = false;
                    boardSeenInLine = false;
                }
                else if (cur == null) EnsureFile(it.Wall, it.Mono);

                if (gapPending)
                {
                    Idx(cur, "{\"k\":\"gap\",\"off\":" + cur.RawBytes + ",\"ms\":" + gap + ",\"el\":" + El(it.Mono) + "}");
                    gapPending = false;
                }

                int nl = Array.IndexOf(d, (byte)'\n', pos, len - pos);
                int end = nl < 0 ? len : nl + 1;
                WriteRaw(d, pos, end - pos);
                rawAbs = it.Abs + end;
                AppendDecoded(d, pos, end - pos);
                lineLastMono = it.Mono;
                lineLastWall = it.Wall;
                if (nl >= 0) CompleteLine();
                else OnPartial();
                pos = end;
            }
            Interlocked.Exchange(ref processedAbs, it.Abs + len);

            // 개행 없는 거대 출력: rotate 상한 + 1MB 를 넘으면 줄 중간이라도 끊어서 전환을 허용
            if (!atLineStart && cur != null && cur.RawBytes > cfg.RotateBytes + (1 << 20)) FlushPartialLine();
        }

        void OnDrop(QItem it)
        {
            FlushPartialLine();
            dec = NewDecoder();                         // 손실로 끊긴 멀티바이트 상태 폐기
            EnsureFile(it.Wall, it.Mono);
            Idx(cur, "{\"k\":\"drop\",\"off\":" + cur.RawBytes + ",\"abs\":" + Json.Str(cable.Ring.Format(it.Abs)) + ",\"n\":" + it.Count + ",\"el\":" + El(it.Mono) + "}");
            WriteLogLine(cur, it.Wall, it.Mono, "*** overflow dropped " + it.Count + " bytes ***");
            Interlocked.Exchange(ref processedAbs, it.Abs + it.Count);
            lastRxMono = it.Mono;
            haveRx = true;
        }

        void WriteRaw(byte[] d, int off, int n)
        {
            cur.Raw.Write(d, off, n);
            cur.RawBytes += n;
            Interlocked.Add(ref writtenRaw, n);
        }

        void AppendDecoded(byte[] d, int off, int n)
        {
            if (cbuf.Length < n + 8) cbuf = new char[n + 8];
            int cn = dec.GetChars(d, off, n, cbuf, 0, false);
            for (int i = 0; i < cn; i++)
            {
                char c = cbuf[i];
                if (c == '\n' || c == '\0') continue;
                line.Append(c);
            }
        }

        void CompleteLine()
        {
            string text = line.ToString().TrimEnd('\r');
            WriteLogLine(cur, lineLastWall, lineLastMono, text);
            Analyze(text);
            line.Clear();
            atLineStart = true;
        }

        void OnPartial()
        {
            if (boardSeenInLine || line.Length == 0 || line.Length > 4096) return;
            string name = matcher.MatchBoard(BoardMatcher.Normalize(line.ToString()));
            if (name != null) { boardSeenInLine = true; OnBoardSeen(name); }
        }

        void TailFlushCheck()
        {
            if (atLineStart) return;
            if (clock.MonoMs - lineLastMono >= cfg.TailFlushMs) FlushPartialLine();
        }

        /// <summary>개행 없는 꼬리를 그대로 한 줄로 기록(구 브리지 400ms 규칙). 이후 바이트는 새 줄로 간다.</summary>
        void FlushPartialLine()
        {
            if (atLineStart) return;
            if (line.Length > 0 && cur != null)
            {
                string text = line.ToString();
                WriteLogLine(cur, lineLastWall, lineLastMono, text);
                Analyze(text);
            }
            line.Clear();
            atLineStart = true;
        }

        // ------------------------------------------------------------------ 최소 매처
        void Analyze(string text)
        {
            string norm = BoardMatcher.Normalize(text);
            if (!boardSeenInLine)
            {
                string name = matcher.MatchBoard(norm);
                if (name != null) { boardSeenInLine = true; OnBoardSeen(name); }
            }
            string boot = matcher.MatchBoot(norm);
            if (boot != null) OnBoot(boot);
        }

        void OnBoot(string m)
        {
            if (lastBootMono != long.MinValue && lineLastMono - lastBootMono < cfg.BootMergeMs) return;   // 같은 부팅
            lastBootMono = lineLastMono;
            acceptNewBoard = true;
            Interlocked.Increment(ref bootCount);
            if (cur == null) return;
            Idx(cur, "{\"k\":\"boot\",\"off\":" + lineStartOff + ",\"abs\":" + Json.Str(cable.Ring.Format(lineStartAbs))
                     + ",\"el\":" + El(lineLastMono) + ",\"ts\":" + Json.Str(Ts(lineLastWall))
                     + ",\"board\":" + Json.Str(board) + ",\"m\":" + Json.Str(m) + "}");
        }

        void OnBoardSeen(string name)
        {
            if (board == null)
            {
                SetBoard(name, null);
                acceptNewBoard = false;
                if (cur != null && cur.Board == null)
                {
                    bool inWindow = lineLastMono - cur.CreatedMono <= (long)(cfg.RenameWindowS * 1000);
                    if (!(inWindow && TryRename(cur, name))) { pendingSwitch = true; pendingReason = "board"; }
                }
            }
            else if (name == board)
            {
                acceptNewBoard = false;
            }
            else if (acceptNewBoard)
            {
                SetBoard(name, board);
                acceptNewBoard = false;
                pendingSwitch = true;
                pendingReason = "board";
            }
            // 그 밖(부팅 경계 없이 다른 호스트명): 데이터·중첩 셸로 보고 무시
        }

        void SetBoard(string name, string prev)
        {
            board = name;
            try { state.SetBoard(cable.Id, name); } catch (Exception e) { hlog.Write("state.json 보드명 저장 실패: " + e.Message); }
            if (cur != null)
                Idx(cur, "{\"k\":\"board\",\"off\":" + (atLineStart ? cur.RawBytes : lineStartOff) + ",\"el\":" + El(lineLastMono)
                         + ",\"board\":" + Json.Str(name) + ",\"prev\":" + Json.Str(prev) + "}");
        }

        // ------------------------------------------------------------------ 파일
        /// <summary>줄 시작에서만 호출: 필요하면 전환(자정·크기·보드 변경)하고 현재 파일을 보장.</summary>
        void EnsureFile(DateTime wall, long mono)
        {
            if (cur == null) { cur = OpenSet(wall, mono, inSession ? "reopen" : "open"); UpdatePaths(); return; }
            string reason = null;
            if (wall.ToString("yyyyMMdd", CultureInfo.InvariantCulture) != cur.DateKey) reason = "midnight";
            else if (Math.Max(cur.RawBytes, cur.LogBytes) >= cfg.RotateBytes) reason = "size";
            else if (pendingSwitch) reason = pendingReason;
            if (reason != null) Switch(reason, wall, mono);
        }

        void Switch(string reason, DateTime wall, long mono)
        {
            var old = cur;
            var next = OpenSet(wall, mono, reason);
            WriteLogLine(old, wall, mono, "*** rotate (" + reason + ") -> " + next.BaseName + " ***");
            Idx(old, "{\"k\":\"rotate\",\"off\":" + old.RawBytes + ",\"el\":" + El(mono) + ",\"reason\":" + Json.Str(reason) + ",\"to\":" + Json.Str(next.BaseName) + "}");
            old.Close();
            cur = next;
            pendingSwitch = false;
            WriteLogLine(cur, wall, mono, "*** rotate (" + reason + ") <- " + old.BaseName + " ***");
            UpdatePaths();
        }

        FileSet OpenSet(DateTime wall, long mono, string reason)
        {
            string dateKey = wall.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            string dir = Path.Combine(cfg.LogRoot, dateKey);
            Directory.CreateDirectory(dir);
            string baseName = BaseNameFor(board, wall);
            for (int n = 1; n < 1000; n++)
            {
                string name = n == 1 ? baseName : baseName + "_" + n.ToString(CultureInfo.InvariantCulture);
                if (AnyExists(dir, name)) continue;
                var fs = new FileSet { Dir = dir, DateKey = dateKey, BaseName = name, Board = board, CreatedWall = wall, CreatedMono = mono };
                try
                {
                    fs.Raw = new FileStream(fs.PathOf(".raw"), FileMode.CreateNew, FileAccess.Write, Share, 65536);
                    fs.LogFs = new FileStream(fs.PathOf(".log"), FileMode.CreateNew, FileAccess.Write, Share, 4096);
                    fs.IdxFs = new FileStream(fs.PathOf(".idx"), FileMode.CreateNew, FileAccess.Write, Share, 4096);
                }
                catch (IOException) when (AnyExists(dir, name))
                {
                    fs.Raw?.Dispose(); fs.LogFs?.Dispose(); fs.IdxFs?.Dispose();
                    continue;   // 경합으로 이름이 생김 → 다음 접미사
                }
                fs.Log = new StreamWriter(fs.LogFs, new UTF8Encoding(false), 65536);
                fs.Idx = new StreamWriter(fs.IdxFs, new UTF8Encoding(false), 4096);
                Idx(fs, "{\"k\":\"open\",\"off\":0,\"abs\":" + Json.Str(cable.Ring.Format(Interlocked.Read(ref processedAbs)))
                        + ",\"el\":" + El(mono) + ",\"ts\":" + Json.Str(Ts(wall)) + ",\"reason\":" + Json.Str(reason)
                        + ",\"cable\":" + Json.Str(cable.Id) + ",\"com\":" + Json.Str(attachCom ?? cable.Com)
                        + ",\"port\":" + cable.TcpPort + ",\"board\":" + Json.Str(board) + "}");
                if (errNote != null)
                {
                    WriteLogLine(fs, wall, mono, errNote);
                    Idx(fs, "{\"k\":\"event\",\"off\":0,\"el\":" + El(mono) + ",\"text\":" + Json.Str(errNote) + "}");
                    errNote = null; errCount = 0; errLost = 0;
                }
                return fs;
            }
            throw new IOException("빈 파일명을 찾지 못함: " + Path.Combine(dir, baseName));
        }

        /// <summary>
        /// 오류 경로(P1 침묵 실패 금지): 현재 파일은 닫히고 남은 청크·버퍼는 잃을 수 있다. 다음에 여는 새 파일 첫머리에
        /// "*** writer error … ***" 를 남기도록 모아 둔다(연속 오류는 횟수·손실 합계로 한 줄).
        /// </summary>
        void NoteError(Exception e, long lost)
        {
            errCount++;
            errLost += lost;
            string msg = (e.GetType().Name + ": " + e.Message).Replace('\r', ' ').Replace('\n', ' ');
            errNote = "*** writer error " + (errCount > 1 ? "x" + errCount + " " : "") + msg
                      + " (기록 못 한 RX " + errLost + "B 이상, 앞 파일은 닫힘) ***";
        }

        /// <summary>&lt;TCP포트&gt;_&lt;보드명|unknown&gt;_&lt;HHMMSS&gt;. 이름이 겹치면 호출자가 _n 을 붙인다.</summary>
        string BaseNameFor(string boardName, DateTime wall)
            => cable.TcpPort.ToString(CultureInfo.InvariantCulture) + "_" + Names.SafeLabel(boardName) + "_" + wall.ToString("HHmmss", CultureInfo.InvariantCulture);

        static bool AnyExists(string dir, string name)
        {
            foreach (var e in Exts) if (File.Exists(Path.Combine(dir, name + e))) return true;
            return false;
        }

        /// <summary>열린 파일 세트를 보드명으로 rename(FileShare.Delete 로 열었으므로 가능). 실패하면 원복하고 false.</summary>
        bool TryRename(FileSet fs, string newBoard)
        {
            string baseName = BaseNameFor(newBoard, fs.CreatedWall);
            for (int n = 1; n < 1000; n++)
            {
                string name = n == 1 ? baseName : baseName + "_" + n.ToString(CultureInfo.InvariantCulture);
                if (AnyExists(fs.Dir, name)) continue;
                var moved = new List<string>();
                try
                {
                    foreach (var e in Exts)
                    {
                        File.Move(Path.Combine(fs.Dir, fs.BaseName + e), Path.Combine(fs.Dir, name + e));
                        moved.Add(e);
                    }
                }
                catch (Exception ex)
                {
                    for (int i = moved.Count - 1; i >= 0; i--)
                    {
                        try { File.Move(Path.Combine(fs.Dir, name + moved[i]), Path.Combine(fs.Dir, fs.BaseName + moved[i])); } catch (Exception) { }
                    }
                    hlog.Write("rename 실패(" + fs.BaseName + " → " + name + "): " + ex.Message);
                    return false;
                }
                string from = fs.BaseName;
                fs.BaseName = name;
                fs.Board = newBoard;
                Idx(fs, "{\"k\":\"rename\",\"from\":" + Json.Str(from) + ",\"to\":" + Json.Str(name) + "}");
                UpdatePaths();
                return true;
            }
            return false;
        }

        void SafeCloseCur()
        {
            try { cur?.Close(); } catch (Exception) { }
            cur = null;
            UpdatePaths();
        }

        void UpdatePaths()
        {
            var f = cur;
            curLog = f?.PathOf(".log");
            curRaw = f?.PathOf(".raw");
            curIdx = f?.PathOf(".idx");
        }

        void WriteLogLine(FileSet fs, DateTime wall, long mono, string text)
        {
            string s = "[" + Ts(wall) + " +" + El(mono, true) + "] " + text + "\r\n";
            fs.Log.Write(s);
            fs.LogBytes += Encoding.UTF8.GetByteCount(s);
        }

        static void Idx(FileSet fs, string json)
        {
            fs.Idx.Write(json);
            fs.Idx.Write('\n');
        }

        static string Ts(DateTime wall) => wall.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

        /// <summary>attach 기준 경과초. pad=true 면 구 브리지 형식 {0,9:F3}.</summary>
        string El(long mono, bool pad = false)
        {
            double el = (mono - attachMono) / 1000.0;
            string s = el.ToString("F3", CultureInfo.InvariantCulture);
            return pad ? s.PadLeft(9) : s;
        }

        void Heartbeat()
        {
            if (cable.TcpPort <= 0) return;
            long now = clock.MonoMs;
            if (lastBeatMono != long.MinValue && now - lastBeatMono < (long)(cfg.HeartbeatS * 1000)) return;
            lastBeatMono = now;
            try
            {
                AtomicFile.WriteAllText(Path.Combine(cfg.EffectiveHubDir, "alive", cable.TcpPort.ToString(CultureInfo.InvariantCulture)),
                                        clock.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture) + "\n");
            }
            catch (Exception e) { hlog.Write("alive 갱신 실패: " + e.Message); }
        }
    }
}
