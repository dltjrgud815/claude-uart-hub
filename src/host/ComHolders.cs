// COM 점유 프로세스 찾기·끝내기(계획 X5 부록 B.2·B.3, US-112). 감독의 /connect-uart 흐름(ConnectFlow)·F1 풍선·인자 없는 connect 표가 쓴다.
//   찾기(읽기 전용 — 다른 프로세스의 핸들을 복제해 이름만 본다, 원본은 닫지 않는다):
//     QueryDosDevice(COMx) → \Device\VCPn. NtQuerySystemInformation(SystemExtendedHandleInformation) 의 핸들 표에서 File 형식 핸들만
//     (형식 번호는 자기 exe 파일 핸들로 찾음, 항목 오프셋 ObjectTypeIndex = 30), 읽기+쓰기로 연 핸들만(GrantedAccess 오프셋 24 — FILE_READ_DATA|FILE_WRITE_DATA,
//     멈추기 쉬운 0x001A019F 제외), 같은 세션 프로세스에서 DuplicateHandle(DUPLICATE_SAME_ACCESS) → GetFileType == FILE_TYPE_CHAR 만
//     NtQueryObject(ObjectNameInformation). 장치 이름이 같으면 그 프로세스가 점유자다.
//     후보 먼저(백신이 핸들 가로채기로 보지 않게 여는 프로세스를 줄인다): ① 알려진 시리얼 도구(허용 목록 + MobaXterm*·SecureCRT·RealTerm·CoolTerm·HTerm·
//     plink·python*·Arduino* …)만 연다. ② 사람이 부른 connect·표(FullIfNone)에서 ①로 못 찾은 COM 만 같은 세션 전체를 본다. F1 풍선은 ①만.
//     질의는 작업 스레드 하나에서 핸들마다 300ms 제한. 멈춘 질의는 그 스레드를 버리고 새 스레드로 계속한다 — 버린 스레드는 막힌 호출이 돌아오면
//     복제 핸들을 닫고 버퍼·이벤트를 정리하고 끝난다. 같은 프로세스에서 2번 멈추면 그 프로세스는 건너뛰고, 모두 6번이면 중단. 전체 5초.
//     열지 않는 프로세스: 백신·보안(Kaspersky·Defender·AhnLab V3·nProtect·Safe Transaction)·DRM(NX·Fasoo·MarkAny·DSCSLink)·시스템·셸 구성 요소,
//     config.json holder_scan_skip(이름, 끝 * 는 앞부분 일치). 프로세스 이름·부모는 Toolhelp 스냅숏(프로세스를 열지 않음).
//     Restart Manager(RmGetList \\.\COMx)는 장치에 쓸 수 없다(접근 거부 5, 스파이크 S-X5).
//   끝내기: 단일 세션 터미널 허용 목록(MoTTY.exe·ttermpro.exe·putty.exe·kitty.exe)만. OpenProcess(PROCESS_TERMINATE|QUERY_LIMITED|SYNCHRONIZE) 핸들 하나로
//     시작 시각·이미지 이름·세션이 찾을 때와 같은지, 그 pid 가 아직 그 COM 을 쥐는지 다시 본 뒤 같은 핸들로 TerminateProcess
//     (핸들을 쥐는 동안 pid 는 재사용되지 않는다). 하나라도 다르면 끝내지 않는다. WM_CLOSE(taskkill)는 MoTTY 가 무시한다(S-X5).
//   시험 모드(C-13): 실제 핸들 표·프로세스를 보지 않는다. 점유자 = <root>\fake_holders.json
//     {"COM450":[{"pid":4501,"name":"MoTTY.exe","parent":"MobaXterm.exe","start_ticks":1}]}(후보·전체·건너뛰기 규칙은 실제와 같게 적용).
//     끝내기는 같은 pid·이름·시작 시각 항목이 아직 있을 때만 그 항목을 fake_holders.json 에서 지우고(코어의 SimCore 가 이 파일을 보고 FakeBoard 를 열게 한다)
//     <root>\fake_kill.log 에 한 줄 남긴다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace UartHub
{
    /// <summary>COM 하나를 쥔 프로세스. StartTicks = 프로세스 생성 시각(UTC DateTime ticks, 0 = 모름).</summary>
    public sealed class ComHolder
    {
        public string Com, Name, Parent;
        public int Pid;
        public long StartTicks;

        /// <summary>끝내도 되는 단일 세션 터미널인가(허용 목록).</summary>
        public bool Allowed => ComHolders.IsAllowed(Name);
        /// <summary>창·풍선에 쓰는 이름(MoTTY → "MobaXterm 세션", 그 밖은 exe 이름에서 .exe 를 뺀 것).</summary>
        public string Label => ComHolders.Label(Name);

        public JObj ToJObj() => new JObj().Set("name", Name).Set("pid", (long)Pid).Set("label", Label).Set("parent", Parent);
        public override string ToString() => Label + "(pid " + Pid.ToString(CultureInfo.InvariantCulture) + (Parent != null ? ", 부모 " + Parent : "") + ")";
    }

    /// <summary>찾는 범위. Candidates = 알려진 시리얼 도구만(F1). FullIfNone = 후보로 못 찾은 COM 만 같은 세션 전체(사람이 부른 connect·표).</summary>
    public enum HolderScanMode { Candidates, FullIfNone }

    /// <summary>한 번의 찾기 결과: COM → 점유자 목록. Note = 덜 찾았을 때의 이유(시간 초과·멈춘 질의), Mode = 실제로 쓴 범위("후보" | "후보 → 전체 n개").</summary>
    public sealed class HolderScan
    {
        public readonly Dictionary<string, List<ComHolder>> ByCom = new Dictionary<string, List<ComHolder>>(StringComparer.OrdinalIgnoreCase);
        public string Note, Mode = "후보";
        public long Ms;
        public List<ComHolder> Of(string com) => com != null && ByCom.TryGetValue(com, out var l) ? l : new List<ComHolder>();
    }

    public static class ComHolders
    {
        /// <summary>끝내도 되는 단일 세션 터미널(이미지 이름 → 표시 이름). 이 목록 밖은 절대 끝내지 않는다.</summary>
        static readonly (string Exe, string Label)[] AllowList =
        {
            ("MoTTY.exe", "MobaXterm 세션"), ("ttermpro.exe", "TeraTerm"), ("putty.exe", "PuTTY"), ("kitty.exe", "KiTTY"),
        };

        /// <summary>후보(먼저 여는) 알려진 시리얼 도구 — 이름(.exe 없이, 대소문자 무시), 끝 * 는 앞부분 일치.</summary>
        static readonly string[] Candidates =
        {
            "motty", "ttermpro", "putty", "puttytel", "kitty", "kitty_portable", "mobaxterm*", "securecrt", "realterm", "coolterm", "hterm", "plink",
            "python*", "py", "arduino*", "java", "javaw", "termite", "yat", "sscom*", "uarthub*", "pwsh", "powershell",
        };

        /// <summary>열지 않는 프로세스 — 백신·보안·DRM·시스템·셸. COM 을 쥐지 않으며, 열면 백신·보안 제품이 의심할 수 있다. 끝 * 는 앞부분 일치.</summary>
        static readonly string[] Skip =
        {
            // 백신·보안
            "avp", "avpui", "kav", "kavtray", "klnagent", "ksde", "ksdeui", "klwtblfs", "kavfs", "kavfswp",
            "msmpeng", "nissrv", "mpdefendercoreservice", "securityhealthservice", "securityhealthsystray", "smartscreen", "mssense", "sense",
            "v3ltray", "v3lsvc", "asdcli", "asdsvc", "ahnlab*", "npkcmsvc", "nprotect*", "astx*",
            // DRM·문서 보안
            "nx*", "fasoo*", "f_*", "dscslink", "markany*",
            // 시스템·셸
            "system", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "svchost", "fontdrvhost", "dwm",
            "sihost", "ctfmon", "conhost", "runtimebroker", "searchhost", "searchindexer", "startmenuexperiencehost", "shellexperiencehost",
            "textinputhost", "explorer", "dllhost", "taskhostw", "audiodg", "spoolsv", "wmiprvse",
        };

        const int QueryTimeoutMs = 300, ScanBudgetMs = 5000, MaxHangsPerProcess = 2, MaxHangsTotal = 6;

        public static bool IsAllowed(string exe) => exe != null && AllowList.Any(a => string.Equals(a.Exe, exe, StringComparison.OrdinalIgnoreCase));

        public static string Label(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return "알 수 없는 프로그램";
            foreach (var a in AllowList) if (string.Equals(a.Exe, exe, StringComparison.OrdinalIgnoreCase)) return a.Label;
            return NoExe(exe);
        }

        static string NoExe(string exe) => exe != null && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe.Substring(0, exe.Length - 4) : exe;

        /// <summary>이름(exe) 이 패턴 하나에 맞는가(.exe·대소문자 무시, 끝 * 는 앞부분 일치).</summary>
        public static bool NameMatches(string exe, string pattern)
        {
            if (string.IsNullOrEmpty(exe) || string.IsNullOrWhiteSpace(pattern)) return false;
            string n = NoExe(exe).ToLowerInvariant(), p = NoExe(pattern.Trim()).ToLowerInvariant();
            return p.EndsWith("*", StringComparison.Ordinal) ? n.StartsWith(p.Substring(0, p.Length - 1), StringComparison.Ordinal) : n == p;
        }

        static bool Any(string exe, IEnumerable<string> patterns) => patterns != null && patterns.Any(p => NameMatches(exe, p));
        public static bool IsCandidate(string exe) => Any(exe, Candidates);
        public static bool IsSkipped(string exe, IEnumerable<string> extraSkip) => Any(exe, Skip) || Any(exe, extraSkip);

        /// <summary>com 하나의 점유자(exclude 의 pid 제외 — 자기·허브 코어).</summary>
        public static List<ComHolder> Find(string com, ICollection<int> exclude, HolderScanMode mode, IEnumerable<string> extraSkip = null)
            => Scan(new[] { com }, exclude, mode, extraSkip).Of(com);

        /// <summary>
        /// 여러 COM 을 한 번에. 후보(알려진 시리얼 도구)만 먼저 열고, mode 가 FullIfNone 이면 후보로 못 찾은 COM 만 같은 세션 전체(후보는 다시 열지 않음)를 본다.
        /// onlyPid &gt; 0 이면 그 프로세스의 핸들만 본다(끝내기 직전 재확인). extraSkip = config.json holder_scan_skip.
        /// </summary>
        public static HolderScan Scan(IEnumerable<string> coms, ICollection<int> exclude, HolderScanMode mode, IEnumerable<string> extraSkip = null, int onlyPid = 0)
        {
            var list = coms.Where(c => !string.IsNullOrEmpty(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var skip = (extraSkip ?? new string[0]).ToList();
            exclude ??= new int[0];
            var sw = Stopwatch.StartNew();
            var res = SystemAccess.IsTest ? FakeScan(list, exclude, onlyPid, x => IsCandidate(x) && !IsSkipped(x, skip))
                                          : RealScan(list, exclude, onlyPid, x => IsCandidate(x) && !IsSkipped(x, skip));
            var rest = list.Where(c => res.Of(c).Count == 0).ToList();
            if (mode == HolderScanMode.FullIfNone && rest.Count > 0 && onlyPid == 0)
            {
                Func<string, bool> others = x => !IsCandidate(x) && !IsSkipped(x, skip);
                var more = SystemAccess.IsTest ? FakeScan(rest, exclude, 0, others) : RealScan(rest, exclude, 0, others);
                foreach (var kv in more.ByCom) res.ByCom[kv.Key] = kv.Value;
                res.Note = res.Note ?? more.Note;
                res.Mode = "후보 → 전체 " + rest.Count + "개";
            }
            res.Ms = sw.ElapsedMilliseconds;
            return res;
        }

        /// <summary>
        /// 그 점유자가 아직 그 COM 을 쥐는가 — 그 한 프로세스만 연다(안내 창 대기·끝낸 뒤 풀림 확인. 전체 검사를 되풀이하지 않는다).
        /// </summary>
        public static bool StillHolds(ComHolder h, ICollection<int> exclude, IEnumerable<string> extraSkip = null)
        {
            if (h == null) return false;
            var skip = (extraSkip ?? new string[0]).ToList();
            var list = new List<string> { h.Com };
            var r = SystemAccess.IsTest ? FakeScan(list, exclude ?? new int[0], h.Pid, x => !IsSkipped(x, skip))
                                        : RealScan(list, exclude ?? new int[0], h.Pid, x => !IsSkipped(x, skip));
            return r.Of(h.Com).Any(x => x.Pid == h.Pid);
        }

        /// <summary>
        /// 끝내기. 허용 목록 밖이면 끝내지 않는다. 시작 시각·이미지 이름·세션·"아직 그 COM 을 쥠"을 다시 본 뒤 같은 핸들로 TerminateProcess.
        /// null = 끝냄(최대 3초 종료 대기), 아니면 끝내지 않은 이유.
        /// </summary>
        public static string Terminate(ComHolder h, ICollection<int> exclude)
        {
            if (h == null) return "점유자 없음";
            if (!IsAllowed(h.Name)) return "허용 목록 밖(" + h.Name + ") — 끝내지 않음";
            if (h.Pid <= 4 || h.Pid == Environment.ProcessId || (exclude != null && exclude.Contains(h.Pid))) return "끝낼 수 없는 pid " + h.Pid;
            return SystemAccess.IsTest ? FakeTerminate(h) : RealTerminate(h, exclude);
        }

        // ================================================================== 시험 모드(C-13)
        static string FakePath => SystemAccess.TestFile("fake_holders.json");

        static List<ComHolder> FakeAll()
        {
            var l = new List<ComHolder>();
            var j = HubFiles.ReadJson(FakePath);
            if (!j.HasValue) return l;
            foreach (var p in j.Value.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var e in p.Value.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.Object)
                        l.Add(new ComHolder { Com = p.Name, Pid = (int)HubFiles.Long(e, "pid"), Name = HubFiles.Str(e, "name"), Parent = HubFiles.Str(e, "parent"), StartTicks = HubFiles.Long(e, "start_ticks") });
            }
            return l;
        }

        static HolderScan FakeScan(List<string> coms, ICollection<int> exclude, int onlyPid, Func<string, bool> open)
        {
            var r = new HolderScan();
            foreach (var h in FakeAll())
            {
                if (!coms.Contains(h.Com, StringComparer.OrdinalIgnoreCase)) continue;
                if (exclude.Contains(h.Pid) || (onlyPid > 0 && h.Pid != onlyPid) || !open(h.Name)) continue;
                if (!r.ByCom.TryGetValue(h.Com, out var l)) r.ByCom[h.Com] = l = new List<ComHolder>();
                l.Add(h);
            }
            return r;
        }

        /// <summary>시험: 같은 COM·pid·이름·시작 시각 항목이 아직 있으면 fake_holders.json 에서 지우고(그 COM 이 풀림) fake_kill.log 에 한 줄. 실제 프로세스는 건드리지 않는다.</summary>
        static string FakeTerminate(ComHolder h)
        {
            string path = FakePath;
            var all = FakeAll();
            var hit = all.FirstOrDefault(x => string.Equals(x.Com, h.Com, StringComparison.OrdinalIgnoreCase) && x.Pid == h.Pid
                                              && string.Equals(x.Name, h.Name, StringComparison.OrdinalIgnoreCase) && x.StartTicks == h.StartTicks);
            if (hit == null) return "(시험) 점유자가 바뀜 — pid " + h.Pid + " " + h.Name + " 이 더 이상 " + h.Com + " 을 쥐지 않음";
            all.Remove(hit);
            var o = new JObj();
            foreach (var g in all.GroupBy(x => x.Com, StringComparer.OrdinalIgnoreCase))
                o.Set(g.Key, g.Select(x => new JObj().Set("pid", (long)x.Pid).Set("name", x.Name).Set("parent", x.Parent).Set("start_ticks", x.StartTicks)));
            SystemAccess.WriteText(path, o.ToString() + "\n");
            SystemAccess.AppendText(SystemAccess.TestFile("fake_kill.log"), h.Pid.ToString(CultureInfo.InvariantCulture) + " " + h.Name + " " + h.Com + "\n");
            return null;
        }

        // ================================================================== 실제(Windows)
        const uint PROCESS_TERMINATE = 0x0001, PROCESS_DUP_HANDLE = 0x0040, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, SYNCHRONIZE = 0x00100000;
        const uint DUPLICATE_SAME_ACCESS = 0x2, FILE_TYPE_CHAR = 2, WAIT_OBJECT_0 = 0;
        const uint FILE_READ_DATA = 0x1, FILE_WRITE_DATA = 0x2, HangProneAccess = 0x001A019F;
        const int SystemExtendedHandleInformation = 0x40, ObjectNameInformation = 1;
        const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
        const int EntrySize = 40, TableHeader = 16, OffPid = 8, OffHandle = 16, OffAccess = 24, OffType = 30;   // SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX(x64)
        const uint TH32CS_SNAPPROCESS = 0x2;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern uint QueryDosDeviceW(string name, char[] buf, int max);
        [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int retLen);
        [DllImport("ntdll.dll")] static extern int NtQueryObject(IntPtr h, int cls, IntPtr buf, int len, out int retLen);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool DuplicateHandle(IntPtr sp, IntPtr s, IntPtr dp, out IntPtr d, uint acc, bool inh, uint opt);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern uint GetFileType(IntPtr h);
        [DllImport("kernel32.dll")] static extern bool ProcessIdToSessionId(uint pid, out uint sid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, char[] buf, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr h, uint code);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref ProcessEntry e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref ProcessEntry e);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ProcessEntry
        {
            public uint Size, Usage, Pid;
            public IntPtr DefaultHeap;
            public uint Module, Threads, ParentPid;
            public int PriClass;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
        }

        /// <summary>COMx 의 장치 이름(\Device\VCP0 등). 없으면 null.</summary>
        static string DeviceOf(string com)
        {
            var buf = new char[1024];
            uint n = QueryDosDeviceW(com, buf, buf.Length);
            if (n == 0) return null;
            string s = new string(buf, 0, (int)n);
            int z = s.IndexOf('\0');
            return z > 0 ? s.Substring(0, z) : s.Length > 0 ? s : null;
        }

        /// <summary>프로세스 이름·부모 pid(Toolhelp 스냅숏 — 프로세스를 열지 않는다).</summary>
        static Dictionary<int, (string Exe, int Parent)> Processes()
        {
            var d = new Dictionary<int, (string, int)>();
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return d;
            try
            {
                var e = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
                for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                {
                    d[(int)e.Pid] = (e.Exe, (int)e.ParentPid);
                    e.Size = (uint)Marshal.SizeOf<ProcessEntry>();
                }
            }
            finally { CloseHandle(snap); }
            return d;
        }

        /// <summary>프로세스 생성 시각(UTC DateTime ticks). 실패하면 0.</summary>
        static long StartTicksOf(IntPtr ph)
        {
            try { return GetProcessTimes(ph, out long c, out _, out _, out _) && c > 0 ? DateTime.FromFileTimeUtc(c).Ticks : 0; }
            catch (ArgumentOutOfRangeException) { return 0; }
        }

        static long StartTicksOf(int pid)
        {
            IntPtr ph = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (ph == IntPtr.Zero) return 0;
            try { return StartTicksOf(ph); } finally { CloseHandle(ph); }
        }

        static string ImageName(IntPtr ph)
        {
            var buf = new char[1024];
            int n = buf.Length;
            return QueryFullProcessImageNameW(ph, 0, buf, ref n) && n > 0 ? Path.GetFileName(new string(buf, 0, n)) : null;
        }

        /// <summary>
        /// 핸들 하나의 이름 질의(GetFileType → FILE_TYPE_CHAR 만 NtQueryObject). 멈출 수 있어 작업 스레드에서 제한 시간으로 돈다.
        /// 요청마다 상태 0(진행) → 1(끝남, 작업 스레드가 바꿈) 또는 2(버림, 부른 쪽이 바꿈) 중 먼저 바꾼 쪽이 이긴다. 버려진 작업 스레드는 막힌 호출이 돌아오면
        /// 그 복제 핸들을 닫고 버퍼·이벤트를 정리한 뒤 끝난다(긴 수명의 트레이 프로세스에 남의 핸들이 쌓이지 않게).
        /// </summary>
        sealed class QueryWorker
        {
            readonly AutoResetEvent go = new AutoResetEvent(false), done = new AutoResetEvent(false);
            readonly IntPtr buf = Marshal.AllocHGlobal(8192);
            IntPtr handle;
            string name;
            int state;
            volatile bool quit;

            public QueryWorker()
            {
                new Thread(Loop) { IsBackground = true, Name = "ComHolderQuery" }.Start();
            }

            void Loop()
            {
                try
                {
                    while (true)
                    {
                        go.WaitOne();
                        if (quit) return;
                        IntPtr h = handle;
                        string n = null;
                        try
                        {
                            if (GetFileType(h) == FILE_TYPE_CHAR && NtQueryObject(h, ObjectNameInformation, buf, 8192, out _) == 0)
                            {
                                int len = (ushort)Marshal.ReadInt16(buf);
                                IntPtr s = Marshal.ReadIntPtr(buf, 8);                   // UNICODE_STRING(x64): Length·MaximumLength·(맞춤)·Buffer
                                if (len > 0 && s != IntPtr.Zero) n = Marshal.PtrToStringUni(s, len / 2);
                            }
                        }
                        catch (Exception) { n = null; }
                        name = n;
                        if (Interlocked.CompareExchange(ref state, 1, 0) == 0) { done.Set(); continue; }
                        CloseHandle(h);                                                  // 부른 쪽이 버림: 이 핸들은 내가 닫는다
                        return;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                    go.Dispose();
                    done.Dispose();
                }
            }

            /// <summary>true = 끝남(name, 핸들은 부른 쪽이 닫는다). false = 제한 시간 초과로 버림(이 작업 스레드가 나중에 핸들을 닫는다 — 다시 쓰지 않는다).</summary>
            public bool TryName(IntPtr h, int ms, out string n)
            {
                handle = h;
                name = null;
                Volatile.Write(ref state, 0);
                go.Set();
                if (!done.WaitOne(ms))
                {
                    if (Interlocked.CompareExchange(ref state, 2, 0) == 0) { n = null; return false; }
                    done.WaitOne();                                                     // 작업 스레드가 막 끝냄(상태 1) — 곧 오는 신호를 받아 비운다
                }
                n = name;
                return true;
            }

            public void Stop() { quit = true; go.Set(); }
        }

        /// <summary>한 번의 핸들 표 검사. open(이미지 이름) 이 true 인 같은 세션 프로세스만 연다. onlyPid &gt; 0 이면 그 프로세스만.</summary>
        static HolderScan RealScan(List<string> coms, ICollection<int> exclude, int onlyPid, Func<string, bool> open)
        {
            var r = new HolderScan();
            var devs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);       // 장치 이름 → COM
            foreach (var c in coms)
            {
                string d = DeviceOf(c);
                if (d != null && !devs.ContainsKey(d)) devs[d] = c;
            }
            if (devs.Count == 0) return r;
            var deadline = Stopwatch.StartNew();
            var procs = Processes();
            uint self = (uint)Environment.ProcessId;
            ProcessIdToSessionId(self, out uint mySess);

            int len = 1 << 20, st;
            IntPtr buf = IntPtr.Zero;
            // File 형식 번호: 자기 exe 파일을 열어 둔 채 핸들 표를 받아 그 핸들의 형식 번호를 본다(임시 파일을 만들지 않는다). 표보다 먼저 열어야 표에 있다.
            using var probe = File.OpenHandle(Environment.ProcessPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long pv = probe.DangerousGetHandle().ToInt64();
            try
            {
                while (true)
                {
                    buf = Marshal.AllocHGlobal(len);
                    st = NtQuerySystemInformation(SystemExtendedHandleInformation, buf, len, out int ret);
                    if (st != STATUS_INFO_LENGTH_MISMATCH) break;
                    Marshal.FreeHGlobal(buf); buf = IntPtr.Zero;
                    len = Math.Max(len * 2, ret + (1 << 16));
                    if (len > (512 << 20)) { r.Note = "핸들 표가 너무 큼"; return r; }
                }
                if (st != 0) { r.Note = "핸들 표 조회 실패 0x" + st.ToString("X8", CultureInfo.InvariantCulture); return r; }
                long count = Marshal.ReadInt64(buf);

                int fileType = -1;
                for (long i = 0; i < count; i++)
                {
                    IntPtr e = buf + TableHeader + (int)(i * EntrySize);
                    if ((uint)Marshal.ReadInt64(e, OffPid) == self && Marshal.ReadInt64(e, OffHandle) == pv) { fileType = (ushort)Marshal.ReadInt16(e, OffType); break; }
                }
                if (fileType < 0) { r.Note = "File 형식 번호를 찾지 못함"; return r; }

                var opened = new Dictionary<uint, IntPtr>();
                var skipped = new HashSet<uint>();
                var hangs = new Dictionary<uint, int>();
                var worker = new QueryWorker();
                int hangsTotal = 0;
                try
                {
                    for (long i = 0; i < count; i++)
                    {
                        if (deadline.ElapsedMilliseconds > ScanBudgetMs) { r.Note = "시간 제한(" + ScanBudgetMs + "ms) — 일부만 봄"; break; }
                        IntPtr e = buf + TableHeader + (int)(i * EntrySize);
                        uint pid = (uint)Marshal.ReadInt64(e, OffPid);
                        if (pid == self || pid <= 4 || skipped.Contains(pid)) continue;
                        if ((ushort)Marshal.ReadInt16(e, OffType) != fileType) continue;
                        if (onlyPid > 0 && pid != (uint)onlyPid) continue;
                        uint acc = (uint)Marshal.ReadInt32(e, OffAccess);
                        if ((acc & (FILE_READ_DATA | FILE_WRITE_DATA)) != (FILE_READ_DATA | FILE_WRITE_DATA) || acc == HangProneAccess) continue;   // COM 은 읽기+쓰기로 연다
                        if (!opened.TryGetValue(pid, out IntPtr ph))
                        {
                            procs.TryGetValue((int)pid, out var pi);
                            if (exclude.Contains((int)pid) || pi.Exe == null || !open(pi.Exe)
                                || !ProcessIdToSessionId(pid, out uint ss) || ss != mySess) { skipped.Add(pid); continue; }
                            ph = OpenProcess(PROCESS_DUP_HANDLE, false, pid);
                            if (ph == IntPtr.Zero) { skipped.Add(pid); continue; }         // 관리자 권한 프로세스 등 — 못 찾을 수 있다(점유자 못 찾음)
                            opened[pid] = ph;
                        }
                        IntPtr hv = (IntPtr)Marshal.ReadInt64(e, OffHandle);
                        if (!DuplicateHandle(ph, hv, GetCurrentProcess(), out IntPtr d, 0, false, DUPLICATE_SAME_ACCESS)) continue;
                        if (!worker.TryName(d, QueryTimeoutMs, out string name))
                        {
                            // 멈춘 질의: 버린 작업 스레드가 d 를 쥐고 있다 — 돌아오면 그 스레드가 닫는다. 같은 프로세스에서 또 멈추면 그 프로세스는 건너뛴다
                            worker = new QueryWorker();
                            hangs[pid] = hangs.TryGetValue(pid, out int hn) ? hn + 1 : 1;
                            if (hangs[pid] >= MaxHangsPerProcess) skipped.Add(pid);
                            if (++hangsTotal >= MaxHangsTotal) { r.Note = "핸들 이름 질의가 " + hangsTotal + "번 멈춤 — 일부만 봄"; break; }
                            continue;
                        }
                        CloseHandle(d);
                        if (name == null || !devs.TryGetValue(name, out string com)) continue;
                        if (!r.ByCom.TryGetValue(com, out var l)) r.ByCom[com] = l = new List<ComHolder>();
                        if (l.Any(x => x.Pid == (int)pid)) continue;
                        procs.TryGetValue((int)pid, out var me);
                        string parent = procs.TryGetValue(me.Parent, out var pp) ? pp.Exe : null;
                        l.Add(new ComHolder { Com = com, Pid = (int)pid, Name = me.Exe, Parent = parent, StartTicks = StartTicksOf((int)pid) });
                    }
                }
                finally
                {
                    worker.Stop();
                    foreach (var h in opened.Values) CloseHandle(h);
                }
                return r;
            }
            finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
        }

        static string RealTerminate(ComHolder h, ICollection<int> exclude)
        {
            IntPtr ph = OpenProcess(PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, false, (uint)h.Pid);
            if (ph == IntPtr.Zero) return "프로세스를 열지 못함(err " + Marshal.GetLastWin32Error() + ") — 이미 끝났거나 권한 없음";
            try
            {
                if (WaitForSingleObject(ph, 0) == WAIT_OBJECT_0) return "이미 끝남";
                long st = StartTicksOf(ph);
                if (st == 0 || h.StartTicks == 0) return "시작 시각을 읽지 못함(권한) — 끝내지 않음";
                if (st != h.StartTicks) return "시작 시각이 다름(pid 재사용) — 끝내지 않음";
                string img = ImageName(ph);
                if (!string.Equals(img, h.Name, StringComparison.OrdinalIgnoreCase)) return "이미지 이름이 다름(" + (img ?? "?") + " ≠ " + h.Name + ") — 끝내지 않음";
                ProcessIdToSessionId((uint)Environment.ProcessId, out uint mySess);
                if (!ProcessIdToSessionId((uint)h.Pid, out uint ss) || ss != mySess) return "다른 세션의 프로세스 — 끝내지 않음";
                // 핸들 ph 를 쥐고 있는 동안 이 pid 는 다른 프로세스에 다시 쓰이지 않는다 → 아래 확인과 끝내기는 같은 프로세스(허용 목록 이름 = 후보)
                var again = RealScan(new List<string> { h.Com }, exclude ?? new int[0], h.Pid, IsAllowed);
                if (!again.Of(h.Com).Any(x => x.Pid == h.Pid)) return "더 이상 " + h.Com + " 을 쥐고 있지 않음" + (again.Note != null ? "(" + again.Note + ")" : "") + " — 끝내지 않음";
                if (!TerminateProcess(ph, 1)) return "TerminateProcess 실패(err " + Marshal.GetLastWin32Error() + ")";
                WaitForSingleObject(ph, 3000);
                return null;
            }
            finally { CloseHandle(ph); }
        }
    }
}
