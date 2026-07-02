using System.Text;
using AC5250.Session;

namespace AC5250.Protocol;

public class TelnetNegotiator
{
    private readonly ConnectionSettings _settings;
    private readonly Stream _stream;

    // How many NEW-ENVIRON SEND requests we've answered. A second (or later)
    // request means the host rejected our DEVNAME (device already in use), so we
    // must send a DIFFERENT name — RFC 4777 disconnects a client that repeats the
    // same DEVNAME twice in a row.
    private int _envRequests;

    public event Action<string>? DebugLog;

    public TelnetNegotiator(ConnectionSettings settings, Stream stream)
    {
        _settings = settings;
        _stream = stream;
    }

    public async Task HandleDoAsync(byte option, CancellationToken ct)
    {
        switch (option)
        {
            case TelnetConstants.OPT_TERMINAL_TYPE:
            case TelnetConstants.OPT_BINARY:
            case TelnetConstants.OPT_EOR:
            case TelnetConstants.OPT_NEW_ENVIRON:
                await SendAsync([TelnetConstants.IAC, TelnetConstants.WILL, option], ct);
                break;
            default:
                await SendAsync([TelnetConstants.IAC, TelnetConstants.WONT, option], ct);
                break;
        }
    }

    public async Task HandleWillAsync(byte option, CancellationToken ct)
    {
        switch (option)
        {
            case TelnetConstants.OPT_BINARY:
            case TelnetConstants.OPT_EOR:
                await SendAsync([TelnetConstants.IAC, TelnetConstants.DO, option], ct);
                break;
            default:
                await SendAsync([TelnetConstants.IAC, TelnetConstants.DONT, option], ct);
                break;
        }
    }

    // The host telling us to stop / that it won't do an option. We requested only
    // the standard 5250 options, so there is nothing to unwind; just acknowledge
    // by not re-negotiating (avoids option ping-pong loops).
    public Task HandleDontAsync(byte option, CancellationToken ct) => Task.CompletedTask;
    public Task HandleWontAsync(byte option, CancellationToken ct) => Task.CompletedTask;

    public async Task HandleSubnegotiationAsync(byte[] buffer, int offset, int length, CancellationToken ct)
    {
        if (length < 2) return;

        byte option = buffer[offset];
        byte subCmd = buffer[offset + 1];

        switch (option)
        {
            case TelnetConstants.OPT_TERMINAL_TYPE:
                if (subCmd == TelnetConstants.TERMINAL_TYPE_SEND)
                    await SendTerminalTypeAsync(ct);
                break;

            case TelnetConstants.OPT_NEW_ENVIRON:
                if (subCmd == TelnetConstants.NEW_ENVIRON_SEND)
                    await SendEnvironAsync(ct);
                break;
        }
    }

    private async Task SendTerminalTypeAsync(CancellationToken ct)
    {
        string termType = _settings.ScreenSize == ScreenSize.Wide
            ? TelnetConstants.TERMINAL_IBM_3477_FC
            : TelnetConstants.TERMINAL_IBM_3179_2;

        var response = new List<byte>
        {
            TelnetConstants.IAC, TelnetConstants.SB,
            TelnetConstants.OPT_TERMINAL_TYPE,
            TelnetConstants.TERMINAL_TYPE_IS
        };
        response.AddRange(Encoding.ASCII.GetBytes(termType));
        response.Add(TelnetConstants.IAC);
        response.Add(TelnetConstants.SE);

        await SendAsync(response.ToArray(), ct);
    }

    private async Task SendEnvironAsync(CancellationToken ct)
    {
        var response = new List<byte>
        {
            TelnetConstants.IAC, TelnetConstants.SB,
            TelnetConstants.OPT_NEW_ENVIRON,
            TelnetConstants.NEW_ENVIRON_IS
        };

        string devName = ResolveDeviceName();
        if (!string.IsNullOrEmpty(devName))
        {
            response.Add(TelnetConstants.NEW_ENVIRON_USERVAR);
            response.AddRange(Encoding.ASCII.GetBytes("DEVNAME"));
            response.Add(TelnetConstants.NEW_ENVIRON_VALUE);
            response.AddRange(Encoding.ASCII.GetBytes(devName));
        }

        response.Add(TelnetConstants.IAC);
        response.Add(TelnetConstants.SE);

        DebugLog?.Invoke($"  -> NEW-ENVIRON IS DEVNAME='{devName}' (request #{_envRequests + 1})");
        _envRequests++;
        await SendAsync(response.ToArray(), ct);
    }

    /// <summary>
    /// Resolve the DEVNAME (workstation ID) to send. Empty means "let the host
    /// auto-assign QPADEVxxxx". A specified name is normalized to the legal
    /// character set (A-Z 0-9 # $ _ @, uppercase, max 10). On a repeat request
    /// (collision) a distinct name is generated so we never send the same one
    /// twice. Trailing ACS-style '*'/'=' uniquify markers are stripped.
    /// </summary>
    private string ResolveDeviceName()
    {
        string raw = (_settings.DeviceName ?? "").Trim().TrimEnd('*', '=');
        if (raw.Length == 0) return ""; // host assigns QPADEVxxxx

        string baseName = new string(raw.ToUpperInvariant()
            .Where(c => (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c is '#' or '$' or '_' or '@')
            .ToArray());
        if (baseName.Length == 0) baseName = "AC5250";

        if (_envRequests == 0)
            return Truncate(baseName, 10);

        string suffix = _envRequests.ToString();
        return Truncate(baseName, 10 - suffix.Length) + suffix;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private async Task SendAsync(byte[] data, CancellationToken ct)
    {
        DebugLog?.Invoke($"SEND [{data.Length}]: {string.Join(" ", data.Select(b => b.ToString("X2")))}");
        await _stream.WriteAsync(data, ct);
        await _stream.FlushAsync(ct);
    }
}
