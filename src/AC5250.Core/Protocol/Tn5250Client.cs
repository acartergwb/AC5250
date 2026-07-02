using System.Net.Sockets;
using System.Net.Security;
using AC5250.Session;

namespace AC5250.Protocol;

public class Tn5250Client : IDisposable
{
    private TcpClient? _tcpClient;
    private Stream? _stream;
    private TelnetNegotiator? _negotiator;
    private CancellationTokenSource? _cts;
    private readonly ConnectionSettings _settings;

    public bool IsConnected => _tcpClient?.Connected == true;

    public event Action<byte[]>? DataReceived;
    public event Action<string>? Disconnected;
    public event Action<Exception>? Error;
    public event Action<string>? DebugLog;

    public Tn5250Client(ConnectionSettings settings)
    {
        _settings = settings;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _tcpClient = new TcpClient();
        _tcpClient.NoDelay = true;
        _tcpClient.ReceiveBufferSize = 32768;

        await _tcpClient.ConnectAsync(_settings.HostName, _settings.Port, ct);

        Stream baseStream = _tcpClient.GetStream();

        if (_settings.UseSsl)
        {
            var sslStream = new SslStream(baseStream, false);
            await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = _settings.HostName,
            }, ct);
            _stream = sslStream;
        }
        else
        {
            _stream = baseStream;
        }

        _negotiator = new TelnetNegotiator(_settings, _stream);
        _negotiator.DebugLog += m => DebugLog?.Invoke(m);
        _cts = new CancellationTokenSource();

        _ = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    // Telnet parse state persists ACROSS socket reads so that an IAC command,
    // an SB..SE block, or a 5250 record split across TCP segments is reassembled
    // correctly instead of being dropped (the original per-read parser could
    // silently miss a negotiation command and hang the connection).
    private readonly List<byte> _pending = new();
    private readonly List<byte> _record = new();

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[32768];

        try
        {
            while (!ct.IsCancellationRequested && _stream != null)
            {
                int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, ct);
                if (bytesRead == 0)
                {
                    Disconnected?.Invoke("Connection closed by host.");
                    return;
                }

                DebugLog?.Invoke($"RECV [{bytesRead}]: {FormatHex(buffer, 0, bytesRead)}");
                for (int k = 0; k < bytesRead; k++) _pending.Add(buffer[k]);

                await ProcessPendingAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
            Disconnected?.Invoke($"Connection error: {ex.Message}");
        }
    }

    private async Task ProcessPendingAsync(CancellationToken ct)
    {
        int idx = 0;
        while (idx < _pending.Count)
        {
            byte b = _pending[idx];

            if (b != TelnetConstants.IAC)
            {
                _record.Add(b);
                idx++;
                continue;
            }

            // b == IAC; need at least the command byte.
            if (idx + 1 >= _pending.Count) break;
            byte cmd = _pending[idx + 1];

            switch (cmd)
            {
                case TelnetConstants.IAC: // escaped 0xFF data byte
                    _record.Add(0xFF);
                    idx += 2;
                    continue;

                case TelnetConstants.EOR:
                    if (_record.Count > 0)
                    {
                        DebugLog?.Invoke($"5250 record [{_record.Count}]: {FormatHex(_record, 0, _record.Count)}");
                        DataReceived?.Invoke(_record.ToArray());
                        _record.Clear();
                    }
                    idx += 2;
                    continue;

                case TelnetConstants.DO:
                case TelnetConstants.DONT:
                case TelnetConstants.WILL:
                case TelnetConstants.WONT:
                    if (idx + 2 >= _pending.Count) goto incomplete; // wait for option byte
                    byte opt = _pending[idx + 2];
                    await HandleNegotiationAsync(cmd, opt, ct);
                    idx += 3;
                    continue;

                case TelnetConstants.SB:
                {
                    int iacSe = FindIacSe(_pending, idx + 2);
                    if (iacSe < 0) goto incomplete;            // wait for full SB..IAC SE
                    int contentStart = idx + 2;
                    int contentLen = iacSe - contentStart;
                    if (contentLen > 0)
                    {
                        var content = _pending.GetRange(contentStart, contentLen).ToArray();
                        DebugLog?.Invoke($"  Server SB [{contentLen}]: {FormatHex(content, 0, contentLen)}");
                        await _negotiator!.HandleSubnegotiationAsync(content, 0, contentLen, ct);
                    }
                    idx = iacSe + 2;                            // past IAC SE
                    continue;
                }

                default:
                    DebugLog?.Invoke($"  Unknown IAC cmd 0x{cmd:X2}");
                    idx += 2;
                    continue;
            }
        }

    incomplete:
        if (idx > 0) _pending.RemoveRange(0, idx);
    }

    private async Task HandleNegotiationAsync(byte cmd, byte opt, CancellationToken ct)
    {
        switch (cmd)
        {
            case TelnetConstants.DO:
                DebugLog?.Invoke($"  Server DO 0x{opt:X2}");
                await _negotiator!.HandleDoAsync(opt, ct);
                break;
            case TelnetConstants.WILL:
                DebugLog?.Invoke($"  Server WILL 0x{opt:X2}");
                await _negotiator!.HandleWillAsync(opt, ct);
                break;
            case TelnetConstants.DONT:
                DebugLog?.Invoke($"  Server DONT 0x{opt:X2}");
                await _negotiator!.HandleDontAsync(opt, ct);
                break;
            case TelnetConstants.WONT:
                DebugLog?.Invoke($"  Server WONT 0x{opt:X2}");
                await _negotiator!.HandleWontAsync(opt, ct);
                break;
        }
    }

    /// <summary>Index of the IAC that starts an IAC SE terminator at/after start, or -1.</summary>
    private static int FindIacSe(List<byte> data, int start)
    {
        for (int i = start; i + 1 < data.Count; i++)
            if (data[i] == TelnetConstants.IAC && data[i + 1] == TelnetConstants.SE)
                return i;
        return -1;
    }

    public async Task SendRecordAsync(byte[] data, CancellationToken ct = default)
    {
        if (_stream == null) return;

        // Build framed record: escape any 0xFF in data, then append IAC EOR
        var framed = new List<byte>(data.Length + 10);
        foreach (byte b in data)
        {
            framed.Add(b);
            if (b == TelnetConstants.IAC)
                framed.Add(TelnetConstants.IAC); // escape
        }
        framed.Add(TelnetConstants.IAC);
        framed.Add(TelnetConstants.EOR);

        DebugLog?.Invoke($"SEND record [{data.Length}]: {FormatHex(data, 0, Math.Min(data.Length, 48))}");
        await _stream.WriteAsync(framed.ToArray(), ct);
        await _stream.FlushAsync(ct);
    }

    private static string FormatHex(IReadOnlyList<byte> data, int offset, int length)
    {
        if (length <= 0) return "(empty)";
        var sb = new System.Text.StringBuilder(length * 3);
        int end = Math.Min(offset + length, data.Count);
        for (int i = offset; i < end; i++) { sb.Append(data[i].ToString("X2")); sb.Append(' '); }
        return sb.ToString().TrimEnd();
    }

    public void Disconnect()
    {
        _cts?.Cancel();
        _stream?.Dispose();
        _tcpClient?.Dispose();
        _stream = null;
        _tcpClient = null;
    }

    public void Dispose()
    {
        Disconnect();
        _cts?.Dispose();
    }
}
