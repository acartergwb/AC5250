using AC5250.Session;

namespace AC5250.Protocol;

public class TelnetNegotiator
{
    private readonly ConnectionSettings _settings;
    private readonly Stream _stream;

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
        response.AddRange(System.Text.Encoding.ASCII.GetBytes(termType));
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

        if (!string.IsNullOrEmpty(_settings.DeviceName))
        {
            response.Add(TelnetConstants.NEW_ENVIRON_USERVAR);
            response.AddRange(System.Text.Encoding.ASCII.GetBytes("DEVNAME"));
            response.Add(TelnetConstants.NEW_ENVIRON_VALUE);
            response.AddRange(System.Text.Encoding.ASCII.GetBytes(_settings.DeviceName));
        }

        response.Add(TelnetConstants.IAC);
        response.Add(TelnetConstants.SE);

        await SendAsync(response.ToArray(), ct);
    }

    private async Task SendAsync(byte[] data, CancellationToken ct)
    {
        await _stream.WriteAsync(data, ct);
        await _stream.FlushAsync(ct);
    }
}
