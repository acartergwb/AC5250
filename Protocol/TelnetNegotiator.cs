using AC5250.Session;

namespace AC5250.Protocol;

public class TelnetNegotiator
{
    private readonly ConnectionSettings _settings;
    private readonly Stream _stream;
    private bool _terminalTypeSent;
    private bool _environSent;
    private bool _serverRequestedEnviron;

    public bool NegotiationComplete { get; private set; }

    public TelnetNegotiator(ConnectionSettings settings, Stream stream)
    {
        _settings = settings;
        _stream = stream;
    }

    public async Task<bool> ProcessAsync(byte[] buffer, int offset, int length, CancellationToken ct)
    {
        int i = offset;
        int end = offset + length;

        while (i < end)
        {
            if (buffer[i] != TelnetConstants.IAC || i + 1 >= end)
            {
                i++;
                continue;
            }

            byte cmd = buffer[i + 1];

            switch (cmd)
            {
                case TelnetConstants.DO:
                    if (i + 2 >= end) return false;
                    await HandleDoAsync(buffer[i + 2], ct);
                    i += 3;
                    break;

                case TelnetConstants.WILL:
                    if (i + 2 >= end) return false;
                    await HandleWillAsync(buffer[i + 2], ct);
                    i += 3;
                    break;

                case TelnetConstants.SB:
                    int sePos = FindSubnegotiationEnd(buffer, i + 2, end);
                    if (sePos < 0) return false;
                    await HandleSubnegotiationAsync(buffer, i + 2, sePos - i - 2, ct);
                    i = sePos + 1; // skip past SE
                    break;

                case TelnetConstants.DONT:
                case TelnetConstants.WONT:
                    i += 3;
                    break;

                default:
                    i += 2;
                    break;
            }
        }

        if (_terminalTypeSent && (_environSent || !_serverRequestedEnviron))
        {
            NegotiationComplete = true;
        }

        return true;
    }

    private static int FindSubnegotiationEnd(byte[] buffer, int start, int end)
    {
        for (int i = start; i < end - 1; i++)
        {
            if (buffer[i] == TelnetConstants.IAC && buffer[i + 1] == TelnetConstants.SE)
                return i + 1;
        }
        return -1;
    }

    private async Task HandleDoAsync(byte option, CancellationToken ct)
    {
        switch (option)
        {
            case TelnetConstants.OPT_TERMINAL_TYPE:
            case TelnetConstants.OPT_BINARY:
            case TelnetConstants.OPT_EOR:
            case TelnetConstants.OPT_NEW_ENVIRON:
            case TelnetConstants.OPT_TN5250E:
                if (option == TelnetConstants.OPT_NEW_ENVIRON)
                    _serverRequestedEnviron = true;
                await SendAsync(new[] { TelnetConstants.IAC, TelnetConstants.WILL, option }, ct);
                break;
            default:
                await SendAsync(new[] { TelnetConstants.IAC, TelnetConstants.WONT, option }, ct);
                break;
        }
    }

    private async Task HandleWillAsync(byte option, CancellationToken ct)
    {
        switch (option)
        {
            case TelnetConstants.OPT_BINARY:
            case TelnetConstants.OPT_EOR:
            case TelnetConstants.OPT_TN5250E:
                await SendAsync(new[] { TelnetConstants.IAC, TelnetConstants.DO, option }, ct);
                break;
            default:
                await SendAsync(new[] { TelnetConstants.IAC, TelnetConstants.DONT, option }, ct);
                break;
        }
    }

    private async Task HandleSubnegotiationAsync(byte[] buffer, int offset, int length, CancellationToken ct)
    {
        if (length < 1) return;

        byte option = buffer[offset];

        switch (option)
        {
            case TelnetConstants.OPT_TERMINAL_TYPE:
                if (length >= 2 && buffer[offset + 1] == TelnetConstants.TERMINAL_TYPE_SEND)
                {
                    await SendTerminalTypeAsync(ct);
                }
                break;

            case TelnetConstants.OPT_NEW_ENVIRON:
                if (length >= 2 && buffer[offset + 1] == TelnetConstants.NEW_ENVIRON_SEND)
                {
                    await SendEnvironAsync(ct);
                }
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
        _terminalTypeSent = true;
    }

    private async Task SendEnvironAsync(CancellationToken ct)
    {
        var response = new List<byte>
        {
            TelnetConstants.IAC, TelnetConstants.SB,
            TelnetConstants.OPT_NEW_ENVIRON,
            TelnetConstants.NEW_ENVIRON_IS
        };

        // Send DEVNAME if provided
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
        _environSent = true;
    }

    private async Task SendAsync(byte[] data, CancellationToken ct)
    {
        await _stream.WriteAsync(data, ct);
        await _stream.FlushAsync(ct);
    }
}
