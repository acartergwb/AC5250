using System.Runtime.Versioning;
using AC5250.Session;

namespace AC5250.Security;

/// <summary>
/// Read-only lookup of saved sign-on credentials for a session, used by the MCP <c>signon</c>
/// flow. This is the portability seam: the desktop app runs on Windows and stores credentials
/// per saved connection in the Credential Manager, while a headless server may run anywhere and
/// fall back to environment variables keyed by host. Callers pass the session's
/// <see cref="ConnectionSettings"/> so each source can key off whatever it needs (connection id
/// on the desktop, host name headless). Implementations must never persist or surface the
/// password anywhere but the returned tuple, consumed transiently to fill the hidden field.
/// </summary>
public interface ICredentialSource
{
    /// <summary>Credentials for a session's <paramref name="settings"/> under an optional
    /// <paramref name="label"/> (null = the default credential), or null if none are available.</summary>
    (string User, string Password)? Get(ConnectionSettings settings, string? label = null);

    /// <summary>The credential labels available for a session's <paramref name="settings"/> (may be empty).</summary>
    IReadOnlyList<string> Labels(ConnectionSettings settings);
}

/// <summary>
/// Credentials from the Windows Credential Manager (DPAPI, per-user) via <see cref="CredentialStore"/>,
/// scoped to the saved connection. A session created from a saved connection carries its id
/// directly; an ad-hoc or MCP-initiated session is matched to a saved connection by endpoint.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialSource : ICredentialSource
{
    public (string User, string Password)? Get(ConnectionSettings settings, string? label = null)
    {
        string? connId = ResolveConnectionId(settings);
        return connId == null ? null : CredentialStore.GetForConnection(connId, label);
    }

    public IReadOnlyList<string> Labels(ConnectionSettings settings)
    {
        string? connId = ResolveConnectionId(settings);
        return connId == null ? Array.Empty<string>() : CredentialStore.LabelsForConnection(connId);
    }

    // The session's own saved-connection id if it has one; otherwise the id of a saved connection
    // matching its endpoint, so a connection to a known host resolves that connection's logins.
    private static string? ResolveConnectionId(ConnectionSettings s)
        => !string.IsNullOrEmpty(s.Id) ? s.Id
         : ConnectionStore.FindByEndpoint(s.HostName, s.Port, s.DeviceName)?.Id;
}

/// <summary>
/// Credentials from environment variables injected by whoever spawns the process — the
/// natural fit for a stdio MCP server the client launches, and the standard headless/CI
/// secret path. Honors the "no credentials in a file" rule: nothing is read from or written
/// to disk. Looks up a host-specific pair first, then a host-agnostic default:
/// <code>
///   AC5250_&lt;HOST&gt;_USER / AC5250_&lt;HOST&gt;_PASSWORD   (HOST = host upper-cased, non-alphanumerics -> '_')
///   AC5250_USER            / AC5250_PASSWORD               (fallback for the single-host case)
/// </code>
/// </summary>
public sealed class EnvironmentCredentialSource : ICredentialSource
{
    public const string Prefix = "AC5250_";

    public (string User, string Password)? Get(ConnectionSettings settings, string? label = null)
    {
        string host = settings.HostName;
        if (!string.IsNullOrWhiteSpace(label))
        {
            var (lu, lp) = VarNamesFor(host, label);
            var byLabel = Pair(lu, lp);
            if (byLabel != null) return byLabel;
        }
        var (userVar, pwVar) = VarNamesFor(host);
        return Pair(userVar, pwVar) ?? Pair(Prefix + "USER", Prefix + "PASSWORD");
    }

    /// <summary>The env-var names for <paramref name="host"/> (and optional label), for help text.</summary>
    public static (string UserVar, string PasswordVar) VarNamesFor(string host, string? label = null)
    {
        string key = Sanitize(host);
        if (!string.IsNullOrWhiteSpace(label))
            key += "_" + Sanitize(label);
        return ($"{Prefix}{key}_USER", $"{Prefix}{key}_PASSWORD");
    }

    public IReadOnlyList<string> Labels(ConnectionSettings settings)
    {
        string prefix = Prefix + Sanitize(settings.HostName) + "_";   // AC5250_<HOST>_
        var labels = new List<string>();
        foreach (System.Collections.DictionaryEntry kv in Environment.GetEnvironmentVariables())
        {
            string name = (kv.Key?.ToString() ?? "").ToUpperInvariant();
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string rem = name[prefix.Length..];
            if (rem == "USER") labels.Add("default");   // matches CredentialStore.DefaultLabel
            else if (rem.EndsWith("_USER", StringComparison.Ordinal)) labels.Add(rem[..^5].ToLowerInvariant());
        }
        return labels.Distinct().ToList();
    }

    private static (string, string)? Pair(string userVar, string pwVar)
    {
        string? u = Env(userVar);
        string? p = Env(pwVar);
        return u is not null && p is not null ? (u, p) : null;
    }

    private static string? Env(string name)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private static string Sanitize(string host)
    {
        char[] chars = (host ?? "").Trim().ToUpperInvariant().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (!((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')))
                chars[i] = '_';
        }
        return new string(chars);
    }
}

/// <summary>Tries each source in order and returns the first hit.</summary>
public sealed class ChainedCredentialSource : ICredentialSource
{
    private readonly ICredentialSource[] _sources;

    public ChainedCredentialSource(params ICredentialSource[] sources) => _sources = sources;

    public (string User, string Password)? Get(ConnectionSettings settings, string? label = null)
    {
        foreach (var s in _sources)
        {
            var c = s.Get(settings, label);
            if (c is not null) return c;
        }
        return null;
    }

    public IReadOnlyList<string> Labels(ConnectionSettings settings)
        => _sources.SelectMany(s => s.Labels(settings)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>Factory for the platform-appropriate default credential source.</summary>
public static class CredentialSources
{
    /// <summary>
    /// On Windows: the Credential Manager, with environment variables as an override/fallback.
    /// Elsewhere: environment variables only (no Credential Manager exists off-Windows).
    /// </summary>
    public static ICredentialSource CreateDefault()
    {
        var env = new EnvironmentCredentialSource();
        return OperatingSystem.IsWindows()
            ? new ChainedCredentialSource(new WindowsCredentialSource(), env)
            : env;
    }
}
