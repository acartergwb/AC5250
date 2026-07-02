using AC5250.Model;

namespace AC5250.Protocol;

public class DataStreamParser
{
    private readonly ScreenBuffer _screen;

    // 5250 escape byte that precedes command codes in the data stream
    private const byte ESC = 0x04;

    public event Func<byte[], Task>? SendResponse;

    public DataStreamParser(ScreenBuffer screen)
    {
        _screen = screen;
    }

    public async Task ParseRecordAsync(byte[] record)
    {
        if (record.Length < TelnetConstants.HEADER_LENGTH)
            return;

        // 5250 data stream header:
        // Bytes 0-1: record length
        // Byte 2: record type (0x12 = General Data Stream)
        // Byte 3: reserved
        // Byte 4: variable (flags)
        // Byte 5: reserved
        // Byte 6-7: sequence number
        // Byte 8: SNA error flag
        // Byte 9: opcode

        byte opcode = record[9];

        switch (opcode)
        {
            case TelnetConstants.OPCODE_OUTPUT:
                ParseOutput(record, TelnetConstants.HEADER_LENGTH);
                break;

            case TelnetConstants.OPCODE_PUT_GET:
                ParseOutput(record, TelnetConstants.HEADER_LENGTH);
                // PUT_GET = output + invite: unlock keyboard and position cursor.
                _screen.InputInhibited = false;
                // If the host didn't leave the cursor on an enterable field, drop it
                // onto the first input field so the operator can type without Tab.
                var cursorField = _screen.GetFieldForCursor();
                if (cursorField == null || cursorField.Attribute.IsBypass)
                {
                    var firstField = _screen.GetNextInputField(0, 0);
                    if (firstField != null)
                        _screen.MoveCursorTo(firstField.Row, firstField.Col);
                }
                _screen.NotifyScreenChanged();
                break;

            case TelnetConstants.OPCODE_INVITE:
                // Host is inviting input - nothing special to parse
                _screen.InputInhibited = false;
                _screen.NotifyScreenChanged();
                break;

            case TelnetConstants.OPCODE_SAVE_SCREEN:
                _screen.SaveScreen();
                // The host requires the screen image back; otherwise it waits and the
                // keyboard stays locked ("X SYSTEM"). Common on windowed/wide flows.
                if (SendResponse != null)
                    await SendResponse.Invoke(DataStreamWriter.BuildSaveScreenResponse(_screen));
                break;

            case TelnetConstants.OPCODE_RESTORE_SCREEN:
                _screen.RestoreScreen();
                break;

            case TelnetConstants.OPCODE_READ_SCREEN:
                // Host wants the current screen contents back (e.g. F21 command line
                // does Save Screen then Read Screen). Without a reply it waits and the
                // keyboard stays locked ("X SYSTEM").
                if (SendResponse != null)
                    await SendResponse.Invoke(DataStreamWriter.BuildSaveScreenResponse(_screen));
                break;

            case TelnetConstants.OPCODE_TURN_ON_MSG_LIGHT:
                _screen.MessageWaiting = true;
                _screen.NotifyScreenChanged();
                break;

            case TelnetConstants.OPCODE_TURN_OFF_MSG_LIGHT:
                _screen.MessageWaiting = false;
                _screen.NotifyScreenChanged();
                break;

            case TelnetConstants.OPCODE_CANCEL_INVITE:
                _screen.InputInhibited = true;
                _screen.NotifyScreenChanged();
                break;
        }
    }

    private void ParseOutput(byte[] record, int offset)
    {
        while (offset < record.Length)
        {
            byte b = record[offset];

            if (b == ESC)
            {
                // Escape byte: next byte is a command code
                offset++;
                if (offset >= record.Length) break;

                byte cmd = record[offset];
                switch (cmd)
                {
                    case TelnetConstants.CMD_CLEAR_UNIT:
                    case TelnetConstants.CMD_CLEAR_UNIT_ALT:
                        _screen.Clear();
                        offset++;
                        break;

                    case TelnetConstants.CMD_CLEAR_FORMAT_TABLE:
                        _screen.ClearFormatTable();
                        offset++;
                        break;

                    case TelnetConstants.CMD_WRITE_TO_DISPLAY:
                        offset = ParseWriteToDisplay(record, offset + 1);
                        break;

                    case TelnetConstants.CMD_WRITE_STRUCTURED_FIELD:
                        offset = ParseWriteStructuredField(record, offset + 1);
                        break;

                    case TelnetConstants.CMD_READ_MDT_FIELDS:
                    case TelnetConstants.CMD_READ_INPUT_FIELDS:
                    case TelnetConstants.CMD_READ_SCREEN:
                        offset = record.Length; // consume remaining
                        break;

                    default:
                        offset++;
                        break;
                }
            }
            else
            {
                // Non-escape byte outside a command — skip
                offset++;
            }
        }

        // Capture host-written field content (e.g. prefilled *SAME values) into the
        // field objects so it displays and is transmitted back correctly.
        _screen.SyncBufferToFields();
        _screen.NotifyScreenChanged();
    }

