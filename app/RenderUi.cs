// 숨은 개발용 하위 명령 `UartHub.exe render-ui <폴더>`(UI_COPY.md 검토): 모든 창과 트레이 메뉴 모형을 PNG 로 그린다.
//   창을 띄우지 않는다 — Show·ShowDialog 없이 화면 밖 위치에서 핸들만 만들고 DrawToBitmap(HubDialog.Snapshot). 허브·감독·설정 파일은 건드리지 않는다(예시 데이터만).
//   DPI: 프로세스는 DpiUnaware(늘 96 DPI 로 그림) → 100% 는 그대로, 150% 는 Zoom 1.5(글꼴·픽셀 값 1.5배)로 흉내 낸다.
//   테마: 레지스트리를 읽지 않고 Theme.Forced 로 밝게·어둡게를 정해 둘 다 그린다(창·메뉴가 같은 Palette).
//   출력: <폴더>\NN_<이름>_<light|dark>@100.png · @150.png. 트레이 메뉴는 주 메뉴 + COM 포트 ▸ + 그 COM ▸(10), 주 메뉴 + 설정 ▸ + 터미널 프로그램 ▸(11)을
//         계단식으로 붙이고, 윈도 11 처럼 모서리를 둥글게 깎아 테두리·그림자를 얹는다(실제 메뉴는 DWM 이 그린다). 13 에이징 시작 창 · 14 에이징 중 트레이 메뉴(세션 정보 줄·COM ▸ 에이징 끝내기)
//         · 15 트레이 아이콘 4종(run·rec·warn·stop, 16·24·32px). 만든 파일 경로를 한 줄씩 돌려준다.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace UartHub.App
{
    static class RenderUi
    {
        const string Exe = @"C:\Tools\UartHub_20261106101724547\UartHub.exe";
        const string OldExe = @"C:\Tools\UartHub_20261106030213432\UartHub.exe";
        const string LogRoot = @"D:\uart_logs", HubDir = @"D:\uart_logs\hub", InstJson = @"C:\Users\me\AppData\Local\uart_hub\install.json";

        public static List<string> Run(string outDir)
        {
            Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Directory.CreateDirectory(outDir);
            var made = new List<string>();
            try
            {
                foreach (bool dark in new[] { false, true })
                    foreach (float z in new[] { 1f, 1.5f })
                    {
                        Theme.Forced = dark;
                        RenderSet(outDir, (dark ? "_dark" : "_light") + (z == 1f ? "@100" : "@150"), z, made);
                    }
            }
            finally { Theme.Forced = null; }
            return made;
        }

        static void RenderSet(string outDir, string tag, float z, List<string> made)
        {
            void Save(string name, Bitmap b)
            {
                string p = Path.Combine(outDir, name + tag + ".png");
                b.Save(p, ImageFormat.Png);
                b.Dispose();
                made.Add(p);
            }
            void Dlg(string name, HubDialog d, bool expand = false)
            {
                using (d)
                {
                    if (expand) d.Expand(true);
                    Save(name, d.Snapshot());
                }
            }

            // 1 처음 시작(휴대용 첫 실행) — 접힌 모습 + 자세히 펼친 모습
            var first = InstallCopy.FirstRun(LogRoot, InstallCopy.SkillsPlaces(true, new[] { "Ubuntu-22.04" }), Exe, HubDir, InstJson);
            Dlg("01_first_confirm", new ConfirmWindow(first, z));
            Dlg("01b_first_confirm_details", new ConfirmWindow(first, z), true);

            // 2 켜는 중(2단계 진행 중, 자세히 펼침)
            var prog = new ProgressWindow(Installer.PortableTitle, z);
            prog.SetSteps(InstallCopy.FirstHead, InstallCopy.StepsFirst);
            prog.SetStage(1);
            foreach (var l in SampleLog()) prog.AddDetail(l);
            Dlg("02_progress", prog);
            prog = new ProgressWindow(Installer.PortableTitle, z);
            prog.SetSteps(InstallCopy.FirstHead, InstallCopy.StepsFirst);
            prog.SetStage(1);
            foreach (var l in SampleLog()) prog.AddDetail(l);
            Dlg("02b_progress_details", prog, true);

            // 3 준비됨 · 4 켜지 못함
            var ok = new ProgressWindow(Installer.PortableTitle, z);
            foreach (var l in SampleLog()) ok.AddDetail(l);
            ok.ShowResult(true, InstallCopy.Ready(LogRoot, "Windows 넣음, WSL Ubuntu-22.04 넣음"), 5000);
            Dlg("03_success", ok);
            var bad = new ProgressWindow(Installer.PortableTitle, z);
            foreach (var l in SampleLog()) bad.AddDetail(l);
            bad.ShowResult(false, InstallCopy.Fail("건강 확인 실패", HubDir + @"\install.log", null), 0);
            Dlg("04_failure", bad);

            // 5 새 버전으로 바꾸기
            Dlg("05_replace_confirm", new ConfirmWindow(InstallCopy.Replace(OldExe, "20261106030213432", "20261106101724547", Exe), z));

            // 6 COM 연결 확인(MoTTY) · 7 안내(MobaXterm 본체)
            var motty = new ComHolder { Name = "MoTTY.exe", Pid = 88688 };
            var confirm = new ConnectForm(ConnectFlow.ConfirmText("COM8", motty), 0, _ => { }, z);
            confirm.Pose(0, true);
            Dlg("06_connect_confirm", confirm);
            var moba = new ComHolder { Name = "MobaXterm.exe", Pid = 40112 };
            var guide = new ConnectForm(ConnectFlow.GuideText("COM8", moba), 120000, _ => { }, z);
            guide.Pose(118000, false);
            Dlg("07_connect_guide", guide);

            // 8 첫 연결 질문(on_demand 꺼짐)
            var row = new CableRow { Cable = "FT9KX2AB", Com = "COM9", Status = "pending", Description = "USB Serial Port (FTDI)" };
            Dlg("08_first_connect", new FirstConnectForm(row, 0, (a, b, c) => { }, z));

            // 9 제거 확인(같은 모양)
            Dlg("09_uninstall_confirm", new ConfirmWindow(InstallCopy.Uninstall(Exe, LogRoot, HubDir, InstJson), z));

            // 12 미연결 UART 정리(목록 3줄 — 하나는 보드·COM 모름) · 빈 목록 · 허브가 꺼짐
            var gone = new List<RememberedCable>
            {
                new RememberedCable { Id = "6&1A2B3C4D&0&2", Port = 7002, Board = "EVB-B2", Com = "COM5" },
                new RememberedCable { Id = "6&1A2B3C4D&0&3", Port = 7003, Board = "EVB-C3", Com = "COM6" },
                new RememberedCable { Id = "6&1A2B3C4D&0&4", Port = 7004 },
            };
            Dlg("12_uart_cleanup", new CleanupForm(gone, null, (ids, done) => { }, z));
            Dlg("12b_uart_cleanup_empty", new CleanupForm(new List<RememberedCable>(), null, (ids, done) => { }, z));
            Dlg("12c_uart_cleanup_hub_off", new CleanupForm(null, "허브가 꺼져 있어 기록을 읽지 못했습니다.", (ids, done) => { }, z));

            // 10·11 트레이 메뉴 모형
            Save("10_tray_menu", TrayMock(z, false));
            Save("11_tray_settings", TrayMock(z, true));

            // 13 에이징 시작(이름 창, 부록 C.12) · 14 에이징 중 트레이 메뉴(세션 정보 줄 2개 — 하나는 10분 넘게 출력 없음, COM3 ▸ 에이징 끝내기) · 15 트레이 아이콘 4종
            Dlg("13_aging_start", new AgingForm("EVB-A1 (7001)", "EVB-A1_20261106-1530", LogRoot + @"\에이징", new DateTime(2026, 11, 6, 15, 30, 0), _ => { }, z));
            Save("14_tray_aging", TrayMock(z, false, true));
            Save("15_tray_icons", IconStrip(z));
        }

        static IEnumerable<string> SampleLog() => new[]
        {
            "# UART 허브 시작(휴대용) v20261106101724547",
            "# exe=" + Exe + " pid=21480 사용자=user",
            "",
            "== 3. 허브 폴더·토큰·config.json",
            "  폴더 " + HubDir,
            "  토큰 " + HubDir + @"\token 새로 만듦(32자 hex) + ACL(상속 끊기, 현재 사용자만 FullControl)",
            "  config.json 새로 만듦(휴대용 기본): {\"on_demand\": true}",
            "",
            "== 4. install.json(portable)",
            "  install.json state=installed portable=true (exe=" + Exe + ")",
            "",
            "== 5. 감독 기동 → 건강 확인",
            "  \"" + Exe + "\" sup 분리 기동(이 exe 그대로)",
            "    건강 확인 OK 2.4초 (합격선 10초 이내)",
            "",
            "== 6. Claude skills",
            "  Claude skills 배치(~/.claude 가 있는 곳만)",
        };

        /// <summary>
        /// 트레이 메뉴(실제 ContextMenuStrip 을 화면 밖에서 그림): settings = false 면 주 메뉴 · COM 포트 ▸ · 연결된 COM ▸,
        /// true 면 주 메뉴 · 설정 ▸ · 터미널 프로그램 ▸ 를 계단식으로. 열린 하위 메뉴의 부모 줄은 마우스를 올린 모습.
        /// </summary>
        static Bitmap TrayMock(float z, bool settings, bool aging = false)
        {
            using var tm = new TrayMenu(z);
            tm.Style.Preview = true;
            var rows = new List<CableRow>
            {
                new CableRow { Cable = "FT6X1A2B", Com = "COM3", Port = 7001, Board = "EVB-A1", Status = "attached", Baud = 115200, Description = "USB Serial Port",
                               SessionName = aging ? "EVB-A1_전원에이징_1106" : null, SessionElapsedS = 2 * 3600 + 13 * 60 + 20, SessionLastRxAgeS = 3 },
                new CableRow { Cable = "FT6X1C3D", Com = "COM5", Port = 7002, Board = "EVB-B2", Status = "attached", Baud = 115200, Description = "USB Serial Port",
                               SessionName = aging ? "EVB-B2_20261106-0910" : null, SessionElapsedS = 7 * 3600 + 2 * 60, SessionLastRxAgeS = 12 * 60 + 5 },
                new CableRow { Cable = "FT6X1G5H", Com = "COM6", Port = 7003, Board = "EVB-C3", Status = "attached", Baud = 115200, Description = "USB Serial Port" },
                new CableRow { Cable = "FT6X1J6K", Com = "COM7", Port = 7004, Board = "H1", Status = "attached", Baud = 115200, Description = "USB Serial Port" },
                new CableRow { Cable = "FT6X1E4F", Com = "COM8", Port = 0, Status = "pending", Baud = 115200, Description = "USB Serial Port" },
            };
            tm.SetStatus(Supervisor.TitleOf("running", false, false, 4, 0, 0, rows.Count(r => r.Aging)), rows);
            tm.SetRunState(false, "running");
            var a = new TrayActions();
            tm.FillComs(rows, true, true, new HashSet<string>(), a);
            tm.FillTerminals("auto", new List<(string, string)> { ("teraterm", @"C:\Program Files (x86)\teraterm5\ttermpro.exe"), ("putty", @"C:\Program Files\PuTTY\putty.exe") }, a);
            var pal = tm.Style.Pal;
            ToolStripMenuItem parent = settings ? tm.Settings : tm.Coms;
            var child = settings ? tm.Terms : (ToolStripMenuItem)tm.Coms.DropDownItems[0];
            ToolStripDropDown[] menus = { tm.Menu, parent.DropDown, child.DropDown };
            foreach (var m in menus) Layout(m);
            parent.Select();
            child.Select();
            var shots = menus.Select(Shot).ToList();
            // 계단: 부모 줄과 하위 메뉴의 첫 줄이 같은 높이, 하위 메뉴는 부모 메뉴 오른쪽에 2px 띄워
            int pad = (int)(28 * z), gap = (int)Math.Round(2 * z);
            var p0 = new Point(pad, pad);
            var p1 = new Point(p0.X + shots[0].Width + gap, p0.Y + parent.Bounds.Top - parent.DropDown.Items[0].Bounds.Top);
            var p2 = new Point(p1.X + shots[1].Width + gap, p1.Y + child.Bounds.Top - child.DropDown.Items[0].Bounds.Top);
            int w = p2.X + shots[2].Width + pad;
            int h = Math.Max(p0.Y + shots[0].Height, Math.Max(p1.Y + shots[1].Height, p2.Y + shots[2].Height)) + pad;
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(pal.Backdrop);
                for (int i = 0; i < 3; i++) Paste(g, shots[i], new[] { p0, p1, p2 }[i], z, pal);
            }
            foreach (var s in shots) s.Dispose();
            return bmp;
        }

        /// <summary>트레이 아이콘 4종(실행·에이징 기록 중·경고·정지)을 16·24·32px(배율 반영)로 테마 바탕 위에 나란히 — 작은 크기에서도 기록 점이 보이는지 확인용.</summary>
        static Bitmap IconStrip(float z)
        {
            var pal = Theme.Current;
            string[] kinds = { "run", "rec", "warn", "stop" };
            int[] sizes = { 16, 24, 32 };
            int pad = (int)(16 * z), gap = (int)(14 * z), cell = (int)(32 * z) + gap;
            using var font = new Font("Segoe UI", 8.5f * z);
            int labelH = (int)(18 * z);
            var bmp = new Bitmap(pad * 2 + cell * kinds.Length, pad * 2 + labelH + sizes.Sum(s => (int)(s * z) + gap), PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(pal.MenuBack);
                for (int i = 0; i < kinds.Length; i++)
                {
                    int x = pad + i * cell;
                    TextRenderer.DrawText(g, kinds[i], font, new Rectangle(x, pad, cell, labelH), pal.Text, TextFormatFlags.Left | TextFormatFlags.NoPrefix);
                    int y = pad + labelH;
                    foreach (int s in sizes)
                    {
                        int px = (int)(s * z);
                        using (var ico = Theme.Icon(TrayIcons.FileOf(kinds[i]), new Size(px, px))) g.DrawIcon(ico, new Rectangle(x, y, px, px));
                        y += px + gap;
                    }
                }
            }
            return bmp;
        }

        static void Layout(ToolStripDropDown m)
        {
            _ = m.Handle;
            m.PerformLayout();
            m.Size = m.GetPreferredSize(Size.Empty);
            m.PerformLayout();
        }

        /// <summary>윈도 11 메뉴 흉내: 부드러운 그림자 → 둥근 모서리(8px)로 깎은 메뉴 → 1px 테두리.</summary>
        static void Paste(Graphics g, Bitmap b, Point at, float z, Palette pal)
        {
            float r = 8 * z;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            int layers = (int)(10 * z);
            for (int i = layers; i >= 1; i--)
            {
                var sr = new RectangleF(at.X - i * 0.6f, at.Y - i * 0.3f + 2 * z, b.Width + i * 1.2f, b.Height + i * 1.2f);
                using var path = Theme.Round(sr, r + i);
                using var br = new SolidBrush(Color.FromArgb(pal.Dark ? 9 : 5, 0, 0, 0));
                g.FillPath(br, path);
            }
            using (var path = Theme.Round(new RectangleF(at.X, at.Y, b.Width, b.Height), r))
            using (var tb = new TextureBrush(b))
            {
                tb.TranslateTransform(at.X, at.Y);
                g.FillPath(tb, path);
            }
            using (var path = Theme.Round(new RectangleF(at.X + 0.5f, at.Y + 0.5f, b.Width - 1f, b.Height - 1f), r - 0.5f))
            using (var pen = new Pen(pal.Border, 1f))
                g.DrawPath(pen, path);
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.Default;
        }

        static Bitmap Shot(ToolStripDropDown m)
        {
            var b = new Bitmap(m.Width, m.Height, PixelFormat.Format32bppArgb);
            m.DrawToBitmap(b, new Rectangle(0, 0, m.Width, m.Height));
            return b;
        }
    }
}
