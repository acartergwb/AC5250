using AC5250.Input;
using AC5250.Model;
using AC5250.Protocol;

namespace AC5250.Session;

public class TerminalSession : IDisposable
{
    private readonly Tn5250Client _client;
    private readonly DataStreamParser _parser;
    private readonly SynchronizationContext? _dispatcher;
    private readonly List<string> _debugLog = new();
    private bool _firstDataReceived;

    /// <summary>Stable short id used by the MCP layer to address this session.</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// When true, typed letters are uppercased (except in non-display/password
    /// fields), matching the ACS "monocase" behavior most green-screen apps expect.
    /// </summary>
    public bool UppercaseInput { get; set; } = true;

    public ConnectionSettings Settings { get; }
    public ScreenBuffer Screen { get; }
    public bool IsConnected => _client.IsConnected;
    public string Title { get; private set; }

    public event Action<string>? ConnectionClosed;
    public event Action<string>? StatusMessage;

    public TerminalSession(ConnectionSettings settings, SynchronizationContext? dispatcher = null)
    {
        Settings = settings;
        _dispatcher = dispatcher;
        Title = settings.DisplayName;

        int rows = settings.ScreenSize == ScreenSize.Wide ? 27 : 24;
        int cols = settings.ScreenSize == ScreenSize.Wide ? 132 : 80;
        Screen = new ScreenBuffer(rows, cols);

        _client = new Tn5250Client(settings);
        _parser = new DataStreamParser(Screen);

        // Wire events
        _client.DataReceived += OnDataReceived;
        _client.Disconnected += OnDisconnected;
        _client.Error += OnError;
        _client.DebugLog += OnDebugLog;
        _parser.SendResponse += OnSendResponse;
    }

    public async Task ConnectAsync()
    {
        try
        {
            Screen.InputInhibited = true;
            Screen.NotifyScreenChanged();
            StatusMessage?.Invoke($"Connecting to {Settings.HostName}:{Settings.Port}...");

            await _client.ConnectAsync();

            StatusMessage?.Invoke("Connected. Negotiating...");
        }
        catch (Exception ex)
        {
            ConnectionClosed?.Invoke($"Connection failed: {ex.Message}");
            throw;
        }
    }

    public void HandleKeyAction(KeyAction action)
    {
        if (!IsConnected) return;

        switch (action.Type)
        {
            case KeyActionType.AidKey:
                SendAidKey(action.Aid);
                break;

            case KeyActionType.Character:
                HandleCharacterInput(action.Character);
                break;

            case KeyActionType.Tab:
                TabToNextField();
                break;

            case KeyActionType.BackTab:
                BackTabToPrevField();
                break;

            case KeyActionType.ArrowUp:
                Screen.MoveCursorTo(Screen.CursorRow - 1, Screen.CursorCol);
                break;

            case KeyActionType.ArrowDown:
                Screen.MoveCursorTo(Screen.CursorRow + 1, Screen.CursorCol);
                break;

            case KeyActionType.ArrowLeft:
                MoveCursorLeft();
                break;

            case KeyActionType.ArrowRight:
                MoveCursorRight();
                break;

            case KeyActionType.Backspace:
                HandleBackspace();
                break;

            case KeyActionType.Delete:
                HandleDelete();
                break;

            case KeyActionType.Home:
                HandleHome();
                break;

            case KeyActionType.End:
                HandleEnd();
                break;

            case KeyActionType.Insert:
                Screen.InsertMode = !Screen.InsertMode;
                Screen.NotifyScreenChanged();
                break;

            case KeyActionType.FieldExit:
                HandleFieldExit();
                break;

            case KeyActionType.Reset:
                HandleReset();
                break;

            case KeyActionType.EraseInput:
                HandleEraseInput();
                break;
        }
    }

    private void SendAidKey(AidKey aid)
    {
        if (Screen.InputInhibited && aid != AidKey.Attn && aid != AidKey.SysReq)
        {
            return; // keyboard locked
        }

        Screen.InputInhibited = true;
        Screen.NotifyScreenChanged();

        var response = DataStreamWriter.BuildReadResponse(Screen, aid);
        _ = _client.SendRecordAsync(response);
    }

