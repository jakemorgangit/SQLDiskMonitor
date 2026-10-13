using System.Runtime.InteropServices;
using System.Text;

namespace SQLDiskMonitor.Services;

/// <summary>Stores SQL / Entra passwords in Windows Credential Manager, keyed by server id.</summary>
public static class CredentialStore
{
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;
    private const string Prefix = "SQLDiskMonitor:v2:";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    public static void SavePassword(long serverId, string username, string password)
    {
        byte[] blob = Encoding.Unicode.GetBytes(password);
        var c = new CREDENTIAL
        {
            Type = CRED_TYPE_GENERIC,
            TargetName = Prefix + serverId,
            UserName = username,
            CredentialBlobSize = blob.Length,
            CredentialBlob = Marshal.AllocHGlobal(Math.Max(1, blob.Length)),
            Persist = CRED_PERSIST_LOCAL_MACHINE,
            Comment = "SQL Disk Monitor"
        };
        try
        {
            Marshal.Copy(blob, 0, c.CredentialBlob, blob.Length);
            if (!CredWrite(ref c, 0))
                throw new InvalidOperationException($"Could not save the password to Windows Credential Manager (error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            Marshal.FreeHGlobal(c.CredentialBlob);
        }
    }

    public static string LoadPassword(long serverId)
    {
        if (!CredRead(Prefix + serverId, CRED_TYPE_GENERIC, 0, out IntPtr ptr)) return "";
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            return c.CredentialBlobSize > 0 ? Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2) ?? "" : "";
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public static void Delete(long serverId) => CredDelete(Prefix + serverId, CRED_TYPE_GENERIC, 0);
}