    private int ParseWriteToDisplay(byte[] record, int offset)
    {
        // WTD has a control character pair (CC1, CC2)
        if (offset + 1 >= record.Length)
            return record.Length;

        byte cc1 = record[offset];
        byte cc2 = record[offset + 1];
        offset += 2;

        // CC1 flags
        if ((cc1 & 0x20) != 0) _screen.ResetMDT();
        if ((cc1 & 0x40) != 0) _screen.ClearFormatTable();

        // Parse orders and data until ESC (new command) or end of record
        while (offset < record.Length)
        {
            byte b = record[offset];

            // ESC byte signals a new command — return to outer loop
            if (b == ESC)
                break;

            switch (b)
            {
                case TelnetConstants.ORDER_SOH:
                    offset = ParseStartOfHeader(record, offset + 1);
                    break;

                case TelnetConstants.ORDER_SBA:
                    offset = ParseSetBufferAddress(record, offset + 1);
                    break;

                case TelnetConstants.ORDER_SF:
                    offset = ParseStartOfField(record, offset + 1);
                    break;

                case TelnetConstants.ORDER_RA:
                    offset = ParseRepeatToAddress(record, offset + 1);
                    break;

                case TelnetConstants.ORDER_EA:
                    offset = ParseEraseToAddress(record, offset + 1);
                    break;

                case TelnetConstants.ORDER_IC:
                    // Insert Cursor: 0x13 followed by row + col (1-based). Positions
                    // the cursor. Previously consumed only 1 byte, so the row/col were
                    // misparsed as data + an attribute byte (a reverse attribute that
                    // then bled across the screen as a green block).
                    if (offset + 2 < record.Length)
                    {
                        _screen.SetCursorAddress(record[offset + 1] - 1, record[offset + 2] - 1);
                        offset += 3;
                    }
                    else
                    {
                        offset = record.Length;
                    }
                    break;

                case TelnetConstants.ORDER_MC:
                    if (offset + 2 < record.Length)
                    {
                        int row = record[offset + 1];
                        int col = record[offset + 2];
                        _screen.SetCursorAddress(row, col);
                        offset += 3;
                    }
                    else
                    {
                        offset = record.Length;
                    }
                    break;

                case TelnetConstants.ORDER_TD:
                    offset = ParseTransparentData(record, offset + 1);
                    break;

                case TelnetConstants.ORDER_WEA:
                    // Write Extended Attribute - skip for now (2 bytes follow)
                    offset += 3;
                    break;

                default:
                    if (b >= 0x20 && b <= 0x3F)
                    {
                        // Display attribute byte — occupies a screen position as a blank
                        // and sets display characteristics for following data
                        _screen.WriteAttribute(b);
                    }
                    else
                    {
                        // Regular EBCDIC data byte - write to screen
                        _screen.WriteCharacter(b);
                    }
                    offset++;
                    break;
            }
        }

        return offset;
    }

    private int ParseStartOfHeader(byte[] record, int offset)
    {
        // SOH order: 0x01, a length byte, then `length` header data bytes. `offset`
        // points at the length byte, so skip the length byte itself PLUS the data.
        // (The previous `offset + length` was one byte short, leaving parsing on the
        // last SOH data byte — which, when it was 0x10, was read as a Transparent
        // Data order and swallowed the following orders, shifting the whole screen.)
        if (offset >= record.Length) return record.Length;

        int length = record[offset];
        return offset + length + 1;
    }

    private int ParseSetBufferAddress(byte[] record, int offset)
    {
        if (offset + 1 >= record.Length) return record.Length;

        int row = record[offset];
        int col = record[offset + 1];
        _screen.SetBufferAddress(row, col);
        return offset + 2;
    }

    private int ParseStartOfField(byte[] record, int offset)
    {
        // SF layout: FFW (2 bytes) + zero or more FCW pairs (2 bytes each) +
        //            attribute byte (0x20-0x3F) + field length (2 bytes, big-endian).
        if (offset + 1 >= record.Length) return record.Length;

        byte ffw1 = record[offset];
        byte ffw2 = record[offset + 1];
        offset += 2;

        // Skip any Field Control Words. FCWs sit between the FFW and the attribute
        // byte; the attribute byte is the first byte in the 0x20-0x3F range. Without
        // this, screens whose fields carry FCWs (e.g. command prompts like CHGUSRPRF)
        // misread the attribute + length, producing over-long fields and a shifted
        // screen. Fields without FCWs (e.g. the sign-on) are unaffected.
        while (offset + 1 < record.Length && (record[offset] < 0x20 || record[offset] > 0x3F))
            offset += 2;

        if (offset >= record.Length) return record.Length;
        byte attr = record[offset];
        offset++;

        // Field length (2 bytes, big-endian)
        if (offset + 1 >= record.Length) return record.Length;
        int fieldLength = (record[offset] << 8) | record[offset + 1];
        offset += 2;

        var fieldAttr = FieldAttribute.Decode(ffw1, ffw2, attr);
        _screen.DefineField(fieldAttr, fieldLength);

        return offset;
    }

