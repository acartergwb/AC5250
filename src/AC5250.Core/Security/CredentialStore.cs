using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace AC5250.Security;

/// <summary>
/// Stores IBM i sign-on credentials in the Windows Credential Manager (the OS vault,
/// DPAPI-encrypted per user). Nothing is written to a file or the connection JSON, and
/// the password is never logged. Entries are keyed by host as generic credentials named
/// "AC5250:{host}", so they're visible/removable under Control Panel > Credential Manager.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CredentialStore
{
    private const string Prefix = "AC5250:";
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    private static string Target(string host) => Prefix + (host ?? "").Trim().ToLowerInvariant();

    /// <summary>Add or overwrite the credential for a host.</summary>
    public static void Save(string host, string user, string password)
    {
        byte[] blob = Encoding.Unicode.GetBytes(password ?? "");
        IntPtr blobPtr = Marshal.AllocHGlobal(Math.Max(blob.Length, 1));
        IntPtr targetPtr = Marshal.StringToCoTaskMemUni(Target(host));
        IntPtr userPtr = Marshal.StringToCoTaskMemUni(user ?? "");
        try
        {
            if (blob.Length > 0) Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = targetPtr,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = userPtr,
            };
            if (!CredWriteW(ref cred, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Array.Clear(blob);
            Marshal.FreeHGlobal(blobPtr);
            Marshal.FreeCoTaskMem(targetPtr);
            Marshal.FreeCoTaskMem(userPtr);
        }
    }

    /// <summary>Retrieve (user, password) for a host, or null if none is stored.</summary>
    public static (string User, string Password)? Get(string host)
    {
        if (!CredReadW(Target(host), CRED_TYPE_GENERIC, 0, out IntPtr credPtr))
            return null;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            string user = cred.UserName == IntPtr.Zero ? "" : Marshal.PtrToStringUni(cred.UserName) ?? "";
            string password = "";
            if (cred.CredentialBlob != IntPtr.Zero && cred.CredentialBlobSize > 0)
            {
                var bytes = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
                password = Encoding.Unicode.GetString(bytes);
                Array.Clear(bytes);
            }
            return (user, password);
        }
        finally { CredFree(credPtr); }
    }

    public static void Delete(string host)
    {
        CredDeleteW(Target(host), CRED_TYPE_GENERIC, 0); // ignore "not found"
    }

    /// <summary>List stored (host, user) pairs — passwords are NOT returned.</summary>
    public static IReadOnlyList<(string Host, string User)> List()
    {
        var result = new List<(string, string)>();
        if (!CredEnumerateW(Prefix + "*", 0, out uint count, out IntPtr credsPtr))
            return result; // ERROR_NOT_FOUND when the vault has none
        try
        {
            for (int i = 0; i < count; i++)
            {
                IntPtr p = Marshal.ReadIntPtr(credsPtr, i * IntPtr.Size);
                var cred = Marshal.PtrToStructure<CREDENTIAL>(p);
                string target = cred.TargetName == IntPtr.Zero ? "" : Marshal.PtrToStringUni(cred.TargetName) ?? "";
                string host = target.StartsWith(Prefix, StringComparison.Ordinal) ? target[Prefix.Length..] : target;
                string user = cred.UserName == IntPtr.Zero ? "" : Marshal.PtrToStringUni(cred.UserName) ?? "";
                result.Add((host, user));
            }
        }
        finally { CredFree(credsPtr); }
        return result;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    private static extern bool CredWriteW(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredEnumerateW")]
    private static extern bool CredEnumerateW(string? filter, uint flags, out uint count, out IntPtr credentials);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }
}
