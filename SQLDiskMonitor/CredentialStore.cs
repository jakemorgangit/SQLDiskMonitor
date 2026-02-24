using System.Runtime.InteropServices;
using System.Text;

namespace SQLDiskMonitor;

public static class CredentialStore
{
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;
    private const string Prefix = "SQLDiskMonitor:";

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

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredEnumerate(string filter, int flags, out int count, out IntPtr credentials);

    /// <summary>
    /// Encodes ServerEntry metadata into a pipe-delimited string stored in the UserName field.
    /// Format: serverAddress|authType|sqlUser|trustCert|encrypt|timeout
    /// </summary>
    private static string Encode(ServerEntry entry)
    {
        string auth = entry.WindowsAuth ? "WIN" : "SQL";
        string user = entry.WindowsAuth ? "" : entry.Username;
        return $"{entry.ServerAddress}|{auth}|{user}|{(entry.TrustCertificate ? "1" : "0")}|{(entry.Encrypt ? "1" : "0")}|{entry.Timeout}";
    }

    private static ServerEntry Decode(string displayName, string encoded, string password)
    {
        var entry = new ServerEntry { DisplayName = displayName };
        var parts = encoded.Split('|');
        if (parts.Length >= 6)
        {
            entry.ServerAddress = parts[0];
            entry.WindowsAuth = parts[1] == "WIN";
            entry.Username = parts[2];
            entry.TrustCertificate = parts[3] == "1";
            entry.Encrypt = parts[4] == "1";
            int.TryParse(parts[5], out int t);
            entry.Timeout = t > 0 ? t : 10;
        }
        else if (parts.Length >= 1)
        {
            entry.ServerAddress = parts[0];
        }
        return entry;
    }

    public static bool Save(ServerEntry entry, string password)
    {
        byte[] blob = Encoding.Unicode.GetBytes(password);
        var c = new CREDENTIAL
        {
            Type = CRED_TYPE_GENERIC,
            TargetName = Prefix + entry.DisplayName,
            UserName = Encode(entry),
            CredentialBlobSize = blob.Length,
            CredentialBlob = Marshal.AllocHGlobal(blob.Length),
            Persist = CRED_PERSIST_LOCAL_MACHINE,
            Comment = "SQL Disk Monitor - Blackcat Data Solutions"
        };
        Marshal.Copy(blob, 0, c.CredentialBlob, blob.Length);
        bool ok = CredWrite(ref c, 0);
        Marshal.FreeHGlobal(c.CredentialBlob);
        return ok;
    }

    public static (ServerEntry? entry, string password) Load(string displayName)
    {
        if (!CredRead(Prefix + displayName, CRED_TYPE_GENERIC, 0, out IntPtr ptr))
            return (null, "");

        var c = Marshal.PtrToStructure<CREDENTIAL>(ptr);
        string encoded = c.UserName ?? "";
        string pass = c.CredentialBlobSize > 0
            ? Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2) ?? ""
            : "";
        CredFree(ptr);
        return (Decode(displayName, encoded, pass), pass);
    }

    public static bool Delete(string displayName) =>
        CredDelete(Prefix + displayName, CRED_TYPE_GENERIC, 0);

    public static List<ServerEntry> ListAll()
    {
        var result = new List<ServerEntry>();
        if (!CredEnumerate(Prefix + "*", 0, out int count, out IntPtr ptr))
            return result;

        int sz = Marshal.SizeOf<IntPtr>();
        for (int i = 0; i < count; i++)
        {
            IntPtr cp = Marshal.ReadIntPtr(ptr, i * sz);
            var c = Marshal.PtrToStructure<CREDENTIAL>(cp);
            if (c.TargetName?.StartsWith(Prefix) == true)
            {
                string displayName = c.TargetName[Prefix.Length..];
                string encoded = c.UserName ?? "";
                result.Add(Decode(displayName, encoded, ""));
            }
        }
        CredFree(ptr);
        return result;
    }
}
