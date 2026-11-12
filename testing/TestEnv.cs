// 시험 대상 exe 위치(계획 T1 H2). 프로세스를 띄우는 시나리오(CLI 1·9·16·19·21·uart-cli-port, 감독·코어 20·33·hang)는
// pwsh·.ps1 대신 배포 산출물 UartHub.exe(sup·core·cli)를 띄운다. tests/run_sim.ps1 -Exe 가 UartHubExe 를 정한다.
// 기본값은 빌드 배치(out/<ver>/{UartHub.exe, lib/UartHub.Tests.dll})의 lib\..\UartHub.exe.
using System.IO;

namespace UartHub.Testing
{
    public static class TestEnv
    {
        /// <summary>이 시험 DLL 이 있는 폴더(lib).</summary>
        public static string LibDir => Path.GetDirectoryName(typeof(TestEnv).Assembly.Location);

        static string exe;
        /// <summary>시험할 UartHub.exe(sup·core·cli). run_sim.ps1 -Exe 가 설정한다.</summary>
        public static string UartHubExe
        {
            get => exe ?? Path.GetFullPath(Path.Combine(LibDir, "..", "UartHub.exe"));
            set => exe = value;
        }

        /// <summary>코어 `--sim-dll` 로 넘길 시험 DLL(= 이 DLL).</summary>
        public static string TestsDll => typeof(TestEnv).Assembly.Location;
    }
}
