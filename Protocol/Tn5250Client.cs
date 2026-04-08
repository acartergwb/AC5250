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
        _cts = new CancellationTokenSource();

        _ = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[32768];
        var recordBuffer = new List<byte>();
        bool inIac = false;

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

                int i = 0;
                while (i < bytesRead)
                {
                    byte b = buffer[i];

                    if (inIac)
                    {
                        inIac = false;
                        switch (b)
                        {
                            case TelnetConstants.IAC:
                                // Escaped 0xFF - literal data byte
                                recordBuffer.Add(0xFF);
                                break;

                            case TelnetConstants.EOR:
                                // End of record - deliver the complete 5250 record
                                if (recordBuffer.Count > 0)
                                {
                                    DataReceived?.Invoke(recordBuffer.ToArray());
                                    recordBuffer.Clear();
                                }
                                break;

                            case TelnetConstants.DO:
                                if (i + 1 < bytesRead)
                                {
                                    await _negotiator!.HandleDoAsync(buffer[i + 1], ct);
                                    i++;
                                }
                                break;

                            case TelnetConstants.WILL:
                                if (i + 1 < bytesRead)
                                {
                                    await _negotiator!.HandleWillAsync(buffer[i + 1], ct);
                                    i++;
                                }
                                break;

                            case TelnetConstants.DONT:
                            case TelnetConstants.WONT:
                                i++; // skip option byte
                                break;

                            case TelnetConstants.SB:
                                int seIdx = FindSE(buffer, i + 1, bytesRead);
                                if (seIdx >= 0)
                                {
                                    // Content between SB and IAC SE
                                    int contentStart = i + 1;
                                    int contentLen = seIdx - 1 - contentStart;
                                    if (contentLen > 0)
                                    {
                                        await _negotiator!.HandleSubnegotiationAsync(
                                            buffer, contentStart, contentLen, ct);
                                    }
                                    i = seIdx;
                                }
                                break;

                            default:
                                break;
                        }
                    }
                    else if (b == TelnetConstants.IAC)
                    {
                        inIac = true;
                    }
                    else
                    {
                        recordBuffer.Add(b);
                    }

                    i++;
                }
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

    /// <summary>
    /// Find IAC SE sequence. Returns the index of the SE byte.
    /// </summary>
    private static int FindSE(byte[] buffer, int start, int end)
    {
        for (int i = start; i < end - 1; i++)
        {
            if (buffer[i] == TelnetConstants.IAC && buffer[i + 1] == TelnetConstants.SE)
                return i + 1;
        }
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

        await _stream.WriteAsync(framed.ToArray(), ct);
        await _stream.FlushAsync(ct);
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