    private void HandleCharacterInput(char ch)
    {
        if (Screen.InputInhibited) return;

        var field = Screen.GetFieldForCursor();
        if (field == null || field.Attribute.IsBypass) return;

        int idx = field.GetIndexForPosition(Screen.CursorRow, Screen.CursorCol, Screen.Cols);
        if (idx < 0 || idx >= field.Length) return;

        // Monocase: uppercase letters unless this is a hidden (password) field,
        // where case must be preserved.
        if (UppercaseInput && !field.Attribute.IsNonDisplay)
            ch = char.ToUpperInvariant(ch);

        byte ebcdic = Ebcdic.FromAscii(ch);

        if (Screen.InsertMode)
        {
            field.InsertCharAt(idx, ebcdic);
        }
        else
        {
            field.SetCharAt(idx, ebcdic);
        }

        Screen.SyncFieldToBuffer(field);
        MoveCursorRight();
        Screen.NotifyScreenChanged();
    }

    /// <summary>
    /// Programmatically set an input field's value (used by the MCP layer).
    /// Positions the cursor at the field, optionally clears it, then types the
    /// value through the normal input path. Stops at the field boundary so it
    /// never overflows into the next field. Returns false if input is inhibited
    /// or the index is not a usable (non-bypass) input field.
    /// </summary>
    public bool SetFieldValue(int fieldIndex, string value, bool clearFirst = true)
    {
        if (Screen.InputInhibited) return false;
        if (fieldIndex < 0 || fieldIndex >= Screen.Fields.Count) return false;

        var field = Screen.Fields[fieldIndex];
        if (field.Attribute.IsBypass) return false;

        Screen.MoveCursorTo(field.Row, field.Col);

        if (clearFirst)
        {
            field.ClearData();
            Screen.SyncFieldToBuffer(field);
        }

        foreach (char ch in value)
        {
            if (Screen.GetFieldForCursor() != field) break; // don't spill into the next field
            HandleCharacterInput(ch);
        }

        Screen.NotifyScreenChanged();
        return true;
    }

    /// <summary>
    /// Type a string at the current cursor position, character by character
    /// (used by the MCP layer's send_text). Honors field boundaries and the
    /// current insert/overtype mode via the normal input path.
    /// </summary>
    public void TypeString(string text)
    {
        if (Screen.InputInhibited) return;
        foreach (char ch in text)
            HandleCharacterInput(ch);
    }

    private void HandleBackspace()
    {
        if (Screen.InputInhibited) return;

        MoveCursorLeft();
        var field = Screen.GetFieldForCursor();
        if (field == null || field.Attribute.IsBypass) return;

        int idx = field.GetIndexForPosition(Screen.CursorRow, Screen.CursorCol, Screen.Cols);
        field.DeleteCharAt(idx);
        Screen.SyncFieldToBuffer(field);
        Screen.NotifyScreenChanged();
    }

    private void HandleDelete()
    {
        if (Screen.InputInhibited) return;

        var field = Screen.GetFieldForCursor();
        if (field == null || field.Attribute.IsBypass) return;

        int idx = field.GetIndexForPosition(Screen.CursorRow, Screen.CursorCol, Screen.Cols);
        field.DeleteCharAt(idx);
        Screen.SyncFieldToBuffer(field);
        Screen.NotifyScreenChanged();
    }

    private void HandleHome()
    {
        var field = Screen.GetFieldForCursor();
        if (field != null)
        {
            Screen.MoveCursorTo(field.Row, field.Col);
        }
        else
        {
            Screen.MoveCursorTo(0, 0);
        }
    }

    private void HandleEnd()
    {
        var field = Screen.GetFieldForCursor();
        if (field == null) return;

        // Find last non-space character
        int lastNonSpace = -1;
        for (int i = field.Length - 1; i >= 0; i--)
        {
            if (field.GetCharAt(i) != 0x40)
            {
                lastNonSpace = i;
                break;
            }
        }

        int targetIdx = lastNonSpace + 1;
        if (targetIdx >= field.Length) targetIdx = field.Length - 1;

        int pos = field.Row * Screen.Cols + field.Col + targetIdx;
        int row = pos / Screen.Cols;
        int col = pos % Screen.Cols;
        Screen.MoveCursorTo(row, col);
    }

