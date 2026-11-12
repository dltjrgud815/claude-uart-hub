// `UartHub.exe core` — 코어 프로세스(계획 §3.8, hub_core.ps1 이식). 보통은 감독(sup)이 같은 exe 로 띄운다(C-10).
//   core [--hub-dir D] [--config P] [--core-mutex M] [--restart-reason R] [--sim <사양.json> --sim-dll <UartHub.Tests.dll>]
//        [--watch-sup <감독 mutex> --update-mutex <update mutex>]
//   1. 코어 mutex(기본 Local\uart_hub_core, 시험은 Local\uart_hub_test_*)를 3초 기다려 잡는다(감독의 순간 확인과 겹칠 수 있다). 못 잡으면 exit 3.
//   2. 설정: --config, 없으면 <허브 폴더>\config.json, 그것도 없으면 기본값. hub_dir 은 --hub-dir 로 고정(감독·CLI 가 보는 폴더와 같아야 한다),
//      log_root 가 명시되지 않았으면 허브 폴더의 부모(C-2). 허브 폴더: --hub-dir → UART_HUB_DIR → install.json(없으면 exit 2).
//   3. CoreHost: core.pid 기록, UnhandledException → 큐 flush + *** hub crash *** → exit 70, ControlServer 의 hub stop/start/upgrade → 감독 요청 스풀.
//   4. Hub 시작. --sim 이면 --sim-dll 의 SimCore 가 코어 안에 FakeBoard 를 만든다(실제 COM 미사용, 운영에서는 tests DLL 을 로드하지 않는다).
//   5. 감독이 core.stop 에 내 PID 를 쓰면 정상 종료: 300ms → Hub.Stop(*** hub stop ***) → core.pid 삭제 → exit 0.
//   C-11(운영 모드에서만, --watch-sup): sup_watch_s 마다 감독 mutex 와 update mutex 의 존재만 확인하고(잡지 않음), 둘 다 없고 install.json 이
//   state:"installed" 이며 그 exe 가 있으면 sup 을 분리 기동한다(hub.log [core] sup respawn). 띄운 감독이 60초 안에 (감독 mutex 없이) 사라지면 다음 기동을
//   30→60→120→300초로 미루고, 10분 살아 있으면 초기화한다. 띄운 감독이 아직 살아 있는데 mutex 가 없으면(기동 중) 그 회차는 다시 띄우지 않는다.
//   시험(C-13): UART_HUB_TEST_RESPAWN="d1,d2,d3,d4,즉사 창,초기화"(초) 로 축소. 시험 주입 core-crash = 기동 직후 exit 70(크래시 루프).
//   출력은 쓰지 않는다(모두 <허브 폴더>\hub.log). 감독이 죽어도 코어는 계속 돈다(Job Object 없음).
//   종료코드: 0 정상 종료 · 2 기동 실패 · 3 코어 mutex 점유 · 70 hub crash · -1 kill
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Threading;

namespace UartHub
{
    public static class CoreMain
    {
        public static int Run(string[] args)
        {
            string hubDir = null, configPath = null, coreMutex = SystemAccess.MutexName("core"), reason = null, sim = null, simDll = null, watchSup = null, updMutex = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Val() => i + 1 < args.Length ? args[++i] : null;
                switch (a)
                {
                    case "--hub-dir": hubDir = Val(); break;
                    case "--config": configPath = Val(); break;
                    case "--core-mutex": coreMutex = Val(); break;
                    case "--restart-reason": reason = Val(); break;
                    case "--sim": sim = Val(); break;
                    case "--sim-dll": simDll = Val(); break;
                    case "--watch-sup": watchSup = Val(); break;
                    case "--update-mutex": updMutex = Val(); break;
                    default: return CoreHost.StartFailedExitCode;          // 모르는 인자: 감독만 부르는 하위 명령이라 조용히 기동 실패
                }
            }
            if (string.IsNullOrEmpty(hubDir)) hubDir = InstallInfo.ResolveHubDir(out _);
            if (string.IsNullOrEmpty(hubDir)) return CoreHost.StartFailedExitCode;
            NativeHandles.DontInheritStd();
            var log = new HubLog(Path.Combine(hubDir, "hub.log"), SystemClock.Instance, "core");
            try { Directory.CreateDirectory(hubDir); } catch (Exception) { }

            // ---- 1. 코어 mutex
            var mutex = new Mutex(false, coreMutex);
            bool own;
            try { own = mutex.WaitOne(3000); } catch (AbandonedMutexException) { own = true; }
            if (!own)
            {
                log.Write("코어 mutex(" + coreMutex + ") 점유 중 — 다른 코어가 실행 중이라 끝냄(exit 3) pid=" + Environment.ProcessId);
                return CoreHost.CoreBusyExitCode;
            }

            if (SystemAccess.TestFail("core-crash"))
            {
                log.Write("시험 주입(UART_HUB_TEST_FAIL=core-crash): 기동 직후 끝냄(exit " + CoreHost.CrashExitCode + ")");
                return CoreHost.CrashExitCode;
            }

