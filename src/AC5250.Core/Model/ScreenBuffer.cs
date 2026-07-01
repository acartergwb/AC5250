using AC5250.Protocol;

namespace AC5250.Model;

public class ScreenBuffer
{
    public int Rows { get; }
    public int Cols { get; }

    // Character buffer (EBCDIC)
    private readonly byte[] _characters;
    private readonly byte[] _attributes;

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
        // 5250 uses 1-based addressing
        _bufferRow = Math.Max(0, row - 1);
        _bufferCol = Math.Max(0, col - 1);
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

    public void WriteAttribute(byte displayAttr)
    {
        // A 5250 display attribute (0x20-0x3F) occupies a screen position as a
        // blank and sets the display characteristics for the data that follows.
        int pos = _bufferRow * Cols + _bufferCol;
        if (pos >= 0 && pos < _characters.Length)
        {
            _characters[pos] = 0x40; // display as space
            _attributes[pos] = displayAttr;
        }
        AdvanceBufferPosition();
    }

    public void WriteCharacter(byte ebcdic)
    {
        int pos = _bufferRow * Cols + _bufferCol;
        if (pos >= 0 && pos < _characters.Length)
        {
            _characters[pos] = ebcdic;
        }
        AdvanceBufferPosition();
    }

    public void WriteCharacterRaw(byte rawByte)
    {
        // Write without EBCDIC interpretation
        int pos = _bufferRow * Cols + _bufferCol;
        if (pos >= 0 && pos < _characters.Length)
        {
            _characters[pos] = rawByte;
        }
        AdvanceBufferPosition();
    }

    public void DefineField(FieldAttribute attr, int length)
    {
        // The attribute byte occupies the current buffer position
        int attrPos = _bufferRow * Cols + _bufferCol;
        if (attrPos >= 0 && attrPos < _attributes.Length)
        {
            _attributes[attrPos] = 0xFF; // mark as field attribute position
        }

        // Field data starts at the next position
        AdvanceBufferPosition();

        var field = new ScreenField(_bufferRow, _bufferCol, length, attr, Cols);
        Fields.Add(field);

        // Advance buffer past the field
        for (int i = 0; i < length; i++)
        {
            AdvanceBufferPosition();
        }
    }

    public void RepeatToAddress(int row, int col, byte ch)
    {
        int targetRow = Math.Max(0, row - 1);
        int targetCol = Math.Max(0, col - 1);
        int targetPos = targetRow * Cols + targetCol;
        int currentPos = _bufferRow * Cols + _bufferCol;
        int total = _characters.Length;

        if (targetPos <= currentPos)
        {
            // Wrap around
            while (currentPos < total)
            {
                _characters[currentPos] = ch;
                currentPos++;
            }
            currentPos = 0;
        }

        while (currentPos < targetPos && currentPos < total)
        {
            _characters[currentPos] = ch;
            currentPos++;
        }

        _bufferRow = targetRow;
        _bufferCol = targetCol;
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
        return _attributes[pos] == 0xFF;
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
