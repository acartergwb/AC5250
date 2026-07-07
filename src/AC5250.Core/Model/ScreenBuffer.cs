using AC5250.Protocol;

namespace AC5250.Model;

public class ScreenBuffer
{
    public int Rows { get; private set; }
    public int Cols { get; private set; }

    // Character buffer (EBCDIC)
    private byte[] _characters;
    private byte[] _attributes;

    // Saved screen state
    private byte[]? _savedCharacters;
    private byte[]? _savedAttributes;
    private List<ScreenField>? _savedFields;
    private int _savedCursorRow, _savedCursorCol;

    // Field list
    public List<ScreenField> Fields { get; } = new();

    // Cursor
    public int CursorRow { get; private set; }
    public int CursorCol { get; private set; }
    private int _insertCursorRow = -1;
    private int _insertCursorCol = -1;

    // Buffer write position
    private int _bufferRow;
    private int _bufferCol;

    // Status
    public bool InputInhibited { get; set; }
    public bool InsertMode { get; set; }
    public bool MessageWaiting { get; set; }
    public bool SystemAvailable { get; set; } = true;

    public event Action? ScreenChanged;

    /// <summary>
    /// Monotonically increments on every screen change. The MCP layer snapshots
    /// this before sending an AID key and waits for it to advance to know the
    /// host has painted a new screen ("screen settled").
    /// </summary>
    public long Version { get; private set; }

    public ScreenBuffer(int rows, int cols)
    {
        Rows = rows;
        Cols = cols;
        _characters = new byte[rows * cols];
        _attributes = new byte[rows * cols];
        Array.Fill(_characters, (byte)0x40); // EBCDIC space
    }

    public void NotifyScreenChanged()
    {
        // Resolve insert cursor if set
        if (_insertCursorRow >= 0)
        {
            CursorRow = _insertCursorRow;
            CursorCol = _insertCursorCol;
            _insertCursorRow = -1;
            _insertCursorCol = -1;
        }
        Version++;
        ScreenChanged?.Invoke();
    }

    /// <summary>
    /// Clear the screen, resizing the buffer first if the requested size differs.
    /// The host switches between 24x80 (Clear Unit) and 27x132 (Clear Unit Alternate)
    /// within a session; without following that, host addressing lands at the wrong
    /// positions and the previous screen bleeds through.
    /// </summary>
    public void ClearToSize(int rows, int cols)
    {
        if (rows != Rows || cols != Cols)
        {
            Rows = rows;
            Cols = cols;
            _characters = new byte[rows * cols];
            _attributes = new byte[rows * cols];
            _savedCharacters = null; // saved screen is for the old size
        }
        Clear();
    }

    public void Clear()
    {
        Array.Fill(_characters, (byte)0x40);
        Array.Fill(_attributes, (byte)0x00);
        Fields.Clear();
        CursorRow = 0;
        CursorCol = 0;
        _bufferRow = 0;
        _bufferCol = 0;
        InsertMode = false;
    }

    public void ClearFormatTable()
    {
        Fields.Clear();
    }

    public void ResetMDT()
    {
        foreach (var field in Fields)
        {
            field.Modified = false;
        }
    }

    public void SetBufferAddress(int row, int col)
    {
        // 5250 uses 1-based addressing. Clamp to the buffer (both bounds): a host that
        // addresses beyond the current screen — e.g. a 27x132 layout drawn into a 24x80
        // buffer — must not push the write position out of range, or a later field/char
        // write (and field sync) throws and aborts the parse, freezing the keyboard.
        _bufferRow = Math.Clamp(row - 1, 0, Rows - 1);
        _bufferCol = Math.Clamp(col - 1, 0, Cols - 1);
    }

    public void SetCursorAddress(int row, int col)
    {
        CursorRow = Math.Clamp(row, 0, Rows - 1);
        CursorCol = Math.Clamp(col, 0, Cols - 1);
    }

    public void InsertCursorHere()
    {
        _insertCursorRow = _bufferRow;
        _insertCursorCol = _bufferCol;
    }

    // A field's leading attribute byte sits one cell before its first data cell.
    // In 5250, writing a CHARACTER over that attribute byte DESTROYS the field (the
    // position is no longer an attribute). This is how a stale input box goes away when
    // the host repaints over it without clearing the format table — e.g. after F12
    // closes the F21 command line, the host paints "Selection:" over the old command
    // field's attribute cell, so the box must drop. NOTE: re-writing an *attribute* at
    // that cell (e.g. a non-display password field whose 0x27 attribute is refreshed in
    // a second pass) must NOT destroy the field — so this is called only for character
    // writes, never from WriteAttribute.
    private void InvalidateFieldWithAttrAt(int pos)
    {
        if (Fields.Count == 0) return;
        for (int i = Fields.Count - 1; i >= 0; i--)
        {
            var f = Fields[i];
            if (f.Row * Cols + f.Col - 1 == pos) Fields.RemoveAt(i);
        }
    }