    private int ParseRepeatToAddress(byte[] record, int offset)
    {
        if (offset + 2 >= record.Length) return record.Length;

        int row = record[offset];
        int col = record[offset + 1];
        byte ch = record[offset + 2];
        _screen.RepeatToAddress(row, col, ch);
        return offset + 3;
    }

    private int ParseEraseToAddress(byte[] record, int offset)
    {
        if (offset + 1 >= record.Length) return record.Length;

        int row = record[offset];
        int col = record[offset + 1];
        _screen.EraseToAddress(row, col);
        return offset + 2;
    }

    private int ParseTransparentData(byte[] record, int offset)
    {
        if (offset >= record.Length) return record.Length;

        int length = record[offset];
        offset++;

        for (int i = 0; i < length && offset < record.Length; i++, offset++)
        {
            _screen.WriteCharacterRaw(record[offset]);
        }

        return offset;
    }

    private int ParseWriteStructuredField(byte[] record, int offset)
    {
        // WSF contains one or more structured fields
        while (offset + 3 < record.Length)
        {
            int sfLength = (record[offset] << 8) | record[offset + 1];
            if (sfLength < 4 || offset + sfLength > record.Length)
                break;

            byte classType = record[offset + 2];
            byte type = record[offset + 3];

            if (classType == TelnetConstants.WSF_5250_QUERY && type == TelnetConstants.WSF_5250_QUERY_STATION)
            {
                _ = SendQueryReplyAsync();
            }

            offset += sfLength;
        }

        return record.Length; // consume rest
    }

    private async Task SendQueryReplyAsync()
    {
        if (SendResponse == null) return;

        var reply = BuildQueryReply();
        await SendResponse.Invoke(reply);
    }

    private byte[] BuildQueryReply()
    {
        bool wide = _screen.Cols > 80;

        // Build a 5250 query reply structured field
        var data = new List<byte>();

        // Record header (10 bytes)
        // Will be filled in by caller or we build a complete record
        data.AddRange(new byte[] { 0x00, 0x00, 0x12, 0xA0, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00 });

        // Query reply structured field
        var sf = new List<byte>();

        // Workstation control unit header
        sf.Add(0x00); // SF length high (filled later)
        sf.Add(0x00); // SF length low (filled later)
        sf.Add(TelnetConstants.WSF_5250_QUERY); // class
        sf.Add(0x70); // type - query response

        // Flag byte
        sf.Add(0x80); // response

        // Workstation type
        sf.Add(0x06); // rows/cols capability
        sf.Add(0x00);

        // Number of rows
        sf.Add((byte)(wide ? 27 : 24));
        // Number of columns
        sf.Add((byte)(wide ? 132 : 80));

        // Controller hardware info
        sf.Add(0x01); // # of input fields supported
        sf.Add(0x01);
        sf.Add(0x00); // controller code level

        // Workstation type: 3179-2 or 3477-FC
        if (wide)
        {
            sf.AddRange(new byte[] { 0x03, 0x04, 0x07, 0x07 }); // device type
        }
        else
        {
            sf.AddRange(new byte[] { 0x03, 0x01, 0x07, 0x09 }); // device type
        }

        sf.Add(0x03); // keyboard type: typewriter
        sf.Add(0x00); // extended keyboard
        sf.Add(0x00); // reserved
        sf.Add(0x00); // serial number
        sf.Add(0x00);
        sf.Add(0x00);
        sf.Add(0x00);

        // word/char cap
        sf.Add(0x01); // device supports colors? 0=no, 1=yes
        sf.Add(0x00); // grid line support
        sf.Add(0x00); // reserved

        // Pad to at least expected length
        while (sf.Count < 60)
            sf.Add(0x00);

        // Fill in SF length
        sf[0] = (byte)(sf.Count >> 8);
        sf[1] = (byte)(sf.Count & 0xFF);

        data.AddRange(sf);

        // Update record length in header
        data[0] = (byte)(data.Count >> 8);
        data[1] = (byte)(data.Count & 0xFF);

        return data.ToArray();
    }
}
