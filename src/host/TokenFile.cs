// 제어 포트 토큰 파일 <허브 폴더>\token (install.ps1:338-352 규칙 이식).
//   없거나 16자 미만이면 32자 hex(RandomNumberGenerator 16바이트, UTF-8 BOM·줄바꿈 없음)를 새로 쓴다. 있으면 그대로 둔다(포트·CLI 가 계속 맞음).
//   ACL: 상속을 끊고(기존 상속 항목도 복사하지 않음) 현재 사용자 SID 만 FullControl — icacls /inheritance:r /grant:r *SID:(F) 를 .NET ACL API 로.
//   새 토큰은 임시 파일에 ACL 을 먼저 건 뒤 이름을 바꿔 놓는다(같은 볼륨 이동은 그 파일의 보안 설명자를 그대로 가져간다).
using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace UartHub
{
    public static class TokenFile
    {
        public const int MinLength = 16;

        /// <summary>쓸 수 있는 토큰이 이미 있는가(16자 이상).</summary>
        public static bool IsValid(string path)
        {
            string t = SystemAccess.ReadText(path);
            return t != null && t.Trim().Length >= MinLength;
        }

        /// <summary>토큰을 보장한다. 새로 만들었으면 true. 어느 경우든 ACL 을 다시 건다.</summary>
        public static bool Ensure(string path)
        {
            bool created = false;
            if (!IsValid(path))
            {
                string tmp = path + ".tmp" + Environment.ProcessId;
                string hex = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                SystemAccess.CreateDir(Path.GetDirectoryName(Path.GetFullPath(path)));
                SystemAccess.WriteText(tmp, hex);
                try
                {
                    Restrict(tmp);
                    SystemAccess.MoveFile(tmp, path, true);
                }
                catch (Exception)
                {
                    try { SystemAccess.DeleteFile(tmp); } catch (Exception) { }
                    throw;
                }
                created = true;
            }
            Restrict(path);
            return created;
        }

        /// <summary>상속 끊기 + 현재 사용자 SID 만 FullControl.</summary>
        public static void Restrict(string path)
        {
            string f = SystemAccess.Guard(path, "write");
            using var me = WindowsIdentity.GetCurrent();
            var sec = new FileSecurity();
            sec.SetAccessRuleProtection(true, false);
            sec.AddAccessRule(new FileSystemAccessRule(me.User, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(f).SetAccessControl(sec);
        }

        /// <summary>ACL 한 줄 요약(install.log·시험용): "protected=True rules=1 S-1-5-21-…:FullControl:Allow".</summary>
        public static string Describe(string path)
        {
            string f = SystemAccess.Guard(path, "read");
            var sec = new FileInfo(f).GetAccessControl(AccessControlSections.Access);
            var rules = sec.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
            return "protected=" + sec.AreAccessRulesProtected + " rules=" + rules.Count + " "
                   + string.Join(" ", rules.Select(r => r.IdentityReference.Value + ":" + r.FileSystemRights + ":" + r.AccessControlType + (r.IsInherited ? "(상속)" : "")));
        }
    }
}