    public void WriteAttribute(byte displayAttr)
    {
        // A 5250 display attribute (0x20-0x3F) occupies a screen position as a
        // blank and sets the display characteristics for the data that follows.
        int pos = _bufferRow * Cols + _bufferCol;
        if (pos >= 0 && pos < _characters.Length)
        {
            // NOTE: do NOT invalidate a field here — writing an attribute at a field's
            // attribute cell refreshes it (e.g. a password field's 0x27 in a 2nd pass);
            // only a character overwrite destroys the field.
            _characters[pos] = 0x40; // display as space
            _attributes[pos] = displayAttr;
        }
        AdvanceBufferPosition();
    }

    public void WriteCharacter(byte ebcdic)
    {
        // A null (0x00) marks an empty character position in 5250. Store it as an
        // EBCDIC blank so it neither renders as a control glyph nor ends up embedded
        // in transmitted field data (an embedded null makes a value an invalid name).
        if (ebcdic == 0x00) ebcdic = 0x40;
        int pos = _bufferRow * Cols + _bufferCol;
        if (pos >= 0 && pos < _characters.Length)
        {
            InvalidateFieldWithAttrAt(pos);
            _characters[pos] = ebcdic;
            _attributes[pos] = 0; // this position now holds a character, not an attribute
        }
        AdvanceBufferPosition();
    }

    public void WriteCharacterRaw(byte rawByte)
    {
        // Write without EBCDIC interpretation
        int pos = _bufferRow * Cols + _bufferCol;
        if (pos >= 0 && pos < _characters.Length)
        {
            InvalidateFieldWithAttrAt(pos);
            _characters[pos] = rawByte;
        }
        AdvanceBufferPosition();
    }

    public void DefineField(FieldAttribute attr, int length)
    {
        // The attribute byte occupies the current buffer position. Store the real
        // 5250 display-attribute byte (0x20-0x3F) so the renderer can color the
        // field's text; a field attribute always displays as a blank.
        int attrPos = _bufferRow * Cols + _bufferCol;
        if (attrPos >= 0 && attrPos < _attributes.Length)
        {
            byte raw = attr.Raw;
            _attributes[attrPos] = (raw >= 0x20 && raw <= 0x3F) ? raw : (byte)0x20;
        }

        // Field data starts at the next position.
        AdvanceBufferPosition();

        // A redraw that re-defines a field at the same position REPLACES it — 5250
        // has one field per position. Without this, screens that repaint without a
        // Clear (e.g. after closing the F21 command window) pile up duplicate field
        // objects, leaving a stale input box on screen that never clears.
        Fields.RemoveAll(f => f.Row == _bufferRow && f.Col == _bufferCol);

        var field = new ScreenField(_bufferRow, _bufferCol, length, attr, Cols);
        Fields.Add(field);

        // Leave the buffer at the field's first data position. In 5250 the field's
        // initial content follows the SF order inline and fills the field; if we
        // advanced past the field here, that content would land AFTER the field
        // (overwriting the next constant) and the field would render empty.
    }

    public void RepeatToAddress(int row, int col, byte ch)
    {
        int targetRow = Math.Max(0, row - 1);
        int targetCol = Math.Max(0, col - 1);
        int targetPos = targetRow * Cols + targetCol;
        int currentPos = _bufferRow * Cols + _bufferCol;
        int total = _characters.Length;

        // Wrap only when the stop address is STRICTLY before the current position.
        // A stop address EQUAL to the current position is a zero-length repeat (a
        // no-op), NOT a full-screen wrap — S2K emits exactly this (e.g. WMETCR's
        // `SBA; non-display attr; RA to the just-advanced position`). Using `<=`
        // here sent that case around the whole buffer, filling all 1920 cells and
        // erasing everything painted before it (title, prompts, the function-key
        // legend) — leaving only content written after the RA. Matches PCOMM/ACS.
        if (targetPos < currentPos)
        {
            // Wrap around
            while (currentPos < total) FillCell(currentPos++, ch);
            currentPos = 0;
        }

        while (currentPos < targetPos && currentPos < total)
            FillCell(currentPos++, ch);

        _bufferRow = targetRow;
        _bufferCol = targetCol;
    }

    // Fill one cell during a Repeat-to-Address. Overwriting a field's leading attribute
    // byte destroys that field (same 5250 rule WriteCharacter follows) — without this, a
    // host that blanks a region with RA (S2K does this constantly on windowed screens)
    // leaves the old fields' objects behind, rendering as phantom input underlines.
    private void FillCell(int pos, byte ch)
    {
        InvalidateFieldWithAttrAt(pos);
        _characters[pos] = ch;
        _attributes[pos] = 0;
    }

    public void EraseToAddress(int row, int col)
    {
        RepeatToAddress(row, col, 0x40); // fill with EBCDIC spaces
    }

    public byte GetCharAt(int row, int col)
    {
        int pos = row * Cols + col;
        if (pos < 0 || pos >= _characters.Length) return 0x40;
        return _characters[pos];
    }

