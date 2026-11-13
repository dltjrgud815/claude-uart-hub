// 바탕화면 보드 아이콘(계획 §2.3 e E1, T3 감독 쪽). 감독이 ls 스냅샷(1초)마다 Reconcile 한다.
//   원하는 집합 = status == attached 인 케이블. 이름 = "<board 또는 COMx> (<포트>)" (파일명 금지 문자만 '_' — Names.SafeLabel 은 공백도 바꾸므로 쓰지 않음).
//   바로가기: 대상 = 설치된 UartHub.exe, 인자 = "open <포트>", 설명 = "UART 허브 <이름> [uart_hub:<install_id>]".
//   소유 판정(E1): 대상·인자가 맞으면 자기 것(설명의 install_id 가 달라도 — 보조 판정, 재설치 뒤). 아니면 남의 파일이라 건드리지 않고
//     "아이콘 이름 충돌"로 남긴다(hub.log 한 줄 + 감독 상태 icon_conflicts → 트레이).
//   만들기·이름 바꾸기·지우기 뒤에는 SHChangeNotify(K6 결과 전이라 항상).
//   icons.json(%LOCALAPPDATA%\uart_hub, 감독·제거기가 씀): {"icons":[{"port","path","name","label","cable"}], …모르는 키는 그대로(C-12)}.
//   감독 기동(Startup): icons.json 의 남은 항목 + 폴더의 자기 것(보조 판정 포함)을 "현재"로 읽어 두고, 첫 스냅샷에서 현재 상태로 정리한다.
//   정지(stop) = 전부 삭제(RemoveAll). shutdown·quit 은 그대로 둔다(새 감독이 이어받음 — 감독이 Reconcile 을 부르지 않을 뿐).
//   OneDrive 동기화 바탕화면(SystemAccess.IsOneDriveDir)이면 시작 메뉴 'UART 허브' 폴더에 만든다(E3).
// 파일·바로가기·셸 알림은 모두 SystemAccess·ShellLink(가드 포함)를 거친다(C-13).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UartHub
{
    public sealed class DesktopIcons
    {
        sealed class Entry { public int Port; public string Path, Name, Label, Cable; }

        readonly HubLog log;
        readonly string exe, installId;
        readonly Dictionary<int, Entry> cur = new Dictionary<int, Entry>();
        readonly List<string> orphans = new List<string>();             // 같은 포트의 자기 것 중복(기동 때 발견) — 첫 정리에서 지움
        readonly Dictionary<int, string> conflicts = new Dictionary<int, string>();
        readonly HashSet<string> conflictLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>아이콘을 만드는 폴더(OneDrive 면 시작 메뉴\UART 허브).</summary>
        public string Dir { get; }
        /// <summary>폴더를 정한 이유(로그용).</summary>
        public string Why { get; }
        public static string ListPath => Path.Combine(SystemAccess.InstallDir, "icons.json");
        /// <summary>남의 같은 이름 파일 때문에 만들지 못한 아이콘 이름.</summary>
        public string[] Conflicts => conflicts.Values.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        public int Count => cur.Count;

        /// <param name="dir">--icons-dir 또는 바탕화면.</param>
        /// <param name="exe">바로가기 대상(설치된 UartHub.exe).</param>
        public DesktopIcons(string dir, string exe, string installId, HubLog log)
        {
            this.log = log;
            this.exe = exe;
            this.installId = string.IsNullOrEmpty(installId) ? "dev" : installId;
            if (SystemAccess.IsOneDriveDir(dir, out string why))
            {
                Dir = Path.Combine(SystemAccess.StartMenuPrograms, "UART 허브");
                Why = L.T("OneDrive 바탕화면(" + why + ") → 시작 메뉴 " + Dir, "OneDrive desktop (" + why + ") → Start menu " + Dir);
            }
            else { Dir = Path.GetFullPath(dir); Why = L.T("바탕화면 " + Dir, "desktop " + Dir); }
        }

        // ================================================================== 이름
        /// <summary>창 제목·아이콘 이름의 앞부분: 보드명, 없으면 COM, 둘 다 없으면 "UART".</summary>
        public static string LabelOf(string board, string com) => !string.IsNullOrWhiteSpace(board) ? board.Trim() : !string.IsNullOrWhiteSpace(com) ? com.Trim() : "UART";

        public static string NameOf(string label, int port) => label + " (" + port.ToString(CultureInfo.InvariantCulture) + ")";

        /// <summary>파일 이름: 금지 문자(\ / : * ? " &lt; &gt; |, 제어 문자)만 '_', 끝의 점·공백 제거.</summary>
        public static string FileNameOf(string name)
        {
            string s = Regex.Replace(name ?? "", "[\\\\/:*?\"<>|\\x00-\\x1f]", "_").TrimEnd('.', ' ');
            return (s.Length == 0 ? "_" : s) + ".lnk";
        }

        string MarkerDesc(string name) => L.T("UART 허브 " + name + " " + ShellLink.Marker(installId), "UART Hub " + name + " " + ShellLink.Marker(installId));

        /// <summary>icons.json 의 그 포트 label(open 의 창 제목용). 없으면 null.</summary>
        public static string LabelFromList(int port)
        {
            foreach (var e in ReadList(out _))
                if (e.Port == port) return e.Label ?? Regex.Replace(e.Name ?? "", @" \(\d+\)$", "");
            return null;
        }

        static List<Entry> ReadList(out JObj raw)
        {
            raw = new JObj();
            var l = new List<Entry>();
            string t = SystemAccess.ReadText(ListPath);
            if (string.IsNullOrWhiteSpace(t)) return l;
            try
            {
                using var d = JsonDocument.Parse(t);
                if (d.RootElement.ValueKind != JsonValueKind.Object) return l;
                raw = HubFiles.ToJObj(d.RootElement);
                if (d.RootElement.TryGetProperty("icons", out var a) && a.ValueKind == JsonValueKind.Array)
                    foreach (var e in a.EnumerateArray())
                        if (e.ValueKind == JsonValueKind.Object)
                            l.Add(new Entry { Port = (int)HubFiles.Long(e, "port"), Path = HubFiles.Str(e, "path"), Name = HubFiles.Str(e, "name"),
                                              Label = HubFiles.Str(e, "label"), Cable = HubFiles.Str(e, "cable") });
            }
            catch (JsonException) { }
            return l;
        }

        // ================================================================== 소유 판정(E1)
        ShellLinkInfo Read(string path) => SystemAccess.FileExists(path) ? ShellLink.Read(path) : null;

        bool Ours(ShellLinkInfo i, int port) => ShellLink.IsOurs(i, exe, "open " + port.ToString(CultureInfo.InvariantCulture));

        /// <summary>args 가 "open &lt;p&gt;" 이고 대상이 exe 인 자기 것이면 그 포트, 아니면 0.</summary>
        int OursPort(ShellLinkInfo i)
        {
            if (i == null || !SystemAccess.SamePath(i.Target, exe)) return 0;
            var m = Regex.Match((i.Arguments ?? "").Trim(), @"^open (\d{1,5})$");
            return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int p) ? p : 0;
        }

        // ================================================================== 기동
        /// <summary>감독 기동 때: icons.json 의 남은 항목과 폴더의 자기 아이콘(보조 판정)을 "현재"로 읽는다. 지우거나 고치는 것은 첫 Reconcile·RemoveAll 이 한다.</summary>
        public void Startup()
        {
            log.Write(L.T("아이콘: " + Why + " · 대상 " + exe + " · install_id " + installId, "icons: " + Why + " · target " + exe + " · install_id " + installId));
            int listed = 0, found = 0, dropped = 0;
            foreach (var e in ReadList(out _))
            {
                if (e.Port <= 0 || string.IsNullOrEmpty(e.Path)) { dropped++; continue; }
                var i = Read(e.Path);
                if (i == null) { dropped++; continue; }                                    // 이미 없음
                if (!Ours(i, e.Port)) { dropped++; log.Write(L.T("아이콘: icons.json 항목이 자기 것이 아님(대상 " + i.Target + " " + i.Arguments + ") — 건드리지 않음: " + e.Path, "icons: icons.json entry is not ours (target " + i.Target + " " + i.Arguments + ") — left alone: " + e.Path)); continue; }
                Adopt(e);
                listed++;
            }
            foreach (var f in SystemAccess.ListFiles(Dir, "*.lnk"))
            {
                if (cur.Values.Any(x => SystemAccess.SamePath(x.Path, f))) continue;
                int p = OursPort(ShellLink.Read(f));
                if (p <= 0) continue;
                found++;
                Adopt(new Entry { Port = p, Path = f, Name = Path.GetFileNameWithoutExtension(f) });
            }
            log.Write(L.T("아이콘: 기동 정리 대상 — icons.json " + listed + "개 · 폴더의 자기 것(보조 판정) " + found + "개 · 버린 항목 " + dropped + "개" + (orphans.Count > 0 ? " · 중복 " + orphans.Count + "개" : ""),
                          "icons: to tidy at startup — icons.json " + listed + " · ours in the folder (secondary check) " + found + " · dropped entries " + dropped + (orphans.Count > 0 ? " · duplicates " + orphans.Count : "")));
        }

        void Adopt(Entry e)
        {
            if (cur.TryGetValue(e.Port, out var have) && !SystemAccess.SamePath(have.Path, e.Path)) orphans.Add(e.Path);
            else cur[e.Port] = e;
        }

        // ================================================================== 맞추기
        /// <summary>ls 스냅샷(attached 케이블들)에 맞춘다. 바뀐 것이 있으면 icons.json 을 쓴다.</summary>
        public void Reconcile(IEnumerable<CableRow> rows)
        {
            bool changed = false;
            foreach (var o in orphans.ToList())
            {
                if (OursPort(Read(o)) > 0) { Delete(o, L.T("중복", "duplicate")); changed = true; }
                orphans.Remove(o);
            }
            var want = new Dictionary<int, CableRow>();
            foreach (var r in rows)
                if (r != null && r.Status == "attached" && r.Port > 0) want[r.Port] = r;
            foreach (var port in cur.Keys.Where(p => !want.ContainsKey(p)).ToList())
            {
                var e = cur[port];
                cur.Remove(port);
                DeleteIfOurs(e, L.T("분리", "detached"));                                // 자기 것이 아니면 목록에서만 뺀다
                changed = true;
            }
            foreach (var port in conflicts.Keys.Where(p => !want.ContainsKey(p)).ToList()) conflicts.Remove(port);
            foreach (var kv in want.OrderBy(x => x.Key))
                if (Ensure(kv.Value)) changed = true;
            if (changed) Save();
        }

        /// <summary>그 케이블의 아이콘이 원하는 이름·내용으로 있게 한다. 바꿨으면 true.</summary>
        bool Ensure(CableRow r)
        {
            int port = r.Port;
            string label = LabelOf(r.Board, r.Com), name = NameOf(label, port);
            string path = Path.Combine(Dir, FileNameOf(name));
            cur.TryGetValue(port, out var e);
            if (e != null && SystemAccess.SamePath(e.Path, path))
            {
                e.Label = label; e.Cable = r.Cable;
                if (SystemAccess.FileExists(path)) return false;               // 그대로(매 초 내용은 읽지 않는다 — 바뀔 때만 E1 확인)
                Create(path, port, name, L.T("다시 만듦(파일이 없어짐)", "recreated (file was gone)"));
                return true;
            }
            // 원하는 이름에 이미 파일이 있다: 자기 것이면 이어받고(설명의 install_id 가 다르면 고침), 남의 것이면 충돌
            if (SystemAccess.FileExists(path))
            {
                var i = ShellLink.Read(path);
                if (!Ours(i, port))
                {
                    if (!conflicts.ContainsKey(port) || conflicts[port] != name)
                    {
                        conflicts[port] = name;
                        if (conflictLogged.Add(path)) log.Write(L.T("아이콘 이름 충돌: " + path + " 은 남의 파일(대상 " + (i?.Target ?? "?") + " " + (i?.Arguments ?? "") + ") — 만들지 않음", "icon name conflict: " + path + " is someone else's file (target " + (i?.Target ?? "?") + " " + (i?.Arguments ?? "") + ") — not created"));
                    }
                    return false;
                }
                conflicts.Remove(port);
                if (e != null && !SystemAccess.SamePath(e.Path, path)) { cur.Remove(port); DeleteIfOurs(e, L.T("이름 바뀜(새 이름에 자기 것이 이미 있음)", "renamed (ours already exists under the new name)")); }
                bool iconOk = SystemAccess.SamePath(i.IconPath, exe) && i.IconIndex == 0;   // 아이콘 모양 = exe 아이콘(IconLocation exe,0)
                if ((i.Description ?? "") != MarkerDesc(name) || !iconOk)
                {
                    Save(path, port, name);
                    string notify = SystemAccess.ShellNotify(SystemAccess.ShcneUpdateItem, path);
                    log.Write(L.T("아이콘 이어받음(" + (iconOk ? "설명 고침 — install_id 다름·보조 판정" : "아이콘 모양 고침 — exe,0") + "): " + Path.GetFileName(path) + " (SHChangeNotify " + notify + ")",
                                  "icon adopted (" + (iconOk ? "description fixed — different install_id or language, secondary check" : "icon image fixed — exe,0") + "): " + Path.GetFileName(path) + " (SHChangeNotify " + notify + ")"));
                }
                else log.Write(L.T("아이콘 이어받음: " + Path.GetFileName(path), "icon adopted: " + Path.GetFileName(path)));
                cur[port] = new Entry { Port = port, Path = path, Name = name, Label = label, Cable = r.Cable };
                return true;
            }
            conflicts.Remove(port);
            // 이름 바뀜: 옛 파일이 자기 것이면 옮긴다(내용은 새 이름으로 다시 씀)
            if (e != null && SystemAccess.FileExists(e.Path) && Ours(Read(e.Path), port))
            {
                Retry(() => SystemAccess.MoveFile(e.Path, path, false));
                Save(path, port, name);
                string notify = SystemAccess.ShellNotify(SystemAccess.ShcneRenameItem, e.Path, path);
                log.Write(L.T("아이콘 이름 바꿈: " + Path.GetFileName(e.Path) + " → " + Path.GetFileName(path) + " (SHChangeNotify " + notify + ")",
                              "icon renamed: " + Path.GetFileName(e.Path) + " → " + Path.GetFileName(path) + " (SHChangeNotify " + notify + ")"));
                cur[port] = new Entry { Port = port, Path = path, Name = name, Label = label, Cable = r.Cable };
                return true;
            }
            if (e != null) log.Write(L.T("아이콘: 옛 파일이 없거나 자기 것이 아님 — 남김: " + e.Path, "icons: old file missing or not ours — left: " + e.Path));
            Create(path, port, name, null);
            cur[port] = new Entry { Port = port, Path = path, Name = name, Label = label, Cable = r.Cable };
            return true;
        }

        /// <summary>
        /// 바로가기를 원자적으로 쓴다: 설치 폴더의 임시 파일에 저장한 뒤 그 자리로 옮긴다(덮어씀). 쓰다 만 .lnk 가 바탕화면에 남으면
        /// 다음 기동이 자기 것으로 알아보지 못해(E1) "충돌"로 남기 때문이다. 탐색기도 완성된 파일만 본다.
        /// </summary>
        void Save(string path, int port, string name)
        {
            string tmp = Path.Combine(SystemAccess.InstallDir, "icon_" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp.lnk");
            ShellLink.Save(tmp, new ShellLinkInfo
            {
                Target = exe, Arguments = "open " + port.ToString(CultureInfo.InvariantCulture), WorkingDirectory = Path.GetDirectoryName(exe),
                Description = MarkerDesc(name), IconPath = exe, IconIndex = 0,
            });
            Retry(() => SystemAccess.MoveFile(tmp, path, true));
        }

        /// <summary>탐색기(또는 다른 프로그램)가 .lnk 를 잠깐 열고 있으면 옮기기·지우기가 거부된다 — 100ms 간격 10회까지 다시 한다.</summary>
        static void Retry(Action a)
        {
            for (int i = 0; ; i++)
            {
                try { a(); return; }
                catch (Exception e) when (i < 9 && (e is IOException || e is UnauthorizedAccessException)) { System.Threading.Thread.Sleep(100); }
            }
        }

        void Create(string path, int port, string name, string note)
        {
            SystemAccess.CreateDir(Dir);
            Save(path, port, name);
            string notify = SystemAccess.ShellNotify(SystemAccess.ShcneCreate, path);
            log.Write(L.T("아이콘 만듦: " + Path.GetFileName(path) + " → open " + port + (note != null ? " (" + note + ")" : "") + " (SHChangeNotify " + notify + ")",
                          "icon created: " + Path.GetFileName(path) + " → open " + port + (note != null ? " (" + note + ")" : "") + " (SHChangeNotify " + notify + ")"));
        }

        bool DeleteIfOurs(Entry e, string why)
        {
            var i = Read(e.Path);
            if (i == null) return false;
            if (!Ours(i, e.Port)) { log.Write(L.T("아이콘: " + e.Path + " 은 자기 것이 아님(대상 " + i.Target + " " + i.Arguments + ") — 지우지 않음", "icons: " + e.Path + " is not ours (target " + i.Target + " " + i.Arguments + ") — not deleted")); return false; }
            Delete(e.Path, why);
            return true;
        }

        void Delete(string path, string why)
        {
            Retry(() => SystemAccess.DeleteFile(path));
            string notify = SystemAccess.ShellNotify(SystemAccess.ShcneDelete, path);
            log.Write(L.T("아이콘 지움(" + why + "): " + Path.GetFileName(path) + " (SHChangeNotify " + notify + ")", "icon deleted (" + why + "): " + Path.GetFileName(path) + " (SHChangeNotify " + notify + ")"));
        }

        /// <summary>정지(stop): 자기 아이콘을 모두 지운다(목록 + 폴더의 자기 것). 남의 파일은 그대로.</summary>
        public void RemoveAll(string why)
        {
            int n = 0;
            foreach (var e in cur.Values.ToList()) if (DeleteIfOurs(e, why)) n++;
            foreach (var o in orphans) if (OursPort(Read(o)) > 0) { Delete(o, why); n++; }
            foreach (var f in SystemAccess.ListFiles(Dir, "*.lnk"))
                if (OursPort(ShellLink.Read(f)) > 0) { Delete(f, why); n++; }
            bool had = cur.Count > 0 || orphans.Count > 0 || n > 0;
            cur.Clear(); orphans.Clear(); conflicts.Clear();
            if (had) Save();
            log.Write(L.T("아이콘 전부 지움(" + why + "): " + n + "개", "all icons deleted (" + why + "): " + n));
        }

        /// <summary>icons.json(C-12: 읽은 원문의 모르는 키는 그대로, icons 만 바꾼다).</summary>
        void Save()
        {
            ReadList(out var raw);
            raw.Set("icons", cur.Values.OrderBy(x => x.Port).Select(x => new JObj().Set("port", (long)x.Port).Set("path", x.Path).Set("name", x.Name)
                                                                                     .Set("label", x.Label).Set("cable", x.Cable)));
            SystemAccess.CreateDir(SystemAccess.InstallDir);
            SystemAccess.WriteText(ListPath, raw.ToString() + "\n");
        }
    }
}
