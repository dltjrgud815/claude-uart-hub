// C-13 단일 접근 계층(계획 §3.3 C-13). 설치기·제거기·감독(C-14)·코어(C-11)가 시스템에 닿는 길은 모두 여기를 거친다:
//   알려진 폴더(LOCALAPPDATA·APPDATA(시작프로그램·시작 메뉴)·바탕화면·USERPROFILE·TEMP·Program Files), mutex 이름,
//   외부 프로세스(wsl.exe·schtasks.exe·설치한 UartHub.exe), WMI 프로세스 조회, 드라이브 판정, 관리자 판정, TCP listen 포트, 창 앞으로 가져오기,
//   설치 계열 파일 조작(쓰기·지우기·옮기기·스트림 복사).
// 환경변수 UART_HUB_TEST_ROOT 가 있으면 시험 모드다. 위 대상이 모두 <root> 아래 가짜로 바뀌고, 시험 루트 밖 경로에 대한 접근이나
// 실제 wsl.exe·schtasks.exe 실행 요청은 TestIsolationViolation 을 던진다(<root>\isolation_violations.log 에도 한 줄 남긴다 — 예외를 누가 삼켜도 시험이 본다).
//   알려진 폴더  <root>\{LocalAppData, AppData, Desktop, UserProfile, Temp, ProgramFiles, ProgramFilesX86}
//   mutex       Local\uart_hub_test_<root 해시 12자>_{sup,core,update,installer}
//   드라이브     <root>\fake_drives.json  {"D":{"type":"Fixed|Removable|Network|CDRom","ready":true}} → 드라이브 루트 = <root>\drive_D
//   wsl.exe     <root>\fake_wsl\l_q.txt(배포판 이름, 줄마다 UTF-8 → 실제처럼 UTF-16LE 로 돌려줌), l_q.delay_ms, l_q.rc. 폴더가 없으면 "WSL 없음"
//               skills 배치(T4): -d <배포판> -e sh -c <skills_wsl.sh> uart_hub probe|write|remove — 배포판 $HOME = <root>\fake_wsl\home\<배포판>(없으면
//               .claude 없는 빈 홈), <배포판>.delay_ms(probe 지연 — 꺼진 배포판 기동 흉내), <배포판>.rc(0 아니면 그 rc 로 실패). skills_wsl.sh 와 같은 규칙으로
//               파일을 쓰고 지우며, 쓴 파일·모드는 <root>\fake_wsl\ops.log 에 "<배포판> write <이름> <모드> <바이트>" 줄로 남긴다.
//   작업 스케줄러 <root>\fake_tasks\<이름>.xml (있으면 작업이 있음, 내용 = /query /xml 출력)
//   프로세스     <root>\fake_procs.json [{"pid":1,"name":"pwsh.exe","cmd":"…"}]
//   포트        <root>\fake_ports.json [{"port":7200,"pid":1,"name":"x"}] (없으면 listen 없음)
//   관리자       <root>\fake_elevated 파일이 있으면 '관리자 권한으로 실행'(내용 "uac_off" 면 UAC 꺼진 관리자 계정)
//   터미널(T3)   App Paths <root>\fake_app_paths.json {"ttermpro.exe":"<경로>"} · PATH <root>\fake_path.txt(폴더 한 줄씩) ·
//               프로세스 fake_procs.json 의 "title"(창 제목)·"fg":false(앞으로 가져오기 거부). 터미널 실행·창 앞으로·창 제목·안내 창·SHChangeNotify 는
//               시험 모드에서 아무것도 띄우지 않고 "[ext:fake] …" 줄만 남긴다(실행 기록은 <root>\fake_launch.log 에도).
//   OneDrive     <root>\fake_onedrive 파일이 있으면 아이콘 폴더를 OneDrive 동기화 폴더로 본다(경로에 'OneDrive*' 조각이 있어도 같다).
//   실패 주입    UART_HUB_TEST_FAIL=<지점,…> (TestFail), 지연 주입 UART_HUB_TEST_DELAY=<지점:ms,…> (TestDelay) — 둘 다 시험 모드에서만 읽는다.
// 실제 모드에서는 경로 가드가 아무것도 하지 않는다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UartHub
{
    /// <summary>시험 모드에서 시험 루트 밖의 실제 시스템에 닿으려 했다(C-13). 시나리오는 그 자리에서 FAIL.</summary>
    public sealed class TestIsolationViolation : Exception
    {
        public TestIsolationViolation(string m) : base("TestIsolationViolation: " + m) { }
    }

    /// <summary>프로세스 한 줄. Title·FgDeny 는 터미널 창 찾기(T3)용: Title = 주 창 제목, FgDeny = 시험 주입(앞으로 가져오기 거부).</summary>
    public sealed class ProcRow { public int Pid; public string Name, CommandLine, Title; public bool FgDeny; }

    public sealed class DriveState
    {
        public string Letter, Type, Root;
        public bool Exists, Ready;
    }

    public sealed class PortOwner { public int Port, Pid; public string Name; }

    /// <summary>외부 프로세스 결과. Out 은 바이트(wsl -l 은 UTF-16LE). Code -1 = 실행 못 함·시간 초과.</summary>
    public sealed class ExtResult
    {
        public int Code = -1;
        public byte[] Out = new byte[0];
        public string Err = "";
        public bool TimedOut, Fake;
        public long Ms;
        public string OutText => DecodeAuto(Out);

        /// <summary>UTF-16LE(BOM 이나 0 바이트가 보이면) 또는 UTF-8.</summary>
        public static string DecodeAuto(byte[] b)
        {
            if (b == null || b.Length == 0) return "";
            bool utf16 = (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) || (b.Length >= 2 && b[1] == 0 && b[0] != 0);
            string s = utf16 ? Encoding.Unicode.GetString(b) : new UTF8Encoding(false).GetString(b);
            return s.TrimStart('﻿').Replace("\0", "");
        }
    }

    public static class SystemAccess
    {
        /// <summary>시험 루트(UART_HUB_TEST_ROOT, 전체 경로·끝 \ 없음). 실제 모드면 null.</summary>
        public static readonly string TestRoot = InitRoot();
        public static bool IsTest => TestRoot != null;

        /// <summary>외부 호출 기록(설치기가 install.log 로 받는다). 실제 호출은 "[ext] …", 가짜는 "[ext:fake] …".</summary>
        public static Action<string> ExternalLog;

        static string InitRoot()
        {
            string r = Environment.GetEnvironmentVariable("UART_HUB_TEST_ROOT");
            if (string.IsNullOrWhiteSpace(r)) return null;
            return Path.GetFullPath(r.Trim()).TrimEnd('\\', '/');
        }

        static string T(params string[] sub) => Path.Combine(new[] { TestRoot }.Concat(sub).ToArray());
        static string Sf(Environment.SpecialFolder f) => Environment.GetFolderPath(f, Environment.SpecialFolderOption.DoNotVerify);

        // ================================================================== 알려진 폴더
        public static string LocalAppData => IsTest ? T("LocalAppData") : Sf(Environment.SpecialFolder.LocalApplicationData);
        public static string AppData => IsTest ? T("AppData") : Sf(Environment.SpecialFolder.ApplicationData);
        public static string StartMenuPrograms => IsTest ? Path.Combine(AppData, "Microsoft", "Windows", "Start Menu", "Programs") : Sf(Environment.SpecialFolder.Programs);
        public static string Startup => IsTest ? Path.Combine(StartMenuPrograms, "Startup") : Sf(Environment.SpecialFolder.Startup);
        public static string Desktop => IsTest ? T("Desktop") : Sf(Environment.SpecialFolder.DesktopDirectory);
        public static string UserProfile => IsTest ? T("UserProfile") : Sf(Environment.SpecialFolder.UserProfile);
        public static string Temp => IsTest ? T("Temp") : Path.GetTempPath().TrimEnd('\\');
        public static string ProgramFiles => IsTest ? T("ProgramFiles") : Sf(Environment.SpecialFolder.ProgramFiles);
        public static string ProgramFilesX86 => IsTest ? T("ProgramFilesX86") : Sf(Environment.SpecialFolder.ProgramFilesX86);

        /// <summary>설치 폴더 %LOCALAPPDATA%\uart_hub.</summary>
        public static string InstallDir => Path.Combine(LocalAppData, "uart_hub");
        public static string InstalledExe => Path.Combine(InstallDir, "UartHub.exe");

        /// <summary>시험 루트 안 파일(실제 모드면 null).</summary>
        public static string TestFile(string name) => IsTest ? Path.Combine(TestRoot, name) : null;

        // ================================================================== mutex 이름(C-8)
        /// <summary>kind = sup | core | update | installer.</summary>
        public static string MutexName(string kind) => IsTest ? MutexNameFor(TestRoot, kind) : @"Local\uart_hub_" + kind;

        public static string MutexNameFor(string root, string kind) => @"Local\uart_hub_test_" + RootHash(root) + "_" + kind;

        public static string RootHash(string root)
        {
            string n = Path.GetFullPath(root).TrimEnd('\\', '/').ToLowerInvariant();
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(n))).Substring(0, 12).ToLowerInvariant();
        }

        // ================================================================== 시험 주입
        /// <summary>UART_HUB_TEST_FAIL 의 지점 목록(쉼표)에 point 가 있는가. 시험 모드에서만.</summary>
        public static bool TestFail(string point)
        {
            if (!IsTest) return false;
            string v = Environment.GetEnvironmentVariable("UART_HUB_TEST_FAIL") ?? "";
            return v.Split(',').Any(x => string.Equals(x.Trim(), point, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// UART_HUB_TEST_DELAY="지점:ms[,지점:ms]" 에 point 가 있으면 그만큼 멈춘다(시험 모드에서만). 멈춘 ms(없으면 0).
        /// 지점: finalize(설치 7·업데이트 5 단계 앞) · pre-replace(업데이트 교체 직전) · after-1177(1177 상태 직후) · sup-pre-mutex(감독 mutex 획득 직전).
        /// </summary>
        public static int TestDelay(string point)
        {
            if (!IsTest) return 0;
            foreach (var part in (Environment.GetEnvironmentVariable("UART_HUB_TEST_DELAY") ?? "").Split(','))
            {
                var kv = part.Split(':');
                if (kv.Length != 2 || !string.Equals(kv[0].Trim(), point, StringComparison.OrdinalIgnoreCase)) continue;
                if (!int.TryParse(kv[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms) || ms <= 0) return 0;
                Ext(L.T("[test] 지연 " + point + " " + ms + "ms", "[test] delay " + point + " " + ms + "ms"));
                Thread.Sleep(ms);
                return ms;
            }
            return 0;
        }

        /// <summary>시험 모드 숫자 환경변수(쉼표 목록의 index 번째). 실제 모드나 형식 오류면 dflt.</summary>
        public static double TestNumber(string env, int index, double dflt)
        {
            if (!IsTest) return dflt;
            var parts = (Environment.GetEnvironmentVariable(env) ?? "").Split(',');
            return index < parts.Length && double.TryParse(parts[index].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : dflt;
        }

        // ================================================================== 경로 가드
        static int violations;
        public static int Violations => violations;

        public static TestIsolationViolation Violation(string what)
        {
            Interlocked.Increment(ref violations);
            var ex = new TestIsolationViolation(what);
            try
            {
                File.AppendAllText(Path.Combine(TestRoot, "isolation_violations.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " pid=" + Environment.ProcessId + " " + what + "\n",
                    new UTF8Encoding(false));
            }
            catch (Exception) { }
            return ex;
        }

        public static bool Under(string path, string dir)
        {
            string p = Path.GetFullPath(path).TrimEnd('\\'), d = Path.GetFullPath(dir).TrimEnd('\\');
            return string.Equals(p, d, StringComparison.OrdinalIgnoreCase) || p.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 같은 파일·폴더인가. 전체 경로 글자가 같으면 같다. 다르면 둘 다 있을 때 파일 식별자(볼륨 일련번호 + 파일 인덱스)로 한 번 더 본다 —
        /// 8.3 짧은 이름·subst 드라이브·다른 철자로 같은 exe 를 "다른 exe" 로 보고 허브를 괜히 바꾸지 않게(X7).
        /// </summary>
        public static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                string fa = Path.GetFullPath(a).TrimEnd('\\'), fb = Path.GetFullPath(b).TrimEnd('\\');
                if (string.Equals(fa, fb, StringComparison.OrdinalIgnoreCase)) return true;
                var ia = FileId(fa);
                return ia != null && ia == FileId(fb);
            }
            catch (Exception) { return false; }
        }

        /// <summary>"볼륨:인덱스"(읽기 권한 없이 연 핸들의 GetFileInformationByHandle). 없거나 못 열면 null.</summary>
        static string FileId(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return null;
            IntPtr h = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);   // FILE_SHARE_READ|WRITE|DELETE, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS
            if (h == new IntPtr(-1)) return null;
            try
            {
                return GetFileInformationByHandle(h, out var fi)
                    ? fi.VolumeSerial.ToString("x8", CultureInfo.InvariantCulture) + ":" + fi.IndexHigh.ToString("x8", CultureInfo.InvariantCulture) + fi.IndexLow.ToString("x8", CultureInfo.InvariantCulture)
                    : null;
            }
            finally { CloseHandle(h); }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ByHandleFileInfo
        {
            public uint Attributes;
            public long Creation, LastAccess, LastWrite;
            public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetFileInformationByHandle(IntPtr h, out ByHandleFileInfo fi);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);

        /// <summary>
        /// exe 가 놓인 곳의 문제(X7 setup 위치 검사). 로컬 고정 디스크면 null, 아니면 이유. UNC(\\wsl.localhost\… 포함)·네트워크·이동식·CD 는 막는다
        /// (휴대용은 그 자리의 exe 를 매번 실행 — 꺼지거나 빠지는 위치면 허브·skills·아이콘이 깨진다). 시험 모드: fake_drives.json 에 있는 문자만 그 형식, 없으면 고정 디스크.
        /// </summary>
        public static string ExeLocationProblem(string path)
        {
            if (string.IsNullOrEmpty(path)) return L.T("exe 경로를 모름", "exe path unknown");
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return L.T("네트워크·UNC 경로(" + path + ")", "network or UNC path (" + path + ")");
            if (path.Length < 3 || path[1] != ':') return L.T("드라이브 문자가 없는 경로(" + path + ")", "path without a drive letter (" + path + ")");
            var d = Drive(path.Substring(0, 1));
            if (IsTest && !d.Exists) return null;
            return d.Type == "Fixed" ? null
                 : d.Type == "Removable" ? L.T("이동식 디스크(" + d.Letter + ":)", "removable disk (" + d.Letter + ":)")
                 : d.Type == "Network" ? L.T("네트워크 드라이브(" + d.Letter + ":)", "network drive (" + d.Letter + ":)")
                 : d.Type == "CDRom" ? L.T("CD·DVD(" + d.Letter + ":)", "CD/DVD (" + d.Letter + ":)")
                 : L.T("로컬 고정 디스크가 아님(" + d.Letter + ": " + d.Type + ")", "not a local fixed disk (" + d.Letter + ": " + d.Type + ")");
        }

        /// <summary>
        /// 시험 모드: path 가 시험 루트 안이어야 한다(읽기는 자기 이미지도 허용 — 설치기가 받은 exe 를 복사한다). 밖이면 TestIsolationViolation.
        /// 실제 모드: 아무것도 하지 않는다. 전체 경로를 돌려준다.
        /// </summary>
        public static string Guard(string path, string op)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException(L.T("경로 없음(" + op + ")", "no path (" + op + ")"));
            string full = Path.GetFullPath(path);
            if (!IsTest) return full;
            if (Under(full, TestRoot)) return full;
            if (op == "read" && SamePath(full, Environment.ProcessPath)) return full;
            throw Violation(op + " " + full);
        }

        // ================================================================== 파일(가드 포함)
        public static bool FileExists(string p) => File.Exists(Guard(p, "read"));
        public static bool DirExists(string p) => Directory.Exists(Guard(p, "read"));

        public static string ReadText(string p)
        {
            string f = Guard(p, "read");
            return File.Exists(f) ? HubFiles.ReadShared(f) : null;
        }

        public static void CreateDir(string p) => Directory.CreateDirectory(Guard(p, "write"));

        /// <summary>원자 교체 쓰기(UTF-8 BOM 없음).</summary>
        public static void WriteText(string p, string text) => AtomicFile.WriteAllText(Guard(p, "write"), text);

        public static void AppendText(string p, string text)
        {
            string f = Guard(p, "write");
            using var fs = new FileStream(f, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            var b = new UTF8Encoding(false).GetBytes(text);
            fs.Write(b, 0, b.Length);
        }

        public static void DeleteFile(string p) { string f = Guard(p, "delete"); if (File.Exists(f)) File.Delete(f); }

        public static void DeleteDir(string p) { string f = Guard(p, "delete"); if (Directory.Exists(f)) Directory.Delete(f, true); }

        public static void MoveFile(string a, string b, bool overwrite) => File.Move(Guard(a, "move"), Guard(b, "write"), overwrite);

        public static void CopyFile(string a, string b, bool overwrite) => File.Copy(Guard(a, "read"), Guard(b, "write"), overwrite);

        /// <summary>
        /// 같은 볼륨 안 이름 바꾸기만(부록 C.11 로그 폴더 옮기기): MoveFileExW 를 MOVEFILE_COPY_ALLOWED 없이(File.Move 는 다른 볼륨이면 복사 뒤 지운다).
        /// 대상이 있거나 다른 볼륨이면 IOException(HResult 하위 16비트 = Win32 오류, 17 = 다른 드라이브). 폴더도 된다.
        /// </summary>
        public static void Rename(string a, string b)
        {
            string s = Guard(a, "move"), d = Guard(b, "write");
            if (!MoveFileExW(s, d, 0)) throw new IOException(L.T("MoveFileEx 실패(Win32 " + Marshal.GetLastWin32Error() + ")", "MoveFileEx failed (Win32 " + Marshal.GetLastWin32Error() + ")"), unchecked((int)0x80070000) | Marshal.GetLastWin32Error());
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool MoveFileExW(string from, string to, uint flags);

        /// <summary>빈 폴더만 지운다(안에 무엇이 있으면 그대로 두고 false).</summary>
        public static bool DeleteEmptyDir(string p)
        {
            string f = Guard(p, "delete");
            if (!Directory.Exists(f) || Directory.EnumerateFileSystemEntries(f).Any()) return false;
            Directory.Delete(f, false);
            return true;
        }

        /// <summary>ReplaceFile(K8a): dst 를 src 로 바꾸고 원래 dst 는 backup 이름으로 남긴다(실행 중인 이미지도 됨). 실패하면 IOException(HResult 하위 16비트 = Win32 오류, 1176·1177).</summary>
        public static void ReplaceFile(string src, string dst, string backup) => File.Replace(Guard(src, "move"), Guard(dst, "write"), Guard(backup, "write"), true);

        /// <summary>바이트 스트림 복사로 새 파일을 만든다(대체 데이터 스트림 = MotW 가 따라오지 않는다, K8b 기본).</summary>
        public static void CopyStream(string src, string dst)
        {
            string s = Guard(src, "read"), d = Guard(dst, "write");
            using var i = new FileStream(s, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var o = new FileStream(d, FileMode.Create, FileAccess.Write, FileShare.None);
            i.CopyTo(o, 1 << 20);
            o.Flush(true);
        }

        public static string[] ListFiles(string dir, string pattern)
        {
            string d = Guard(dir, "read");
            return Directory.Exists(d) ? Directory.GetFiles(d, pattern) : new string[0];
        }

        public static void SetCurrentDirectory(string dir) => Directory.SetCurrentDirectory(Guard(dir, "read"));

        public static string[] ListDirs(string dir)
        {
            string d = Guard(dir, "read");
            return Directory.Exists(d) ? Directory.GetDirectories(d) : new string[0];
        }

        /// <summary>
        /// MotW(인터넷에서 받은 표시 = 대체 데이터 스트림 Zone.Identifier)의 ZoneId(X7 setup 보고). 스트림이 없으면 null, 있는데 ZoneId 줄이 없으면 "?",
        /// 확인할 수 없으면(FAT·SMB·9P 처럼 대체 데이터 스트림이 없는 파일 시스템, 권한) "unknown".
        /// 읽기만 한다 — 지우지 않는다(exe 가 스스로 MotW 를 지우면 방어 회피로 보일 수 있음. 사용자가 파일 속성 → '차단 해제').
        /// </summary>
        public static string ZoneId(string path)
        {
            string f = Guard(path, "read");
            try
            {
                using var fs = new FileStream(f + ":Zone.Identifier", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(fs);
                foreach (var l in r.ReadToEnd().Replace("\r", "").Split('\n'))
                    if (l.StartsWith("ZoneId=", StringComparison.OrdinalIgnoreCase)) return l.Substring(7).Trim();
                return "?";
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (IOException) { return "unknown"; }
            catch (UnauthorizedAccessException) { return "unknown"; }
            catch (NotSupportedException) { return "unknown"; }
        }

        // ================================================================== 외부 프로세스
        static void Ext(string line) { try { ExternalLog?.Invoke(line); } catch (Exception) { } }

        static string ArgText(IEnumerable<string> args)
            => string.Join(" ", args.Select(a => a.Contains('\n') || a.Length > 200 ? L.T("<스크립트 " + a.Length + "자>", "<script " + a.Length + " chars>") : a.Length == 0 || a.Contains(' ') ? "\"" + a + "\"" : a));

        /// <summary>wsl.exe 실행. 표준 입력은 stdin 바이트(null 이면 바로 닫음), cwd 는 TEMP(UNC 경고 회피, K7), 제한 시간이 지나면 kill.</summary>
        public static ExtResult Wsl(string[] args, byte[] stdin, int timeoutMs)
        {
            if (IsTest) return FakeWsl(args, stdin, timeoutMs);
            var r = Capture(Path.Combine(Environment.SystemDirectory, "wsl.exe"), args, stdin, timeoutMs, Temp);
            Ext("[ext] wsl.exe " + ArgText(args) + " → rc=" + r.Code + (r.TimedOut ? L.T(" (시간 초과)", " (timed out)") : "") + " " + r.Ms + "ms");
            return r;
        }

        public static ExtResult Schtasks(string[] args, int timeoutMs)
        {
            if (IsTest) return FakeSchtasks(args);
            var r = Capture(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args, null, timeoutMs, Temp);
            Ext("[ext] schtasks.exe " + ArgText(args) + " → rc=" + r.Code + (r.TimedOut ? L.T(" (시간 초과)", " (timed out)") : "") + " " + r.Ms + "ms");
            return r;
        }

        /// <summary>설치한(또는 복사한) UartHub.exe 를 실행해 출력을 받는다. 시험 모드에서는 exe 가 시험 루트 안이어야 한다.</summary>
        public static ExtResult RunCapture(string exe, string[] args, int timeoutMs)
        {
            string e = Guard(exe, "exec");
            var r = Capture(e, args, null, timeoutMs, Path.GetDirectoryName(e));
            Ext("[run] " + Path.GetFileName(e) + " " + ArgText(args) + " → rc=" + r.Code + (r.TimedOut ? L.T(" (시간 초과)", " (timed out)") : "") + " " + r.Ms + "ms");
            return r;
        }

        /// <summary>
        /// 분리 기동(창 없음, 핸들 상속 없음). PID 와 시작 시각.
        /// X7: 부르는 쪽이 작업 개체(Job — Claude Code·터미널이 셸을 묶는 것)에 들어 있으면 그 작업이 닫힐 때 자식도 같이 죽는다 →
        /// CreateProcess 에 CREATE_BREAKAWAY_FROM_JOB 을 주어 작업 밖에서 띄운다. 그 작업이 이탈을 허락하지 않으면(접근 거부) 지금처럼 Process.Start.
        /// dropTestInjection: 자식 환경에서 UART_HUB_TEST_FAIL·UART_HUB_TEST_DELAY 를 뺀다(되돌림이 띄우는 옛 감독이 주입을 물려받지 않게).
        /// dropEnv: 그 밖에 뺄 환경변수(예: 설치기가 띄우는 운영 감독은 UART_HUB_DIR 을 물려받지 않는다).
        /// </summary>
        public static (int Pid, long StartTicks) StartDetached(string exe, string[] args, bool dropTestInjection = false, string[] dropEnv = null)
        {
            string e = Guard(exe, "exec");
            var drop = new List<string>(dropEnv ?? new string[0]);
            if (dropTestInjection) drop.AddRange(new[] { "UART_HUB_TEST_FAIL", "UART_HUB_TEST_DELAY" });
            int pid = Breakaway(e, args, drop, out long st, out string how);
            if (pid <= 0)
            {
                var psi = new ProcessStartInfo(e) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(e) };
                foreach (var a in args) psi.ArgumentList.Add(a);
                foreach (var k in drop) psi.Environment.Remove(k);
                using var p0 = Process.Start(psi);
                pid = p0.Id;
                try { st = HubFiles.StartTicks(p0); } catch (Exception) { }
            }
            Ext("[run] " + Path.GetFileName(e) + " " + ArgText(args) + " → pid=" + pid + L.T(" (분리 기동" + (how != null ? ", " + how : "") + ")", " (detached start" + (how != null ? ", " + how : "") + ")"));
            return (pid, st);
        }

        /// <summary>CREATE_BREAKAWAY_FROM_JOB|CREATE_NO_WINDOW|CREATE_UNICODE_ENVIRONMENT, 핸들 상속 없음. 성공하면 pid·시작 시각(how = null), 못 하면 0(how = 이유).</summary>
        static int Breakaway(string exe, string[] args, List<string> drop, out long startTicks, out string how)
        {
            how = null;
            startTicks = 0;
            var cmd = new StringBuilder(QuoteArg(exe));
            foreach (var a in args) cmd.Append(' ').Append(QuoteArg(a));
            IntPtr env = IntPtr.Zero;
            try
            {
                if (drop.Count > 0)
                {
                    var sb = new StringBuilder();
                    foreach (System.Collections.DictionaryEntry kv in Environment.GetEnvironmentVariables())
                    {
                        string k = (string)kv.Key;
                        if (drop.Any(x => string.Equals(x, k, StringComparison.OrdinalIgnoreCase))) continue;
                        sb.Append(k).Append('=').Append((string)kv.Value).Append('\0');
                    }
                    sb.Append('\0');
                    env = Marshal.StringToHGlobalUni(sb.ToString());
                }
                var si = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
                const uint flags = 0x01000000 | 0x08000000 | 0x00000400;     // CREATE_BREAKAWAY_FROM_JOB | CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT
                if (!CreateProcessW(exe, cmd, IntPtr.Zero, IntPtr.Zero, false, flags, env, Path.GetDirectoryName(exe), ref si, out var pi))
                {
                    int err = Marshal.GetLastWin32Error();
                    how = L.T("작업 이탈 못 함(Win32 " + err + ") → 일반 기동", "could not break away from job (Win32 " + err + ") → normal start");
                    return 0;
                }
                CloseHandle(pi.hThread);
                try { using var p = Process.GetProcessById(pi.dwProcessId); startTicks = HubFiles.StartTicks(p); }     // 핸들을 쥔 동안(PID 재사용 없음)
                catch (Exception) { }
                CloseHandle(pi.hProcess);
                return pi.dwProcessId;
            }
            catch (Exception x) { how = L.T("작업 이탈 못 함(" + x.GetType().Name + ") → 일반 기동", "could not break away from job (" + x.GetType().Name + ") → normal start"); return 0; }
            finally { if (env != IntPtr.Zero) Marshal.FreeHGlobal(env); }
        }

        /// <summary>CommandLineToArgvW 규칙의 인자 하나(빈 문자열·공백·따옴표만 감싸고, 따옴표 앞·끝의 역슬래시는 두 배).</summary>
        static string QuoteArg(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            for (int i = 0; i < a.Length; i++)
            {
                int bs = 0;
                while (i < a.Length && a[i] == '\\') { bs++; i++; }
                if (i == a.Length) { sb.Append('\\', bs * 2); break; }
                if (a[i] == '"') sb.Append('\\', bs * 2 + 1).Append('"');
                else sb.Append('\\', bs).Append(a[i]);
            }
            return sb.Append('"').ToString();
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct StartupInfo
        {
            public int cb;
            public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string cwd, ref StartupInfo si, out ProcessInformation pi);

        static ExtResult Capture(string exe, string[] args, byte[] stdin, int timeoutMs, string cwd)
        {
            var r = new ExtResult();
            var sw = Stopwatch.StartNew();
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            if (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd)) psi.WorkingDirectory = cwd;
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process p;
            try { p = Process.Start(psi); }
            catch (Exception ex) { r.Err = ex.Message; r.Ms = sw.ElapsedMilliseconds; return r; }
            using (p)
            {
                var ms = new MemoryStream();
                var outTask = p.StandardOutput.BaseStream.CopyToAsync(ms);
                var errTask = p.StandardError.ReadToEndAsync();
                try
                {
                    // 바이트 스트림으로 쓴다(pwsh 처럼 줄 끝을 바꾸지 않음, K7 주의 ①)
                    if (stdin != null && stdin.Length > 0) { p.StandardInput.BaseStream.Write(stdin, 0, stdin.Length); p.StandardInput.BaseStream.Flush(); }
                    p.StandardInput.Close();
                }
                catch (Exception) { }
                if (!p.WaitForExit(timeoutMs))
                {
                    r.TimedOut = true;
                    try { p.Kill(true); } catch (Exception) { }
                    try { p.WaitForExit(3000); } catch (Exception) { }
                }
                try { Task.WaitAll(new Task[] { outTask, errTask }, 3000); } catch (Exception) { }
                r.Out = ms.ToArray();
                r.Err = errTask.IsCompletedSuccessfully ? errTask.Result : "";
                r.Code = r.TimedOut ? -1 : SafeExit(p);
            }
            r.Ms = sw.ElapsedMilliseconds;
            return r;
        }

        static int SafeExit(Process p) { try { return p.ExitCode; } catch (Exception) { return -1; } }

        static ExtResult FakeWsl(string[] args, byte[] stdin, int timeoutMs)
        {
            var r = new ExtResult { Fake = true };
            var sw = Stopwatch.StartNew();
            string dir = T("fake_wsl");
            string a = string.Join(" ", args);
            if (a == "-l --running -q")
            {
                // X7 setup 계획: 켜진 배포판만(l_running.txt — 없으면 l_q.txt 와 같음 = 모두 켜짐)
                string f = Path.Combine(dir, "l_running.txt");
                if (!Directory.Exists(dir)) { r.Code = -1; r.Err = L.T("fake: WSL 없음(fake_wsl 폴더 없음)", "fake: no WSL (no fake_wsl folder)"); }
                else
                {
                    if (!File.Exists(f)) f = Path.Combine(dir, "l_q.txt");
                    string names = File.Exists(f) ? File.ReadAllText(f) : "";
                    r.Code = 0;
                    r.Out = Encoding.Unicode.GetBytes(string.Join("\r\n", names.Replace("\r", "").Split('\n')));
                }
            }
            else if (a == "-l -q" || a == "--list --quiet")
            {
                if (!Directory.Exists(dir)) { r.Code = -1; r.Err = L.T("fake: WSL 없음(fake_wsl 폴더 없음)", "fake: no WSL (no fake_wsl folder)"); }
                else
                {
                    int delay = ReadInt(Path.Combine(dir, "l_q.delay_ms"), 0);
                    if (delay > 0) Thread.Sleep(Math.Min(delay, timeoutMs));
                    if (delay >= timeoutMs) { r.TimedOut = true; r.Code = -1; }
                    else
                    {
                        r.Code = ReadInt(Path.Combine(dir, "l_q.rc"), 0);
                        string names = File.Exists(Path.Combine(dir, "l_q.txt")) ? File.ReadAllText(Path.Combine(dir, "l_q.txt")) : "";
                        r.Out = Encoding.Unicode.GetBytes(string.Join("\r\n", names.Replace("\r", "").Split('\n')));
                    }
                }
            }
            else if (args.Length >= 8 && args[0] == "-d" && args[2] == "-e" && args[3] == "sh" && args[4] == "-c" && args[6] == "uart_hub")
                FakeDistro(r, dir, args[1], args[7], args.Length > 8 ? args[8] : null, stdin, timeoutMs);
            else { r.Code = 1; r.Err = L.T("fake: 응답 파일 없음(" + a + ")", "fake: no reply file (" + a + ")"); }
            r.Ms = sw.ElapsedMilliseconds;
            Ext("[ext:fake] wsl.exe " + ArgText(args) + " → rc=" + r.Code + (r.TimedOut ? L.T(" (시간 초과)", " (timed out)") : "") + " " + r.Ms + "ms");
            return r;
        }

        /// <summary>가짜 배포판 하나에서 skills_wsl.sh 의 probe·write·remove 를 같은 규칙으로 흉내 낸다(출력 key=value 줄, 종료코드 같음).</summary>
        static void FakeDistro(ExtResult r, string dir, string distro, string op, string arg, byte[] stdin, int timeoutMs)
        {
            if (!Directory.Exists(dir)) { r.Code = -1; r.Err = L.T("fake: WSL 없음(fake_wsl 폴더 없음)", "fake: no WSL (no fake_wsl folder)"); return; }
            if (distro.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || distro.StartsWith(".", StringComparison.Ordinal)) { r.Code = 1; r.Err = L.T("fake: 배포판 이름 오류", "fake: bad distro name"); return; }
            if (op == "probe")
            {
                int delay = ReadInt(Path.Combine(dir, distro + ".delay_ms"), 0);
                if (delay > 0) Thread.Sleep(Math.Min(delay, timeoutMs));
                if (delay >= timeoutMs) { r.TimedOut = true; r.Code = -1; return; }
            }
            int rc = ReadInt(Path.Combine(dir, distro + ".rc"), 0);
            if (rc != 0) { r.Code = rc; r.Err = L.T("fake: 배포판 " + distro + " 실행 실패", "fake: distro " + distro + " failed to run"); return; }
            string home = Path.Combine(dir, "home", distro), claude = Path.Combine(home, ".claude"), d = Path.Combine(claude, "skills", "uart"), m = Path.Combine(d, ".uart_hub");
            var o = new StringBuilder();
            var u8 = new UTF8Encoding(false);
            bool exists = Directory.Exists(d) || File.Exists(d);
            switch (op)
            {
                case "probe":
                    o.Append("home=/home/fake\n").Append("claude=").Append(Directory.Exists(claude) ? 1 : 0).Append('\n').Append("dir=").Append(exists ? 1 : 0).Append('\n');
                    if (File.Exists(m)) o.Append("marker=").Append(File.ReadAllText(m).Replace("\r", "").Replace("\n", "")).Append('\n');
                    r.Code = 0;
                    break;
                case "write":
                {
                    if (!Directory.Exists(claude)) { o.Append("err=no_claude\n"); r.Code = 3; break; }
                    if (exists && !File.Exists(m)) { o.Append("err=foreign\n"); r.Code = 4; break; }
                    bool had = File.Exists(m);
                    var files = new List<(string Name, string Mode, byte[] Data)>();
                    string text = stdin == null ? "" : u8.GetString(stdin);
                    foreach (var line in text.Split('\n'))
                    {
                        if (line.Length == 0) continue;
                        if (line.Contains('\r')) { o.Append("err=base64\n"); r.Code = 7; break; }     // 실제 base64 -d 는 CR 을 거부한다
                        var f = line.Split(' ');
                        if (f.Length != 3 || !(new[] { "SKILL.md", "uart", ".uart_hub" }.Contains(f[0]) || FakeExtraNames.Contains(f[0]))) { o.Append("err=name ").Append(f[0]).Append('\n'); r.Code = 6; break; }
                        if (f[1] != "644" && f[1] != "755") { o.Append("err=mode ").Append(f[1]).Append('\n'); r.Code = 6; break; }
                        try { files.Add((f[0], f[1], Convert.FromBase64String(f[2]))); }
                        catch (FormatException) { o.Append("err=base64\n"); r.Code = 7; break; }
                    }
                    if (r.Code > 0) break;
                    var main = files.Where(x => !x.Name.Contains('/')).ToList();
                    if (main.Count != 3) { o.Append("err=count ").Append(main.Count).Append('\n'); r.Code = 6; break; }
                    Directory.CreateDirectory(d);
                    foreach (var f in main.OrderBy(x => x.Name == ".uart_hub" ? 0 : 1))
                    {
                        File.WriteAllBytes(Path.Combine(d, f.Name), f.Data);
                        File.AppendAllText(Path.Combine(dir, "ops.log"), distro + " write " + f.Name + " " + f.Mode + " " + f.Data.Length + "\n", u8);
                    }
                    // X5 extras(skills_wsl.sh 와 같은 규칙): 표시 없이 있는 폴더는 그 폴더만 건너뜀, 두 파일이 다 와야 넣음
                    var ex = new List<string>();
                    foreach (var e in SkillsDeployer.Extras)
                    {
                        string xd = Path.Combine(claude, "skills", e), xm = Path.Combine(xd, ".uart_hub");
                        var mine = files.Where(x => x.Name.StartsWith(e + "/", StringComparison.Ordinal)).ToList();
                        if ((Directory.Exists(xd) || File.Exists(xd)) && !File.Exists(xm)) { ex.Add(e + ":skipped"); continue; }
                        if (mine.Count(x => x.Name == e + "/SKILL.md" || x.Name == e + "/.uart_hub") != 2) continue;
                        bool hx = File.Exists(xm);
                        Directory.CreateDirectory(xd);
                        foreach (var f in mine.OrderBy(x => x.Name.EndsWith("/.uart_hub", StringComparison.Ordinal) ? 0 : 1))
                        {
                            File.WriteAllBytes(Path.Combine(xd, f.Name.Substring(e.Length + 1)), f.Data);
                            File.AppendAllText(Path.Combine(dir, "ops.log"), distro + " write " + f.Name + " " + f.Mode + " " + f.Data.Length + "\n", u8);
                        }
                        ex.Add(e + ":" + (hx ? "updated" : "installed"));
                    }
                    o.Append(had ? "ok=updated\n" : "ok=installed\n").Append("path=/home/fake/.claude/skills/uart\n");
                    if (ex.Count > 0) o.Append("extras=").Append(string.Join(",", ex)).Append('\n');
                    r.Code = 0;
                    break;
                }
                case "remove":
                {
                    if (string.IsNullOrEmpty(arg)) { o.Append("err=no_id\n"); r.Code = 2; break; }
                    bool mainMk = File.Exists(m);
                    if (mainMk && !File.ReadAllText(m).Contains("\"install_id\":\"" + arg + "\"")) { o.Append("err=other_install\n"); r.Code = 9; break; }
                    if (!mainMk) o.Append("ok=absent\n");
                    else
                    {
                        foreach (var n in new[] { "SKILL.md", "uart", ".uart_hub" }) File.Delete(Path.Combine(d, n));
                        File.AppendAllText(Path.Combine(dir, "ops.log"), distro + " remove " + arg + "\n", u8);
                        if (Directory.GetFileSystemEntries(d).Length == 0) { Directory.Delete(d); o.Append("ok=removed\n"); }
                        else o.Append("ok=removed_files\n");
                    }
                    var rx = new List<string>();
                    foreach (var e in SkillsDeployer.Extras)
                    {
                        string xd = Path.Combine(claude, "skills", e), xm = Path.Combine(xd, ".uart_hub");
                        if (!File.Exists(xm) || !File.ReadAllText(xm).Contains("\"install_id\":\"" + arg + "\"")) continue;
                        foreach (var n in new[] { "SKILL.md", ".uart_hub" }) File.Delete(Path.Combine(xd, n));
                        if (Directory.GetFileSystemEntries(xd).Length == 0) Directory.Delete(xd);
                        File.AppendAllText(Path.Combine(dir, "ops.log"), distro + " remove " + e + " " + arg + "\n", u8);
                        rx.Add(e);
                    }
                    if (rx.Count > 0) o.Append("extras_removed=").Append(string.Join(",", rx)).Append('\n');
                    r.Code = 0;
                    break;
                }
                default:
                    o.Append("err=op ").Append(op).Append('\n');
                    r.Code = 2;
                    break;
            }
            r.Out = u8.GetBytes(o.ToString());
        }

        static readonly string[] FakeExtraNames = SkillsDeployer.Extras.SelectMany(e => new[] { e + "/SKILL.md", e + "/.uart_hub" }).ToArray();

        static ExtResult FakeSchtasks(string[] args)
        {
            var r = new ExtResult { Fake = true };
            string dir = T("fake_tasks");
            int tn = Array.FindIndex(args, x => string.Equals(x, "/tn", StringComparison.OrdinalIgnoreCase));
            string name = tn >= 0 && tn + 1 < args.Length ? args[tn + 1].TrimStart('\\') : "";
            string file = Path.Combine(dir, name + ".xml");
            bool query = args.Any(x => string.Equals(x, "/query", StringComparison.OrdinalIgnoreCase));
            bool del = args.Any(x => string.Equals(x, "/delete", StringComparison.OrdinalIgnoreCase));
            if (name.Length == 0 || name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0) { r.Code = 1; r.Err = L.T("fake: 작업 이름 없음", "fake: no task name"); }
            else if (!File.Exists(file)) { r.Code = 1; r.Err = "ERROR: The system cannot find the file specified."; }
            else if (query) { r.Code = 0; r.Out = File.ReadAllBytes(file); }
            else if (del) { File.Delete(file); r.Code = 0; r.Out = Encoding.UTF8.GetBytes("SUCCESS: The scheduled task \"" + name + "\" was successfully deleted."); }
            else { r.Code = 1; r.Err = L.T("fake: 지원하지 않는 인자", "fake: unsupported argument"); }
            Ext("[ext:fake] schtasks.exe " + ArgText(args) + " → rc=" + r.Code);
            return r;
        }

        static int ReadInt(string path, int dflt)
            => File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : dflt;

        // ================================================================== 프로세스 조회·종료
        /// <summary>pwsh.exe·powershell.exe 프로세스와 명령줄(WMI). 시험 모드는 fake_procs.json.</summary>
        public static List<ProcRow> ScriptHosts()
        {
            var l = new List<ProcRow>();
            if (IsTest)
            {
                var j = ReadJsonArray(T("fake_procs.json"));
                if (j.HasValue)
                    foreach (var e in j.Value.EnumerateArray())
                        l.Add(new ProcRow { Pid = (int)HubFiles.Long(e, "pid"), Name = HubFiles.Str(e, "name"), CommandLine = HubFiles.Str(e, "cmd") });
                Ext(L.T("[ext:fake] WMI Win32_Process(pwsh·powershell) → " + l.Count + "개", "[ext:fake] WMI Win32_Process(pwsh·powershell) → " + l.Count + " item(s)"));
                return l;
            }
            using var s = new ManagementObjectSearcher("SELECT ProcessId, Name, CommandLine FROM Win32_Process WHERE Name = 'pwsh.exe' OR Name = 'powershell.exe'");
            foreach (ManagementObject o in s.Get())
                using (o) l.Add(new ProcRow { Pid = Convert.ToInt32(o["ProcessId"], CultureInfo.InvariantCulture), Name = o["Name"] as string, CommandLine = o["CommandLine"] as string });
            Ext(L.T("[ext] WMI Win32_Process(pwsh·powershell) → " + l.Count + "개", "[ext] WMI Win32_Process(pwsh·powershell) → " + l.Count + " item(s)"));
            return l;
        }

        /// <summary>pid 와 시작 시각이 같은 프로세스만 kill(PID 재사용 방지). 시험 모드에서는 그 exe 가 시험 루트 안이어야 한다.</summary>
        public static bool KillVerified(long pid, long startTicks)
        {
            if (pid <= 0) return false;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                if (p.HasExited || (startTicks != 0 && HubFiles.StartTicks(p) != startTicks)) return false;
                if (IsTest)
                {
                    string exe = null;
                    try { exe = p.MainModule?.FileName; } catch (Exception) { }
                    if (exe == null || !Under(exe, TestRoot)) throw Violation("kill pid=" + pid + " " + (exe ?? L.T("(경로 모름)", "(path unknown)")));
                }
                p.Kill();
                p.WaitForExit(5000);
                return true;
            }
            catch (TestIsolationViolation) { throw; }
            catch (Exception) { return false; }
        }

        // ================================================================== 드라이브
        public static DriveState Drive(string letter)
        {
            letter = letter.TrimEnd(':', '\\').ToUpperInvariant();
            var st = new DriveState { Letter = letter };
            if (IsTest)
            {
                var j = HubFiles.ReadJson(T("fake_drives.json"));
                if (j.HasValue && j.Value.TryGetProperty(letter, out var d) && d.ValueKind == JsonValueKind.Object)
                {
                    st.Type = HubFiles.Str(d, "type") ?? "Fixed";
                    st.Ready = HubFiles.Bool(d, "ready");
                    st.Exists = true;
                    st.Root = T("drive_" + letter);
                }
                else { st.Type = "NoRootDirectory"; st.Root = T("drive_" + letter); }
                return st;
            }
            st.Root = letter + ":\\";
            try
            {
                var di = new DriveInfo(letter);
                st.Type = di.DriveType.ToString();
                st.Exists = di.DriveType != DriveType.NoRootDirectory;
                st.Ready = st.Exists && di.IsReady;
            }
            catch (Exception) { st.Type = "NoRootDirectory"; }
            return st;
        }

        // ================================================================== 권한
        /// <summary>Normal = 일반 토큰 · Elevated = UAC 켜짐 + '관리자 권한으로 실행'(TokenElevationTypeFull) · UacOff = UAC 꺼짐(EnableLUA=0)이라 관리자 계정의 모든 프로세스가 관리자 토큰.</summary>
        public enum Elevation { Normal, Elevated, UacOff }

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int len, out int retLen);

        /// <summary>시험 모드: &lt;root&gt;\fake_elevated 가 있으면 내용 "uac_off" → UacOff, 그 밖 → Elevated.</summary>
        public static Elevation ElevationState
        {
            get
            {
                if (IsTest)
                {
                    string f = T("fake_elevated");
                    if (!File.Exists(f)) return Elevation.Normal;
                    return File.ReadAllText(f).Trim() == "uac_off" ? Elevation.UacOff : Elevation.Elevated;
                }
                try
                {
                    using var id = WindowsIdentity.GetCurrent();
                    const int TokenElevationType = 18;               // 1 Default(연결 토큰 없음: UAC 꺼짐·표준 사용자) · 2 Full(올린 토큰) · 3 Limited(걸러진 토큰)
                    if (GetTokenInformation(id.Token, TokenElevationType, out int t, 4, out _))
                    {
                        if (t == 2) return Elevation.Elevated;
                        if (t == 3) return Elevation.Normal;
                    }
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator) ? Elevation.UacOff : Elevation.Normal;
                }
                catch (Exception) { return Elevation.Normal; }
            }
        }

        public static bool IsElevated => ElevationState != Elevation.Normal;

        // ================================================================== TCP listen 포트
        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

        /// <summary>127.0.0.1·0.0.0.0 에서 listen 중인 포트의 주인(포트 목록 중). 시험 모드는 fake_ports.json.</summary>
        public static List<PortOwner> TcpListeners(ICollection<int> ports)
        {
            var l = new List<PortOwner>();
            if (IsTest)
            {
                var j = ReadJsonArray(T("fake_ports.json"));
                if (j.HasValue)
                    foreach (var e in j.Value.EnumerateArray())
                    {
                        int port = (int)HubFiles.Long(e, "port");
                        if (ports.Contains(port)) l.Add(new PortOwner { Port = port, Pid = (int)HubFiles.Long(e, "pid"), Name = HubFiles.Str(e, "name") });
                    }
                return l;
            }
            const int AfInet = 2, TcpTableOwnerPidListener = 3;
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
            IntPtr buf = Marshal.AllocHGlobal(Math.Max(size, 4));
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, AfInet, TcpTableOwnerPidListener, 0) != 0) return l;
                int n = Marshal.ReadInt32(buf);
                for (int i = 0; i < n; i++)
                {
                    IntPtr row = buf + 4 + i * 24;                  // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid
                    uint lp = (uint)Marshal.ReadInt32(row, 8);
                    int port = (int)(((lp & 0xFF) << 8) | ((lp >> 8) & 0xFF));
                    if (!ports.Contains(port)) continue;
                    int pid = Marshal.ReadInt32(row, 20);
                    string name = null;
                    try { using var p = Process.GetProcessById(pid); name = p.ProcessName; } catch (Exception) { }
                    l.Add(new PortOwner { Port = port, Pid = pid, Name = name });
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return l;
        }

        // ================================================================== 창
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string title);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

        /// <summary>제목이 title 인 창을 앞으로(중복 실행한 설치기). 시험 모드에서는 창을 건드리지 않는다.</summary>
        public static bool BringWindowToFront(string title)
        {
            if (IsTest) { Ext(L.T("[ext:fake] 창 앞으로(" + title + ") — 시험 모드라 창을 건드리지 않음", "[ext:fake] bring window to front (" + title + ") — test mode, window not touched")); return false; }
            IntPtr h = FindWindowW(null, title);
            if (h == IntPtr.Zero) return false;
            ShowWindow(h, 9);                                    // SW_RESTORE
            return SetForegroundWindow(h);
        }

        // ================================================================== 터미널·아이콘(T3) — 시험 모드에서는 화면에 아무것도 띄우지 않는다
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetWindowTextW(IntPtr h, string text);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(IntPtr h, string text, string caption, uint type);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
        [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FlashInfo fi);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern void SHChangeNotify(int ev, uint flags, string p1, string p2);
        [StructLayout(LayoutKind.Sequential)] struct FlashInfo { public uint Size; public IntPtr Hwnd; public uint Flags, Count, Timeout; }

        public const int ShcneRenameItem = 0x1, ShcneCreate = 0x2, ShcneDelete = 0x4, ShcneUpdateItem = 0x2000;

        /// <summary>App Paths 레지스트리(HKCU·HKLM, 64·32비트 보기, 읽기만)의 exe 경로. 시험 모드는 fake_app_paths.json. 없으면 null.</summary>
        public static string AppPath(string exeName)
        {
            if (IsTest)
            {
                var j = HubFiles.ReadJson(T("fake_app_paths.json"));
                return j.HasValue ? HubFiles.Str(j.Value, exeName) : null;
            }
            foreach (var hive in new[] { Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryHive.LocalMachine })
                foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
                {
                    try
                    {
                        using var b = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
                        using var k = b.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName);
                        if (k?.GetValue(null) is string v && !string.IsNullOrWhiteSpace(v)) return Environment.ExpandEnvironmentVariables(v.Trim().Trim('"'));
                    }
                    catch (Exception) { }
                }
            return null;
        }

        /// <summary>PATH 의 폴더들. 시험 모드는 fake_path.txt(줄마다 폴더).</summary>
        public static string[] PathDirs()
        {
            if (IsTest)
            {
                string f = T("fake_path.txt");
                return File.Exists(f) ? File.ReadAllLines(f).Select(x => x.Trim()).Where(x => x.Length > 0).ToArray() : new string[0];
            }
            return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Select(x => x.Trim().Trim('"')).Where(x => x.Length > 0).ToArray();
        }

        /// <summary>이름이 names 중 하나인 프로세스와 명령줄(WMI)·주 창 제목. 시험 모드는 fake_procs.json(title·fg 포함).</summary>
        public static List<ProcRow> ProcessesNamed(params string[] names)
        {
            var l = new List<ProcRow>();
            if (IsTest)
            {
                var j = ReadJsonArray(T("fake_procs.json"));
                if (j.HasValue)
                    foreach (var e in j.Value.EnumerateArray())
                    {
                        string n = HubFiles.Str(e, "name");
                        if (!names.Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase))) continue;
                        l.Add(new ProcRow { Pid = (int)HubFiles.Long(e, "pid"), Name = n, CommandLine = HubFiles.Str(e, "cmd"), Title = HubFiles.Str(e, "title"),
                                            FgDeny = e.TryGetProperty("fg", out var fg) && fg.ValueKind == JsonValueKind.False });
                    }
                Ext(L.T("[ext:fake] WMI Win32_Process(" + string.Join("·", names) + ") → " + l.Count + "개", "[ext:fake] WMI Win32_Process(" + string.Join("·", names) + ") → " + l.Count + " item(s)"));
                return l;
            }
            string where = string.Join(" OR ", names.Select(n => "Name = '" + n.Replace("'", "") + "'"));
            using (var s = new ManagementObjectSearcher("SELECT ProcessId, Name, CommandLine FROM Win32_Process WHERE " + where))
                foreach (ManagementObject o in s.Get())
                    using (o) l.Add(new ProcRow { Pid = Convert.ToInt32(o["ProcessId"], CultureInfo.InvariantCulture), Name = o["Name"] as string, CommandLine = o["CommandLine"] as string });
            foreach (var r in l)
                try { using var p = Process.GetProcessById(r.Pid); r.Title = p.MainWindowTitle; } catch (Exception) { }
            Ext(L.T("[ext] WMI Win32_Process(" + string.Join("·", names) + ") → " + l.Count + "개", "[ext] WMI Win32_Process(" + string.Join("·", names) + ") → " + l.Count + " item(s)"));
            return l;
        }

        /// <summary>
        /// 그 프로세스의 주 창을 앞으로: 최소화면 SW_RESTORE(최대화 창은 그대로 둔다) + SetForegroundWindow, 거부되면 FlashWindowEx.
        /// "front" · "flash" · null(창 없음). 시험 모드: 창을 건드리지 않고 fg:false 주입이면 "flash", 아니면 "front".
        /// </summary>
        public static string FrontWindow(ProcRow r)
        {
            if (IsTest)
            {
                string res = r.FgDeny ? "flash" : "front";
                Ext(L.T("[ext:fake] 창 앞으로 pid=" + r.Pid + " → " + res + " — 시험 모드라 창을 건드리지 않음", "[ext:fake] bring window to front pid=" + r.Pid + " → " + res + " — test mode, window not touched"));
                return res;
            }
            IntPtr h;
            try { using var p = Process.GetProcessById(r.Pid); h = p.MainWindowHandle; }
            catch (Exception) { return null; }
            if (h == IntPtr.Zero) return null;
            ShowWindow(h, IsIconic(h) ? 9 : 5);                          // SW_RESTORE : SW_SHOW
            if (SetForegroundWindow(h)) return "front";
            var fi = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Hwnd = h, Flags = 3 | 12 };   // FLASHW_ALL | FLASHW_TIMERNOFG
            FlashWindowEx(ref fi);
            return "flash";
        }

        /// <summary>
        /// 터미널(GUI)을 띄운다(인자는 한 문자열 그대로 — TeraTerm /W="…" 따옴표를 다시 감싸지 않게). pid.
        /// 시험 모드: 띄우지 않고 "[ext:fake] 실행 …" + &lt;root&gt;\fake_launch.log 에 한 줄, pid 0.
        /// </summary>
        public static int LaunchGui(string exe, string args)
        {
            if (IsTest)
            {
                Ext(L.T("[ext:fake] 실행 " + exe + " " + args + " — 시험 모드라 띄우지 않음", "[ext:fake] run " + exe + " " + args + " — test mode, not launched"));
                try { File.AppendAllText(T("fake_launch.log"), exe + " | " + args + "\n", new UTF8Encoding(false)); } catch (Exception) { }
                return 0;
            }
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, Arguments = args, WorkingDirectory = Path.GetDirectoryName(exe) };
            using var p = Process.Start(psi);
            Ext(L.T("[ext] 실행 " + exe + " " + args + " → pid=" + p.Id, "[ext] run " + exe + " " + args + " → pid=" + p.Id));
            return p.Id;
        }

        /// <summary>그 프로세스의 주 창이 뜨면(timeoutMs 안) 제목을 바꾸고, 1초·2초 뒤 제목이 바뀌어 있으면 한 번씩 다시 쓴다(PuTTY 가 시작하며 덮어씀). 시험 모드는 줄만.</summary>
        public static bool SetWindowTitle(int pid, string title, int timeoutMs)
        {
            if (IsTest) { Ext(L.T("[ext:fake] SetWindowText pid=" + pid + " '" + title + "' — 시험 모드라 창을 건드리지 않음", "[ext:fake] SetWindowText pid=" + pid + " '" + title + "' — test mode, window not touched")); return true; }
            try
            {
                using var p = Process.GetProcessById(pid);
                var sw = Stopwatch.StartNew();
                IntPtr h = IntPtr.Zero;
                while (sw.ElapsedMilliseconds < timeoutMs && !p.HasExited)
                {
                    p.Refresh();
                    if ((h = p.MainWindowHandle) != IntPtr.Zero) break;
                    Thread.Sleep(100);
                }
                if (h == IntPtr.Zero || !SetWindowTextW(h, title)) return false;
                for (int k = 0; k < 2 && !p.HasExited; k++)
                {
                    Thread.Sleep(1000);
                    p.Refresh();
                    if (p.MainWindowTitle != title) SetWindowTextW(h, title);
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        // ---- 띄운 터미널 창 제목 맞추기(TermTitler). 시험 모드는 실제 프로세스·창을 보지 않는다(가짜 창 목록은 ITermWindows 로 넣는다).
        delegate bool EnumWindowsProc(IntPtr h, IntPtr lp);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeoutW(IntPtr h, uint msg, IntPtr wp, string lp, uint flags, uint timeoutMs, out IntPtr result);

        /// <summary>프로세스 시작 시각(UTC ticks, pid 재사용 구분). 없으면·시험 모드면 0.</summary>
        public static long ProcessStartTicks(int pid)
        {
            if (IsTest || pid <= 0) return 0;
            try { using var p = Process.GetProcessById(pid); return HubFiles.StartTicks(p); }
            catch (Exception) { return 0; }
        }

        /// <summary>pid 가 살아 있고 시작 시각이 같은가. 시험 모드는 false.</summary>
        public static bool ProcessAlive(int pid, long startTicks) => !IsTest && HubFiles.IsAlive(pid, startTicks);

        /// <summary>그 프로세스의 보이는 최상위 창 중 주인 창이 없는 것(대화상자 제외)과 제목. 시험 모드는 빈 목록.</summary>
        public static List<(IntPtr Handle, string Title)> TopWindows(int pid)
        {
            var l = new List<(IntPtr, string)>();
            if (IsTest || pid <= 0) return l;
            try
            {
                EnumWindows((h, _) =>
                {
                    GetWindowThreadProcessId(h, out uint wp);
                    if (wp != (uint)pid || !IsWindowVisible(h) || GetWindow(h, 4) != IntPtr.Zero) return true;   // GW_OWNER
                    var sb = new StringBuilder(512);
                    GetWindowTextW(h, sb, sb.Capacity);
                    l.Add((h, sb.ToString()));
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception) { }
            return l;
        }

        /// <summary>창 제목 바꾸기(WM_SETTEXT, 멈춘 창이면 500ms 안에 포기 — 감독 스레드를 붙잡지 않게). 시험 모드는 false.</summary>
        public static bool SetWindowTextTimeout(IntPtr h, string title)
        {
            if (IsTest || h == IntPtr.Zero) return false;
            try { return SendMessageTimeoutW(h, 0x000C, IntPtr.Zero, title, 0x0002, 500, out IntPtr r) != IntPtr.Zero && r != IntPtr.Zero; }   // WM_SETTEXT, SMTO_ABORTIFHUNG
            catch (Exception) { return false; }
        }

        /// <summary>안내 창(맨 위, 확인 버튼 하나). 시험 모드는 띄우지 않고 "[ui:fake] 안내 창: …" 줄만.</summary>
        public static void ShowNote(string caption, string text)
        {
            if (IsTest) { Ext(L.T("[ui:fake] 안내 창: ", "[ui:fake] guide window: ") + caption + " — " + text.Replace("\n", " ")); return; }
            MessageBoxW(IntPtr.Zero, text, caption, 0x40 | 0x10000 | 0x40000);   // MB_ICONINFORMATION | MB_SETFOREGROUND | MB_TOPMOST
        }

        /// <summary>트레이 풍선 클릭 뒤 띄우는 open 이 창을 앞으로 가져올 수 있게(ASFW_ANY). 시험 모드는 아무것도 안 함.</summary>
        public static void AllowForeground()
        {
            if (IsTest) return;
            try { AllowSetForegroundWindow(-1); } catch (Exception) { }
        }

        /// <summary>그 프로세스 하나에만 전면 권한(/connect-uart 가 띄운 터미널). pid ≤ 0 이거나 시험 모드면 아무것도 안 함.</summary>
        public static void AllowForeground(int pid)
        {
            if (IsTest || pid <= 0) return;
            try { AllowSetForegroundWindow(pid); } catch (Exception) { }
        }

        /// <summary>셸에 바로가기 변경을 알린다(SHCNF_PATHW, K6 결과 전이라 항상 부른다). 시험 모드는 부르지 않는다. "ok" | "fake".</summary>
        public static string ShellNotify(int ev, string p1, string p2 = null)
        {
            if (IsTest) return "fake";
            try { SHChangeNotify(ev, 0x0005, p1, p2); } catch (Exception) { }
            return "ok";
        }

        /// <summary>
        /// 아이콘 폴더가 OneDrive 동기화 폴더인가(계획 E3 — 그러면 시작 메뉴 'UART 허브' 폴더에 만든다): 경로에 'OneDrive*' 조각,
        /// %OneDrive%·%OneDriveCommercial%·%OneDriveConsumer% 아래, 또는 그 폴더가 바탕화면 KnownFolder 이고 기본 위치(%USERPROFILE%\Desktop)에서 리디렉션됨.
        /// 시험 모드: 경로 조각 또는 &lt;root&gt;\fake_onedrive(실제 환경변수·KnownFolder 는 보지 않는다).
        /// </summary>
        public static bool IsOneDriveDir(string dir, out string why)
        {
            why = null;
            string full = Path.GetFullPath(dir).TrimEnd('\\');
            if (full.Split('\\').Any(s => s.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase))) { why = L.T("경로에 OneDrive 폴더", "OneDrive folder in the path"); return true; }
            if (IsTest)
            {
                if (File.Exists(T("fake_onedrive"))) { why = L.T("시험 주입(fake_onedrive)", "test injection (fake_onedrive)"); return true; }
                return false;
            }
            foreach (var env in new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" })
            {
                string r = Environment.GetEnvironmentVariable(env);
                if (!string.IsNullOrWhiteSpace(r) && Under(full, r)) { why = L.T("%" + env + "% 아래", "under %" + env + "%"); return true; }
            }
            // 리디렉션 자체는 기준이 아니다(스펙: "OneDrive 동기화 폴더면"). 로컬 고정 드라이브로 옮긴 바탕화면(예 D:\Desktop)은 그대로 쓰고,
            // 네트워크(UNC·네트워크 드라이브)로 리디렉션된 바탕화면만 동기화·공유 위험이 같아 시작 메뉴로 보낸다.
            if (SamePath(full, Desktop) && !SamePath(full, Path.Combine(UserProfile, "Desktop")) && IsNetworkPath(full)) { why = L.T("바탕화면 KnownFolder 가 네트워크로 리디렉션됨(" + full + ")", "Desktop KnownFolder is redirected to the network (" + full + ")"); return true; }
            return false;
        }

        static bool IsNetworkPath(string full)
        {
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            try { return new DriveInfo(Path.GetPathRoot(full)).DriveType == DriveType.Network; }
            catch (Exception) { return false; }
        }

        static JsonElement? ReadJsonArray(string path)
        {
            string t = File.Exists(path) ? HubFiles.ReadShared(path) : null;
            if (string.IsNullOrWhiteSpace(t)) return null;
            try
            {
                using var d = JsonDocument.Parse(t);
                return d.RootElement.ValueKind == JsonValueKind.Array ? d.RootElement.Clone() : (JsonElement?)null;
            }
            catch (JsonException) { return null; }
        }
    }
}
