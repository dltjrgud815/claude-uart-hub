// 바로가기(.lnk) 만들기·읽기: IShellLinkW + IPersistFile ComImport(계획 T2·T3 공용 — 시작프로그램·시작 메뉴·바탕화면 아이콘).
// 소유 판정(E1·P3): 설명에 "[uart_hub:<install_id>]" 표시를 넣는다. 고치거나 지우기 전에는 대상 = 설치한 UartHub.exe 이고
// 인자가 기대값인지 확인한다(설치 id 가 바뀌어도 경로·인자가 맞으면 자기 것 — 보조 판정).
// 쓰기·읽기 경로는 SystemAccess.Guard 를 거친다(C-13). ShellLink 는 ThreadingModel=Both 라 MTA 스레드에서도 만들 수 있다.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace UartHub
{
    public sealed class ShellLinkInfo
    {
        public string Target, Arguments, WorkingDirectory, Description, IconPath;
        public int IconIndex;
    }

    public static class ShellLink
    {
        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class CShellLink { }

        const int Max = 2048;
        const uint StgmRead = 0;

        public const string MarkerPrefix = "[uart_hub:";
        public static string Marker(string installId) => MarkerPrefix + installId + "]";

        /// <summary>.lnk 를 만든다(있으면 덮어씀). 폴더가 없으면 만든다.</summary>
        public static void Save(string lnkPath, ShellLinkInfo info)
        {
            string path = SystemAccess.Guard(lnkPath, "write");
            SystemAccess.CreateDir(Path.GetDirectoryName(path));
            var sl = (IShellLinkW)new CShellLink();
            try
            {
                sl.SetPath(info.Target);
                sl.SetArguments(info.Arguments ?? "");
                if (!string.IsNullOrEmpty(info.WorkingDirectory)) sl.SetWorkingDirectory(info.WorkingDirectory);
                if (!string.IsNullOrEmpty(info.Description)) sl.SetDescription(info.Description);
                if (!string.IsNullOrEmpty(info.IconPath)) sl.SetIconLocation(info.IconPath, info.IconIndex);
                ((IPersistFile)sl).Save(path, true);
            }
            finally { Marshal.FinalReleaseComObject(sl); }
        }

        /// <summary>.lnk 를 읽는다. 없거나 읽지 못하면 null.</summary>
        public static ShellLinkInfo Read(string lnkPath)
        {
            string path = SystemAccess.Guard(lnkPath, "read");
            if (!SystemAccess.FileExists(path)) return null;
            object o = null;
            try
            {
                o = new CShellLink();
                ((IPersistFile)o).Load(path, (int)StgmRead);
                var sl = (IShellLinkW)o;
                var sb = new StringBuilder(Max);
                var r = new ShellLinkInfo();
                sl.GetPath(sb, Max, IntPtr.Zero, 0); r.Target = sb.ToString(); sb.Clear();
                sl.GetArguments(sb, Max); r.Arguments = sb.ToString(); sb.Clear();
                sl.GetWorkingDirectory(sb, Max); r.WorkingDirectory = sb.ToString(); sb.Clear();
                sl.GetDescription(sb, Max); r.Description = sb.ToString(); sb.Clear();
                sl.GetIconLocation(sb, Max, out r.IconIndex); r.IconPath = sb.ToString();
                return r;
            }
            catch (Exception) { return null; }
            finally { if (o != null) Marshal.FinalReleaseComObject(o); }
        }

        /// <summary>
        /// 자기 것인가: 대상이 exe 이고 인자가 args(argsPrefix 면 그것으로 시작)와 같다. 설명의 [uart_hub:…] 표시는 install_id 가 바뀌어도
        /// 경로·인자가 맞으면 자기 것으로 보는 보조 판정 때문에 필수 조건이 아니다(E1).
        /// </summary>
        public static bool IsOurs(ShellLinkInfo i, string exe, string args, bool argsPrefix = false)
        {
            if (i == null || !SystemAccess.SamePath(i.Target, exe)) return false;
            string a = (i.Arguments ?? "").Trim();
            return argsPrefix ? a.StartsWith(args, StringComparison.Ordinal) : a == args;
        }
    }
}
