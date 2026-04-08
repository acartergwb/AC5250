namespace AC5250.Model;

public class ScreenField
{
    public int Row { get; set; }
    public int Col { get; set; }
    public int Length { get; set; }
    public FieldAttribute Attribute { get; set; } = new();
    public bool Modified { get; set; }

    private readonly byte[] _data;
    private readonly int _cols;

    public ScreenField(int row, int col, int length, FieldAttribute attr, int screenCols)
    {
        Row = row;
        Col = col;
        Length = length;
        Attribute = attr;
        Modified = attr.IsModified;
        _data = new byte[length];
        _cols = screenCols;

        // Initialize with EBCDIC spaces (0x40)
        Array.Fill(_data, (byte)0x40);
    }

    public byte GetCharAt(int index)
    {
        if (index < 0 || index >= Length) return 0x40;
        return _data[index];
    }

    public void SetCharAt(int index, byte ebcdicChar)
    {
        if (index < 0 || index >= Length) return;
        _data[index] = ebcdicChar;
        Modified = true;
    }

    public void InsertCharAt(int index, byte ebcdicChar)
    {
        if (index < 0 || index >= Length) return;
        // Shift right
        for (int i = Length - 1; i > index; i--)
        {
            _data[i] = _data[i - 1];
        }
        _data[index] = ebcdicChar;
        Modified = true;
    }

    public void DeleteCharAt(int index)
    {
        if (index < 0 || index >= Length) return;
        // Shift left
        for (int i = index; i < Length - 1; i++)
        {
            _data[i] = _data[i + 1];
        }
        _data[Length - 1] = 0x40; // fill end with space
        Modified = true;
    }

    public byte[] GetData()
    {
        // Return a copy, trimming trailing spaces
        int end = Length;
        while (end > 0 && _data[end - 1] == 0x40)
            end--;

        var result = new byte[end];
        Array.Copy(_data, result, end);
        return result;
    }

    public void SetData(byte[] data, int offset, int length)
    {
        int copyLen = Math.Min(length, Length);
        Array.Copy(data, offset, _data, 0, copyLen);
    }

    public void ClearData()
    {
        Array.Fill(_data, (byte)0x40);
        Modified = false;
    }

    public bool ContainsPosition(int row, int col, int screenCols)
    {
        int fieldStart = Row * screenCols + Col;
        int pos = row * screenCols + col;
        return pos >= fieldStart && pos < fieldStart + Length;
    }

    public int GetIndexForPosition(int row, int col, int screenCols)
    {
        int fieldStart = Row * screenCols + Col;
        int pos = row * screenCols + col;
        return pos - fieldStart;
    }
}