            // ---- 2~4. 설정·Hub
            Hub hub = null;
            CoreHost ch;
            LogHider hider = null;
            SpoolSweeper sweeper = null;
            try
            {
                if (string.IsNullOrEmpty(configPath)) configPath = HubFiles.ConfigPath(hubDir);
                Config cfg;
                if (File.Exists(configPath)) cfg = Config.Load(configPath);
                else { cfg = Config.Default(); configPath = ""; }
                if (!string.IsNullOrEmpty(cfg.HubDir) && !string.Equals(Path.GetFullPath(cfg.HubDir).TrimEnd('\\'), Path.GetFullPath(hubDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    log.Write("config hub_dir(" + cfg.HubDir + ") 대신 --hub-dir(" + hubDir + ") 사용");
                cfg.HubDir = hubDir;
                cfg.ResolveLogRoot(hubDir);
                if (SystemAccess.IsTest)
                {
                    // C-13: 시험 코어는 실제 FTDI 를 열거하지 않고(vids []), 시험 포트(17000 이상)만 쓴다 — 운영 허브(7200·7001~)와 실제 COM 을 건드리지 않게
                    if (cfg.Vids.Count > 0) { log.Write("시험 모드: vids " + string.Join(",", cfg.Vids) + " → [] (실제 장치 열거 안 함)"); cfg.Vids.Clear(); }
                    if (cfg.ControlPort < 17000 || cfg.BasePort < 17000)
                        throw SystemAccess.Violation("시험 코어가 실제 포트를 쓰려 함 control_port=" + cfg.ControlPort + " base_port=" + cfg.BasePort);
                }
                IPortProvider[] providers = null;
                MethodInfo attach = null;
                if (!string.IsNullOrEmpty(sim))
                {
                    if (string.IsNullOrEmpty(simDll)) throw new ArgumentException("--sim 에는 --sim-dll <UartHub.Tests.dll> 가 필요");
                    var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(simDll));
                    var t = asm.GetType("UartHub.Testing.SimCore", true);
                    providers = (IPortProvider[])t.GetMethod("Providers").Invoke(null, new object[] { sim });
                    attach = t.GetMethod("Attach");
                }
                hub = new Hub(cfg, null, providers);
                if (!string.IsNullOrEmpty(reason)) hub.RestartReason = reason;
                ch = CoreHost.Install(hub, hubDir, configPath);
                hub.Start();
                hider = LogHider.Start(hub);           // 부록 C.11: log_layout 2 면 .raw·.idx·콘솔로그\live 에 숨김 속성(속성만)
                sweeper = SpoolSweeper.Start(hub);     // 부록 C.12: log_layout 3 이면 <허브 폴더>\임시기록 숨김 + 24시간·2GB 보관 정리
                ch.Started();
                attach?.Invoke(null, new object[] { hub, sim });
            }
            catch (Exception e)
            {
                if (e is TargetInvocationException tie && tie.InnerException != null) e = tie.InnerException;
                log.Write("코어 기동 실패(exit 2): " + e);
                try { hub?.Stop(); } catch (Exception) { }
                return CoreHost.StartFailedExitCode;
            }

            if (!string.IsNullOrEmpty(watchSup))
            {
                var h = hub;
                new Thread(() => WatchSupervisor(h, watchSup, updMutex, coreMutex)) { IsBackground = true, Name = "SupWatch" }.Start();
            }

            // ---- 5. 감독의 정상 종료 요청(core.stop) 대기
            while (!ch.StopRequested) Thread.Sleep(200);
            Thread.Sleep(300);                     // hub stop 을 보낸 CLI 에 응답이 먼저 나가도록
            hider?.Dispose();
            sweeper?.Dispose();
            try { hub.Stop(); } catch (Exception e) { log.Write("Hub.Stop 오류: " + e.Message); }
            try { hub.Control.Events.Join(1500); } catch (Exception) { }   // X6(L12): 사건 추적 스레드가 앞뒤 로그에 '# 끝' 을 쓰고 남은 기록을 마치게
            ch.Exit();
            try { mutex.ReleaseMutex(); } catch (Exception) { }
            GC.KeepAlive(mutex);
            return 0;
        }

        /// <summary>이름 있는 mutex 가 있는가(C-8 존재 확인: 잡지 않고 바로 닫는다). 이름이 비면 false.</summary>
        public static bool MutexExists(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            try
            {
                if (!Mutex.TryOpenExisting(name, out var m)) return false;
                m.Dispose();
                return true;
            }
            catch (UnauthorizedAccessException) { return true; }          // 있지만 열 권한이 없음
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 자가 복구(C-9·C-11)가 띄울 감독 exe: install.json 이 state:"installed" 이고 그 exe 가 있으면 경로, 아니면 null + 이유.
        /// 감독·update mutex 확인은 부르는 쪽이 한다(코어 감시는 백오프 판정 순서 때문에 먼저 본다).
        /// </summary>
        public static string RespawnExe(out string why)
        {
            var info = InstallInfo.Load(InstallInfo.DefaultPath);
            why = info == null ? "install.json 없음"
                : info.State != "installed" ? "install.json state=" + (info.State ?? "(없음)")
                : string.IsNullOrEmpty(info.Exe) || !SystemAccess.FileExists(info.Exe) ? "exe 없음(" + (info.Exe ?? "") + ")" : null;
            return why == null ? info.Exe : null;
        }

        // ================================================================== C-11 감독 감시
        static void WatchSupervisor(Hub hub, string supMutex, string updMutex, string coreMutex)
        {
            var log = hub.Log;
            var inv = CultureInfo.InvariantCulture;
            double[] delays = Enumerable.Range(0, 4).Select(i => SystemAccess.TestNumber("UART_HUB_TEST_RESPAWN", i, new double[] { 30, 60, 120, 300 }[i])).ToArray();
            double quickS = SystemAccess.TestNumber("UART_HUB_TEST_RESPAWN", 4, 60), resetS = SystemAccess.TestNumber("UART_HUB_TEST_RESPAWN", 5, 600);
            log.Write("감독 감시 시작(sup_watch_s=" + hub.Config.SupWatchS.ToString(inv) + ", " + supMutex + " · " + updMutex
                      + ", 즉사 백오프 " + string.Join("→", delays.Select(d => d.ToString(inv))) + "초 · 즉사 창 " + quickS.ToString(inv) + "초 · 초기화 " + resetS.ToString(inv) + "초)");
            int fails = 0, spawnPid = 0;
            long spawnTicks = 0;
            DateTime lastSpawn = default, nextAllowed = default;
            string lastSkip = null;
            bool waitLogged = false;
            while (true)
            {
                Thread.Sleep(TimeSpan.FromSeconds(Math.Max(0.2, hub.Config.SupWatchS)));
                try
                {
                    var now = DateTime.UtcNow;
                    bool sup = MutexExists(supMutex), upd = MutexExists(updMutex);
                    if (lastSpawn != default)
                    {
                        double age = (now - lastSpawn).TotalSeconds;
                        if (!HubFiles.IsAlive(spawnPid, spawnTicks))
                        {
                            // 내가 띄운 감독이 사라졌다. 감독 mutex 가 있으면 다른 감독이 이긴 것(경쟁에서 진 쪽은 exit 0) — 즉사가 아니다
                            if (!sup && age < quickS)
                            {
                                double d = delays[Math.Min(fails, delays.Length - 1)];
                                fails++;
                                nextAllowed = now.AddSeconds(d);
                                waitLogged = false;
                                log.Write("띄운 감독 pid=" + spawnPid + " 이 " + quickS.ToString(inv) + "초 안에 사라짐(연속 " + fails + "회) → 다음 기동은 " + d.ToString(inv) + "초 뒤");
                            }
                            lastSpawn = default;
                        }
                        else if (fails > 0 && age >= resetS)
                        {
                            fails = 0; nextAllowed = default; lastSpawn = default;
                            log.Write("띄운 감독이 " + resetS.ToString(inv) + "초 살아 있음 → 재기동 백오프 초기화");
                        }
                        else if (!sup && !upd && age < quickS) continue;          // 띄운 감독이 아직 기동 중(mutex 전) — 또 띄우지 않는다
                    }
                    if (sup || upd) { lastSkip = null; continue; }
                    if (now < nextAllowed)
                    {
                        if (!waitLogged) log.Write("감독 없음 — 백오프 대기 " + (nextAllowed - now).TotalSeconds.ToString("0.0", inv) + "초");
                        waitLogged = true;
                        continue;
                    }
                    string exe = RespawnExe(out string skip);
                    if (exe == null)
                    {
                        if (skip != lastSkip) log.Write("감독 없음 — 재기동하지 않음: " + skip);
                        lastSkip = skip;
                        continue;
                    }
                    lastSkip = null;
                    var p = SystemAccess.StartDetached(exe, new[] { "sup", "--sup-mutex", supMutex, "--core-mutex", coreMutex, "--update-mutex", updMutex });
                    log.Write("sup respawn pid=" + p.Pid + " exe=" + exe + (fails > 0 ? " (연속 즉사 " + fails + "회 뒤)" : ""));
                    lastSpawn = now; spawnPid = p.Pid; spawnTicks = p.StartTicks;
                }
                catch (Exception e) { log.WriteThrottled("supwatch", "감독 감시 오류: " + e.GetType().Name + ": " + e.Message); }
            }
        }
    }

    /// <summary>표준 핸들 상속 막기: 감독·코어가 띄우는 자식(코어·감독)이 호출자(WSL·시험 하네스)의 파이프를 물고 남지 않게 한다.</summary>
    public static class NativeHandles
    {
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GetStdHandle(int nStdHandle);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);
        const uint HandleFlagInherit = 1;

        public static void DontInheritStd()
        {
            foreach (int n in new[] { -10, -11, -12 })
            {
                IntPtr h = GetStdHandle(n);
                if (h != IntPtr.Zero && h != new IntPtr(-1)) SetHandleInformation(h, HandleFlagInherit, 0);
            }
        }
    }
}
