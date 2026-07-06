using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace AC5250.Security;

/// <summary>
/// Stores IBM i sign-on credentials in the Windows Credential Manager (the OS vault,
/// DPAPI-encrypted per user). Nothing is written to a file or the connection JSON, and
/// the password is never logged.
///
/// A host can hold multiple credentials, each under a short <em>label</em> (e.g. "ADMIN",
/// "TESTUSER"), stored as generic credentials named <c>AC5250:{host}:{label}</c>. One label
/// per host may be marked the default (a marker entry <c>AC5250$def:{host}</c> whose user
/// field holds the default label — no password). Legacy single-credential entries written by
/// earlier versions (<c>AC5250:{host}</c>, no label) are still read and surfaced as the
/// "default" label.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CredentialStore
{
    public const string DefaultLabel = "default";

    private const string CredPrefix = "AC5250:";              // AC5250:{host}:{label}  (and legacy AC5250:{host})
    private const string DefaultMarkerPrefix = "AC5250$def:"; // AC5250$def:{host} -> user field holds default label
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    private static string Host(string host) => (host ?? "").Trim().ToLowerInvariant();
    private static string CleanLabel(string label) => (label ?? "").Trim().Replace(':', '_');
    private static string Target(string host, string label) => $"{CredPrefix}{Host(host)}:{CleanLabel(label)}";
    private static string LegacyTarget(string host) => $"{CredPrefix}{Host(host)}";
    private static string MarkerTarget(string host) => $"{DefaultMarkerPrefix}{Host(host)}";

    /// <summary>Add or overwrite the credential for a host under a label.</summary>
    public static void Save(string host, string label, string user, string password)
    {
        string clean = CleanLabel(label);
        Write(Target(host, clean), user, password);
        // A labeled "default" supersedes any pre-label bare entry (AC5250:{host}); migrate it
        // away so we don't leave a duplicate that the UI can't address by label.
        if (string.Equals(clean, DefaultLabel, StringComparison.OrdinalIgnoreCase))
            CredDeleteW(LegacyTarget(host), CRED_TYPE_GENERIC, 0);
    }

    /// <summary>
    /// Retrieve (user, password) for a host. With <paramref name="label"/> null, resolves the
    /// default: the marked default label, else a "default"/legacy entry, else the sole entry
    /// if exactly one exists, else null. With a label, matches it (case-insensitively).
    /// </summary>
    public static (string User, string Password)? Get(string host, string? label = null)
    {
        if (!string.IsNullOrWhiteSpace(label))
        {
            var direct = Read(Target(host, label));
            if (direct != null) return direct;
            // Case-insensitive match against stored labels. Don't return a null read here —
            // "default" can be a synthetic label for a legacy bare entry, so fall through.
            foreach (var l in Labels(host))
                if (string.Equals(l, label, StringComparison.OrdinalIgnoreCase))
                {
                    var r = Read(Target(host, l));
                    if (r != null) return r;
                }
            if (string.Equals(label, DefaultLabel, StringComparison.OrdinalIgnoreCase))
                return Read(LegacyTarget(host));
            return null;
        }

        // Default resolution.
        var def = GetDefaultLabel(host);
        if (def != null)
        {
            var r = Read(Target(host, def));
            if (r != null) return r;
        }
        return Read(Target(host, DefaultLabel))
            ?? Read(LegacyTarget(host))
            ?? SingleOrNull(host);
    }

    private static (string User, string Password)? SingleOrNull(string host)
    {
        var labels = Labels(host);
        return labels.Count == 1 ? Read(Target(host, labels[0])) : null;
    }

    public static void Delete(string host, string label)
    {
        string clean = CleanLabel(label);
        CredDeleteW(Target(host, clean), CRED_TYPE_GENERIC, 0);
        // "default" also covers a pre-label bare entry (AC5250:{host}) from older versions.
        if (string.Equals(clean, DefaultLabel, StringComparison.OrdinalIgnoreCase))
            CredDeleteW(LegacyTarget(host), CRED_TYPE_GENERIC, 0);
        // If we just removed the default, drop the now-stale default marker too.
        if (string.Equals(GetDefaultLabel(host), clean, StringComparison.OrdinalIgnoreCase))
            CredDeleteW(MarkerTarget(host), CRED_TYPE_GENERIC, 0);
    }

    /// <summary>Labels stored for a host (includes "default" if a legacy entry exists).</summary>
    public static IReadOnlyList<string> Labels(string host)
    {
        var result = new List<string>();
        foreach (var (target, _) in EnumerateUserNames(CredPrefix + Host(host) + ":*"))
        {
            int lastColon = target.LastIndexOf(':');   // target = AC5250:{host}:{label}
            if (lastColon > 0 && lastColon < target.Length - 1)
                result.Add(target[(lastColon + 1)..]);
        }
        if (Read(LegacyTarget(host)) != null &&
            !result.Any(l => string.Equals(l, DefaultLabel, StringComparison.OrdinalIgnoreCase)))
            result.Add(DefaultLabel);
        return result;
    }

    /// <summary>All stored (host, label, user) triples — passwords are NOT returned.</summary>
    public static IReadOnlyList<(string Host, string Label, string User)> List()
    {
        var result = new List<(string, string, string)>();
        foreach (var (target, user) in EnumerateUserNames(CredPrefix + "*"))
        {
            string rest = target[CredPrefix.Length..];       // {host}:{label} or {host}
            int colon = rest.IndexOf(':');
            if (colon < 0)
                result.Add((rest, DefaultLabel, user));       // legacy entry
            else
                result.Add((rest[..colon], rest[(colon + 1)..], user));
        }
        return result;
    }

    public static string? GetDefaultLabel(string host)
    {
        string? name = ReadUserName(MarkerTarget(host));
        return string.IsNullOrEmpty(name) ? null : name;
    }

    public static void SetDefaultLabel(string host, string label)
        => Write(MarkerTarget(host), CleanLabel(label), ""); // label in user field, no password

    // --- P/Invoke helpers ---

    private static void Write(string target, string user, string password)
    {
        byte[] blob = Encoding.Unicode.GetBytes(password ?? "");
        IntPtr blobPtr = Marshal.AllocHGlobal(Math.Max(blob.Length, 1));
        IntPtr targetPtr = Marshal.StringToCoTaskMemUni(target);
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

    private static (string User, string Password)? Read(string target)
    {
        if (!CredReadW(target, CRED_TYPE_GENERIC, 0, out IntPtr credPtr))
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

    private static string? ReadUserName(string target)
    {
        if (!CredReadW(target, CRED_TYPE_GENERIC, 0, out IntPtr credPtr))
            return null;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            return cred.UserName == IntPtr.Zero ? null : Marshal.PtrToStringUni(cred.UserName);
        }
        finally { CredFree(credPtr); }
    }

    /// <summary>Enumerate (target, userName) for credentials whose target matches the filter.</summary>
    private static IEnumerable<(string Target, string User)> EnumerateUserNames(string filter)
    {
        var result = new List<(string, string)>();
        if (!CredEnumerateW(filter, 0, out uint count, out IntPtr credsPtr))
            return result; // ERROR_NOT_FOUND when none match
        try
        {
            for (int i = 0; i < count; i++)
            {
                IntPtr p = Marshal.ReadIntPtr(credsPtr, i * IntPtr.Size);
                var cred = Marshal.PtrToStructure<CREDENTIAL>(p);
                string target = cred.TargetName == IntPtr.Zero ? "" : Marshal.PtrToStringUni(cred.TargetName) ?? "";
                string user = cred.UserName == IntPtr.Zero ? "" : Marshal.PtrToStringUni(cred.UserName) ?? "";
                if (target.Length > 0) result.Add((target, user));
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
