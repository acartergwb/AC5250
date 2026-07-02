namespace AC5250.Mcp;

/// <summary>One open terminal session, as reported by the <c>list_sessions</c> tool.</summary>
public sealed record SessionSummary(
    string Id,
    string Title,
    string Host,
    int Port,
    bool Connected,
    bool Active);

/// <summary>
/// A single input field on the screen. <see cref="Index"/> is the value passed to
/// <c>set_field</c>. Row/Col are 1-based to match the emulator status bar.
/// </summary>
public sealed record FieldInfo(
    int Index,
    int Row,
    int Col,
    int Length,
    bool Protected,
    bool Hidden,
    bool Numeric,
    string Content);

/// <summary>
/// A full snapshot of a 5250 screen returned by the screen/input tools. <see cref="Text"/>
/// is the human-readable grid (one line per row) for the model to "see"; <see cref="Fields"/>
/// is the structured field list for targeting input. Non-display (password) fields are
/// masked out of both.
/// </summary>
public sealed record ScreenSnapshot(
    string SessionId,
    string Title,
    bool Connected,
    int Rows,
    int Cols,
    int CursorRow,
    int CursorCol,
    bool KeyboardInhibited,
    bool InsertMode,
    bool MessageWaiting,
    string Text,
    IReadOnlyList<FieldInfo> Fields);