    public void SetCharAt(int row, int col, byte ebcdic)
    {
        int pos = row * Cols + col;
        if (pos >= 0 && pos < _characters.Length)
        {
            _characters[pos] = ebcdic;
        }
    }

    public bool IsFieldAttributeAt(int row, int col)
    {
        int pos = row * Cols + col;
        if (pos < 0 || pos >= _attributes.Length) return false;
        byte a = _attributes[pos];
        return a >= 0x20 && a <= 0x3F; // a 5250 attribute byte occupies this cell
    }

    /// <summary>The 5250 attribute byte stored at a position (0 if none).</summary>
    public byte GetAttributeByteAt(int row, int col)
    {
        int pos = row * Cols + col;
        if (pos < 0 || pos >= _attributes.Length) return 0;
        return _attributes[pos];
    }

    /// <summary>True if a field begins exactly at this position (i.e. the preceding
    /// cell holds that field's leading attribute byte).</summary>
    public bool IsFieldStart(int row, int col)
    {
        foreach (var f in Fields)
            if (f.Row == row && f.Col == col) return true;
        return false;
    }

    public ScreenField? GetFieldAt(int row, int col)
    {
        foreach (var field in Fields)
        {
            if (field.ContainsPosition(row, col, Cols))
                return field;
        }
        return null;
    }

    public ScreenField? GetFieldForCursor()
    {
        return GetFieldAt(CursorRow, CursorCol);
    }

    public ScreenField? GetNextInputField(int row, int col)
    {
        int pos = row * Cols + col;
        ScreenField? best = null;
        int bestDist = int.MaxValue;

        foreach (var field in Fields)
        {
            if (field.Attribute.IsBypass) continue;

            int fPos = field.Row * Cols + field.Col;
            int dist = fPos - pos;
            if (dist <= 0) dist += Rows * Cols;

            if (dist < bestDist)
            {
                bestDist = dist;
                best = field;
            }
        }
        return best;
    }

    public ScreenField? GetPrevInputField(int row, int col)
    {
        int pos = row * Cols + col;
        ScreenField? best = null;
        int bestDist = int.MaxValue;

        foreach (var field in Fields)
        {
            if (field.Attribute.IsBypass) continue;

            int fPos = field.Row * Cols + field.Col;
            int dist = pos - fPos;
            if (dist <= 0) dist += Rows * Cols;

            if (dist < bestDist)
            {
                bestDist = dist;
                best = field;
            }
        }
        return best;
    }

    public void MoveCursorTo(int row, int col)
    {
        CursorRow = Math.Clamp(row, 0, Rows - 1);
        CursorCol = Math.Clamp(col, 0, Cols - 1);
        Version++;
        ScreenChanged?.Invoke();
    }

    public void MoveCursorForward()
    {
        CursorCol++;
        if (CursorCol >= Cols)
        {
            CursorCol = 0;
            CursorRow++;
            if (CursorRow >= Rows)
                CursorRow = 0;
        }
    }

    public void MoveCursorBack()
    {
        CursorCol--;
        if (CursorCol < 0)
        {
            CursorCol = Cols - 1;
            CursorRow--;
            if (CursorRow < 0)
                CursorRow = Rows - 1;
        }
    }

    public void SaveScreen()
    {
        _savedCharacters = (byte[])_characters.Clone();
        _savedAttributes = (byte[])_attributes.Clone();
        _savedFields = new List<ScreenField>(Fields);
        _savedCursorRow = CursorRow;
        _savedCursorCol = CursorCol;
    }

    public void RestoreScreen()
    {
        if (_savedCharacters != null)
        {
            Array.Copy(_savedCharacters, _characters, _characters.Length);
            Array.Copy(_savedAttributes!, _attributes, _attributes.Length);
            Fields.Clear();
            Fields.AddRange(_savedFields!);
            CursorRow = _savedCursorRow;
            CursorCol = _savedCursorCol;
            NotifyScreenChanged();
        }
    }

    // Copy host-written content out of the character buffer into each field's data
    // (the inverse of SyncFieldToBuffer). Called after parsing a display record so
    // that host-prefilled field values (e.g. *SAME on CHGUSRPRF) both display and
    // are sent back correctly. Uses SetData so the modified-data-tag is left clear.
    public void SyncBufferToFields()
    {
        foreach (var field in Fields)
            field.SetData(_characters, field.Row * Cols + field.Col, field.Length);
    }

    // Sync field data into the character buffer (for display)
    public void SyncFieldToBuffer(ScreenField field)
    {
        int pos = field.Row * Cols + field.Col;
        for (int i = 0; i < field.Length && pos + i < _characters.Length; i++)
        {
            _characters[pos + i] = field.GetCharAt(i);
        }
    }

    private void AdvanceBufferPosition()
    {
        _bufferCol++;
        if (_bufferCol >= Cols)
        {
            _bufferCol = 0;
            _bufferRow++;
            if (_bufferRow >= Rows)
                _bufferRow = 0;
        }
    }
}
