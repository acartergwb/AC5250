using AC5250.Model;
using AC5250.Protocol;
using AC5250.Session;

namespace AC5250.Mcp;

/// <summary>
/// Turns a live <see cref="TerminalSession"/> screen into a serializable
/// <see cref="ScreenSnapshot"/>. Reuses the same EBCDIC-to-ASCII translation the
/// on-screen renderer uses, so the text the model sees matches the window.
///
/// MUST be called on the session's owning thread (via <see cref="IThreadMarshal"/>).
/// </summary>
internal static class ScreenSerializer
{
    public static ScreenSnapshot Capture(TerminalSession s)
    {
        var buf = s.Screen;
        int rows = buf.Rows, cols = buf.Cols;

        string text = RenderText(buf);

        var fields = new List<FieldInfo>(buf.Fields.Count);
        for (int i = 0; i < buf.Fields.Count; i++)
        {
            var f = buf.Fields[i];
            bool hidden = f.Attribute.IsNonDisplay;
            bool numeric = f.Attribute.Format is FieldFormat.NumericOnly
                or FieldFormat.NumericShift or FieldFormat.DigitsOnly or FieldFormat.SignedNumeric;
            // Hidden field content is never surfaced (security): only the fact that
            // it exists and is hidden is reported.
            string content = hidden ? "" : DecodeField(f);
            fields.Add(new FieldInfo(i, f.Row + 1, f.Col + 1, f.Length,
                f.Attribute.IsBypass, hidden, numeric, content));
        }

        return new ScreenSnapshot(
            s.Id, s.Title, s.IsConnected, rows, cols,
            buf.CursorRow + 1, buf.CursorCol + 1,
            buf.InputInhibited, buf.InsertMode, buf.MessageWaiting,
            text, fields);
    }

    /// <summary>True if the visible screen text contains <paramref name="needle"/>
    /// (case-insensitive). Used by <c>wait_for_screen</c>; matches exactly what the model
    /// sees via <c>get_screen</c> (hidden fields masked). Must run on the session thread.</summary>
    public static bool ContainsText(TerminalSession s, string needle) =>
        RenderText(s.Screen).Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>Render the screen buffer to the plain-text grid the model reads (one line
    /// per row, attribute cells and non-display fields blanked).</summary>
    private static string RenderText(ScreenBuffer buf)
    {
        int rows = buf.Rows, cols = buf.Cols;
        var sb = new System.Text.StringBuilder(rows * (cols + 1));
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                char ch;
                if (buf.IsFieldAttributeAt(r, c))
                {
                    ch = ' '; // attribute byte position is blank on screen
                }
                else
                {
                    var field = buf.GetFieldAt(r, c);
                    if (field is { Attribute.IsNonDisplay: true })
                    {
                        ch = ' '; // mask hidden (password) fields out of the text view
                    }
                    else
                    {
                        byte e = field != null
                            ? field.GetCharAt(field.GetIndexForPosition(r, c, cols))
                            : buf.GetCharAt(r, c);
                        ch = Printable(Ebcdic.ToAscii(e));
                    }
                }
                sb.Append(ch);
            }
            if (r < rows - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string DecodeField(ScreenField f)
    {
        var chars = new char[f.Length];
        for (int i = 0; i < f.Length; i++)
            chars[i] = Printable(Ebcdic.ToAscii(f.GetCharAt(i)));
        return new string(chars).TrimEnd();
    }

    private static char Printable(char ch) => (ch < ' ' || ch > '~') ? ' ' : ch;
}