    private void HandleFieldExit()
    {
        if (Screen.InputInhibited) return;

        var field = Screen.GetFieldForCursor();
        if (field == null || field.Attribute.IsBypass) return;

        // Clear from cursor to end of field
        int idx = field.GetIndexForPosition(Screen.CursorRow, Screen.CursorCol, Screen.Cols);
        for (int i = idx; i < field.Length; i++)
        {
            field.SetCharAt(i, 0x40);
        }
        Screen.SyncFieldToBuffer(field);

        // Move to next field
        TabToNextField();
        Screen.NotifyScreenChanged();
    }

    private void HandleReset()
    {
        Screen.InputInhibited = false;
        Screen.NotifyScreenChanged();
    }

    private void HandleEraseInput()
    {
        if (Screen.InputInhibited) return;

        foreach (var field in Screen.Fields)
        {
            if (!field.Attribute.IsBypass)
            {
                field.ClearData();
                Screen.SyncFieldToBuffer(field);
            }
        }

        // Move cursor to first input field
        var first = Screen.GetNextInputField(0, 0);
        if (first != null)
        {
            Screen.MoveCursorTo(first.Row, first.Col);
        }
        Screen.NotifyScreenChanged();
    }

    private void TabToNextField()
    {
        var next = Screen.GetNextInputField(Screen.CursorRow, Screen.CursorCol);
        if (next != null)
        {
            Screen.MoveCursorTo(next.Row, next.Col);
        }
    }

    private void BackTabToPrevField()
    {
        var prev = Screen.GetPrevInputField(Screen.CursorRow, Screen.CursorCol);
        if (prev != null)
        {
            Screen.MoveCursorTo(prev.Row, prev.Col);
        }
    }

    private void MoveCursorLeft()
    {
        Screen.MoveCursorBack();
        Screen.NotifyScreenChanged();
    }

    private void MoveCursorRight()
    {
        Screen.MoveCursorForward();
        Screen.NotifyScreenChanged();
    }

    private void OnDataReceived(byte[] record)
    {
        // If a dispatcher was supplied (the WinForms UI thread for the desktop
        // app, or the single-thread dispatcher for the headless host), marshal
        // parsing onto it so host-driven screen mutation is serialized with
        // input handling and rendering. Otherwise parse inline on the socket
        // thread (original behavior, preserved when no dispatcher is set).
        if (_dispatcher != null)
            _dispatcher.Post(_ => ApplyRecord(record), null);
        else
            ApplyRecord(record);
    }

    private async void ApplyRecord(byte[] record)
    {
        try
        {
            if (!_firstDataReceived)
            {
                _firstDataReceived = true;
                StatusMessage?.Invoke($"Connected to {Settings.HostName}");
            }
            await _parser.ParseRecordAsync(record);
        }
        catch (Exception ex)
        {
            StatusMessage?.Invoke($"Parse error: {ex.Message}");
        }
    }

    private async Task OnSendResponse(byte[] data)
    {
        try
        {
            await _client.SendRecordAsync(data);
        }
        catch (Exception ex)
        {
            StatusMessage?.Invoke($"Send error: {ex.Message}");
        }
    }

    private void OnDisconnected(string reason)
    {
        ConnectionClosed?.Invoke(reason);
    }

    private void OnError(Exception ex)
    {
        StatusMessage?.Invoke($"Error: {ex.Message}");
    }

    /// <summary>Raised for each debug/trace line (connection, negotiation, records).</summary>
    public event Action<string>? DebugLogged;

    private void OnDebugLog(string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        lock (_debugLog) _debugLog.Add(line);
        DebugLogged?.Invoke(line);
    }

    /// <summary>Snapshot of the connection/negotiation debug trace (thread-safe copy).</summary>
    public IReadOnlyList<string> GetDebugLog()
    {
        lock (_debugLog) return _debugLog.ToArray();
    }

    public void Disconnect()
    {
        _client.Disconnect();
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
