// install.json(계획 §3.1): version·install_id·exe·hub_dir·log_root·icons_dir·skills[]·state(installing|installed)·portable(X4 휴대용 실행 — exe 가 사용자 폴더의 그 파일).
// writer 는 살아 있는 설치기, 설치기가 없을 때만 감독의 Repair(C-14). 이 파일은 읽기·쓰기 골격과 C-2 허브 폴더 결정, version --json 이다.
// C-12: 읽을 때 모든 최상위 키의 원문을 순서대로 보관하고, 쓸 때 아는 키만 바꿔 다시 쓴다(새 판이 더한 키가 되돌림 뒤에도 남는다).
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace UartHub
{
    public sealed class InstallInfo
    {
        public string Version, InstallId, Exe, HubDir, LogRoot, IconsDir, State;
        /// <summary>휴대용 실행(인자 없는 더블클릭)이 쓴 install.json — 바로가기·설치본(%LOCALAPPDATA%\uart_hub\UartHub.exe)이 없다.</summary>
        public bool Portable;
        bool hadPortable;

        readonly JObj raw;                 // 읽은 원문(키 순서·모르는 키 포함)
        readonly bool[] had = new bool[Keys.Length];
        static readonly string[] Keys = { "version", "install_id", "exe", "hub_dir", "log_root", "icons_dir", "state" };

        public InstallInfo() { raw = new JObj(); }
        InstallInfo(JObj r) { raw = r; }

        /// <summary>%LOCALAPPDATA%\uart_hub (설치 폴더). 시험 모드(C-13)면 &lt;UART_HUB_TEST_ROOT&gt;\LocalAppData\uart_hub.</summary>
        public static string DefaultDir => SystemAccess.InstallDir;
        public static string DefaultPath => Path.Combine(DefaultDir, "install.json");

        /// <summary>install.json 을 읽는다. 없거나 객체가 아니면 null.</summary>
        public static InstallInfo Load(string path)
        {
            var j = HubFiles.ReadJson(path);
            if (!j.HasValue) return null;
            var e = j.Value;
            var info = new InstallInfo(HubFiles.ToJObj(e));
            string[] v = Keys.Select(k => HubFiles.Str(e, k)).ToArray();
            for (int i = 0; i < Keys.Length; i++) info.had[i] = e.TryGetProperty(Keys[i], out _);
            info.Version = v[0]; info.InstallId = v[1]; info.Exe = v[2]; info.HubDir = v[3]; info.LogRoot = v[4]; info.IconsDir = v[5]; info.State = v[6];
            info.hadPortable = e.TryGetProperty("portable", out var pt);
            info.Portable = info.hadPortable && pt.ValueKind == JsonValueKind.True;
            return info;
        }

        /// <summary>JSON 텍스트(아는 키는 현재 값, 모르는 키는 읽은 원문 그대로·같은 순서).</summary>
        public string ToJson()
        {
            string[] v = { Version, InstallId, Exe, HubDir, LogRoot, IconsDir, State };
            for (int i = 0; i < Keys.Length; i++)
                if (v[i] != null || had[i]) raw.Set(Keys[i], v[i]);
            if (Portable || hadPortable) raw.Set("portable", Portable);
            return raw.ToString();
        }

        /// <summary>아는 키 밖의 최상위 키를 JSON 조각 그대로 넣는다(예: skills 배열). 순서·다른 키는 그대로.</summary>
        public InstallInfo SetRaw(string key, string json) { raw.Raw(key, json); return this; }

        /// <summary>원자 교체로 쓴다(임시 파일 → rename).</summary>
        public bool Save(string path) => HubFiles.TryWriteAtomic(path, ToJson() + "\n");

        /// <summary>
        /// C-2 허브 폴더 결정: 환경변수 UART_HUB_DIR(시험·개발) → install.json 의 hub_dir → null(설치되지 않음).
        /// fromInstall = install.json 에서 얻었는가(감독의 운영 모드 판정, C-11).
        /// </summary>
        public static string ResolveHubDir(out bool fromInstall)
        {
            fromInstall = false;
            string env = Environment.GetEnvironmentVariable("UART_HUB_DIR");
            if (!string.IsNullOrEmpty(env)) return env;
            var info = Load(DefaultPath);
            if (info == null || string.IsNullOrEmpty(info.HubDir)) return null;
            fromInstall = true;
            return info.HubDir;
        }

        /// <summary>version --json: 빌드 버전, Core MVID(시험 DLL 과 같은 컴파일 결과인지 확인용), self-contained 여부, 런타임.</summary>
        public static string VersionJson(bool selfContained)
            => new JObj().Set("version", Hub.Version).Set("core_mvid", typeof(Hub).Module.ModuleVersionId.ToString())
                         .Set("self_contained", selfContained).Set("runtime", RuntimeInformation.FrameworkDescription).ToString();
    }
}
