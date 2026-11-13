// UartHub.exe 진입점(계획 §2.3 b, C-1): 하위 명령 sup · core · cli · open · version · setup · install · uninstall. 알 수 없는 하위 명령은 창 없이 exit 106.
// setup [--yes] [--json] [--no-start](X7 부록 D): Claude 가 INSTALL_UART_HUB.md 를 따라 부르는 창 없는 휴대용 등록(--yes 없으면 계획만). uninstall --yes 는 창 없는 제거.
// 인자 없이 실행(더블클릭): 휴대용 — 그 자리에서 허브 시작(Installer "portable", X4: 자기 복사·다른 위치 실행·바로가기·작업 스케줄러 없음).
//   예외: 예전 설치기의 설치본(%LOCALAPPDATA%\uart_hub\UartHub.exe)이면 지금처럼 sup. 설치기는 명시적 하위 명령 install 로만.
// WinExe 라 콘솔이 없다. 표준 출력은 상속된 핸들(파이프)에만 나간다(K4). cli·core·version 경로는 WinForms 형식을 건드리지 않는다
// (트레이 코드는 TrayApp 에만 있고 sup 의 트레이 경로에서만 JIT 된다).
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace UartHub.App
{
    static class Program
    {
        static void Out(string s) => Write(Console.OpenStandardOutput(), s);
        static void Err(string s) => Write(Console.OpenStandardError(), s);
        static void Write(Stream o, string s)
        {
            try
            {
                using (o)
                {
                    var b = new UTF8Encoding(false).GetBytes(s + "\n");
                    o.Write(b, 0, b.Length);
                    o.Flush();
                }
            }
            catch (Exception) { }
        }

        [STAThread]
        static int Main(string[] args)
        {
            string sub = args.Length > 0 ? args[0] : null;
            string[] rest = args.Skip(1).ToArray();
            if (sub == null && SystemAccess.SamePath(Environment.ProcessPath, SystemAccess.InstalledExe)) sub = "sup";
            try
            {
                switch (sub)
                {
                    case null: return Installer.Main("portable", rest, RunInstallGui);
                    case "cli": return Cli.Run(rest);
                    case "core": return CoreMain.Run(rest);
                    case "sup": return Sup(rest);
                    case "open": return OpenCmd.Run(rest);
                    case "version": return Version(rest);
                    case "setup":
                    case "install":
                    case "uninstall": return Installer.Main(sub, rest, RunInstallGui);
                    case "render-ui": return RenderUiCmd(rest);                    // 숨은 개발용: 창을 띄우지 않고 모든 창·트레이 메뉴 모형을 PNG 로(UI_COPY.md 검토)
                    default:
                        Err(L.T("uart: E106 rejected 알 수 없는 하위 명령: " + sub + " — sup | core | cli | open | version | setup | install | uninstall", "uart: E106 rejected unknown subcommand: " + sub + " — sup | core | cli | open | version | setup | install | uninstall"));
                        return 106;
                }
            }
            catch (Exception e)
            {
                // 창(WER·WinForms 예외 대화상자)을 띄우지 않고 끝낸다
                Err(L.T("UartHub " + (sub ?? "") + ": 내부 오류 " + e.GetType().Name + ": " + e.Message, "UartHub " + (sub ?? "") + ": internal error " + e.GetType().Name + ": " + e.Message));
                return sub == "cli" ? 103 : sub == null || sub == "setup" || sub == "install" || sub == "uninstall" ? Installer.RcFailed : CoreHost.StartFailedExitCode;
            }
        }

        static int Version(string[] a)
        {
            if (a.Contains("--json"))
            {
                string sc = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                    .Where(x => x.Key == "self_contained").Select(x => x.Value).FirstOrDefault() ?? "false";
                Out(InstallInfo.VersionJson(string.Equals(sc, "true", StringComparison.OrdinalIgnoreCase)));
            }
            else Out(Hub.Version);
            return 0;
        }

        // ================================================================== sup
        /// <summary>
        /// 감독(uart_hub.ps1 이식): sup [--no-tray] [--hub-dir D] [--sup-mutex M] [--core-mutex M] [--update-mutex M]
        ///                              [--sim 사양 --sim-dll Tests.dll] [--tick-ms N] [--icons-dir D]
        /// update mutex 가 있으면(설치기가 교체 중, C-8) 바로 exit 0(감독 mutex 를 잡은 뒤에도 한 번 더 본다). 감독 mutex 를 못 잡으면 다른 감독이 있으므로 조용히 exit 0.
        /// 허브 폴더: --hub-dir → UART_HUB_DIR → install.json(이 경우만 운영 모드, C-11). 종료코드: 0 정상·이미 실행 중 · 2 기동 실패.
        /// 바탕화면 아이콘(T3): --icons-dir 이 있으면 그 폴더(--no-tray 에서도 ls 폴링). 없으면 운영 모드일 때만 install.json 의 icons_dir(없으면 바탕화면) —
        /// 단 시험 모드(C-13)의 운영 감독은 --icons-dir 를 줄 때만 아이콘을 다룬다(설치·업데이트 시나리오의 icons.json·바탕화면을 바꾸지 않게).
        /// </summary>
        static int Sup(string[] a)
        {
            var o = new SupervisorOptions();
            bool noTray = SystemAccess.IsTest;                          // 시험 모드(C-13): 트레이를 띄우지 않는다(사용자 화면 무변경)
            int tickMs = 200;
            for (int i = 0; i < a.Length; i++)
            {
                string Val() => i + 1 < a.Length ? a[++i] : null;
                switch (a[i])
                {
                    case "--no-tray": noTray = true; break;
                    case "--hub-dir": o.HubDir = Val(); break;
                    case "--sup-mutex": o.SupMutex = Val(); break;
                    case "--core-mutex": o.CoreMutex = Val(); break;
                    case "--update-mutex": o.UpdateMutex = Val(); break;
                    case "--sim": o.Sim = Val(); break;
                    case "--sim-dll": o.SimDll = Val(); break;
                    case "--icons-dir": o.IconsDir = Val(); break;
                    case "--tick-ms":
                        if (!int.TryParse(Val(), out tickMs) || tickMs < 10) { Err(L.T("sup: --tick-ms 는 10 이상 정수", "sup: --tick-ms must be an integer of 10 or more")); return 2; }
                        break;
                    default: Err(L.T("sup: 알 수 없는 인자 " + a[i], "sup: unknown argument " + a[i])); return 2;
                }
            }
            if (SystemAccess.IsTest && o.Sim == null && File.Exists(SystemAccess.TestFile("sim_spec.json")))
            {
                // C-13: 시험 루트의 FakeBoard 사양이 있으면 코어 안 FakeBoard(실제 COM·WMI 열거 안 함). tests DLL 경로는 sim_dll.txt
                o.Sim = SystemAccess.TestFile("sim_spec.json");
                o.SimDll = File.Exists(SystemAccess.TestFile("sim_dll.txt")) ? File.ReadAllText(SystemAccess.TestFile("sim_dll.txt")).Trim() : null;
            }
            if (CoreMain.MutexExists(o.UpdateMutex)) return 0;           // 설치기가 교체 공백 중: 새 감독은 설치기가 띄운다
            SystemAccess.TestDelay("sup-pre-mutex");                    // 시험(mutex-race ①): mutex 획득 직전에 멈춘 감독
            var mutex = new Mutex(false, o.SupMutex);
            bool own;
            try { own = mutex.WaitOne(0); } catch (AbandonedMutexException) { own = true; }
            if (!own) return 0;
            try
            {
                if (CoreMain.MutexExists(o.UpdateMutex)) return 0;       // 확인 뒤 mutex 를 잡는 사이에 설치기가 공백을 시작했다(C-8 순서)
                if (string.IsNullOrEmpty(o.HubDir))
                {
                    o.HubDir = InstallInfo.ResolveHubDir(out bool fromInstall);
                    o.Production = fromInstall;
                }
                if (string.IsNullOrEmpty(o.HubDir))
                    return Fatal(noTray, L.T("허브 폴더를 정하지 못함 — 설치되지 않음(--hub-dir·UART_HUB_DIR·" + InstallInfo.DefaultPath + " 없음)", "Could not find the hub folder — not installed (no --hub-dir, UART_HUB_DIR or " + InstallInfo.DefaultPath + ")"));
                L.ConfigureFromFile(HubFiles.ConfigPath(o.HubDir));       // 감독·트레이 문구의 언어(config.json "language")
                NativeHandles.DontInheritStd();
                o.Exe = Environment.ProcessPath;
                o.PollCables = !noTray;
                if (string.IsNullOrEmpty(o.IconsDir) && o.Production && !SystemAccess.IsTest)
                {
                    var info = InstallInfo.Load(InstallInfo.DefaultPath);
                    o.IconsDir = !string.IsNullOrEmpty(info?.IconsDir) ? info.IconsDir : SystemAccess.Desktop;
                }
                Supervisor sup;
                try
                {
                    sup = new Supervisor(o);
                    sup.Init();
                }
                catch (Exception e) { return Fatal(noTray, L.T("감독 기동 실패: " + e.GetType().Name + ": " + e.Message, "Supervisor failed to start: " + e.GetType().Name + ": " + e.Message), o.HubDir); }
                if (noTray)
                {
                    while (!sup.QuitRequested)
                    {
                        try { sup.Tick(); } catch (Exception e) { try { sup.LogError(e); } catch (Exception) { } }
                        Thread.Sleep(tickMs);
                    }
                }
                else RunTray(sup, tickMs, o.HubDir);
                sup.Shutdown();
                return 0;
            }
            finally
            {
                try { mutex.ReleaseMutex(); } catch (Exception) { }
                GC.KeepAlive(mutex);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void RunTray(Supervisor sup, int tickMs, string hubDir) => TrayApp.Run(sup, tickMs, hubDir);

        /// <summary>설치기 화면(확인 창 + 진행 창). 시험 모드·dry-run 은 Installer 가 이 경로를 부르지 않는다.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static int RunInstallGui(string title, Func<IInstallUi, int> work) => InstallForm.Run(title, work);

        /// <summary>기동 실패: hub.log(가능하면) + 트레이 풍선(--no-tray 가 아니면 30초) → exit 2.</summary>
        static int Fatal(bool noTray, string msg, string hubDir = null)
        {
            if (hubDir != null)
                try { Directory.CreateDirectory(hubDir); new HubLog(Path.Combine(hubDir, "hub.log"), SystemClock.Instance, "sup").Write(msg); } catch (Exception) { }
            Err("sup: " + msg);
            if (!noTray) ShowFatal(msg);
            return 2;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ShowFatal(string msg) => TrayApp.ShowFatal(msg);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int RenderUiCmd(string[] a)
        {
            // render-ui <출력 폴더> [--lang en|ko]: --lang 은 영어판 가이드 그림용(없으면 환경변수 → Windows 언어)
            string dir = null, lang = null;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] == "--lang" && i + 1 < a.Length && (a[i + 1] == L.En || a[i + 1] == L.Ko)) lang = a[++i];
                else if (dir == null && !a[i].StartsWith("--", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(a[i])) dir = a[i];
                else dir = "";
            }
            if (string.IsNullOrEmpty(dir)) { Err(L.T("render-ui <출력 폴더>", "render-ui <output folder>") + " [--lang en|ko]"); return 106; }
            if (lang != null) L.Use(lang);
            foreach (var f in RenderUi.Run(dir)) Out(f);
            return 0;
        }
    }
}
