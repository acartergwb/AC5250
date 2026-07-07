using AC5250.Model;

namespace AC5250.Protocol;

public static class DataStreamWriter
{
    public static byte[] BuildReadResponse(ScreenBuffer screen, AidKey aidKey)
    {
        var data = new List<byte>();

        // 5250 data stream header (10 bytes)
        // Bytes 0-1: record length (filled later)
        // Bytes 2-3: 0x12 0xA0 (GDS variable length record)
        // Bytes 4-5: reserved
        // Byte 6: 0x04 (variable header length)
        // Bytes 7-9: flags/opcode (0x00 for response)
        data.AddRange(new byte[] { 0x00, 0x00, 0x12, 0xA0, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00 });

        // Row and column of cursor (1-based)
        data.Add((byte)(screen.CursorRow + 1));
        data.Add((byte)(screen.CursorCol + 1));

        // AID key
        data.Add((byte)aidKey);

        // For aid keys that don't transmit data, stop here
        if (aidKey == AidKey.Clear || aidKey == AidKey.Attn || aidKey == AidKey.SysReq)
        {
            FillRecordLength(data);
            return data.ToArray();
        }

        // Append modified fields (MDT set)
        foreach (var field in screen.Fields)
        {
            if (!field.Modified || field.Attribute.IsBypass)
                continue;

            // SBA order to position
            data.Add(TelnetConstants.ORDER_SBA);
            data.Add((byte)(field.Row + 1));
            data.Add((byte)(field.Col + 1));

            // Field data in EBCDIC (GetData already trims trailing blanks). For alpha input,
            // also strip LEADING blanks so a value typed mid-field is left-adjusted (ACS-style
            // "smart" entry) — otherwise S2K receives e.g. "   1023" instead of "1023". Numeric
            // fields are right-justified by the host, so their content is left untouched.
            var fieldData = field.GetData();
            if (!IsNumericField(field.Attribute.Format))
            {
                // Strip leading blanks — both EBCDIC space (0x40, typed) and null (0x00, the
                // untouched cells left when the cursor is click-positioned to the right). Both
                // mean "empty" (GetData trims the same pair from the tail).
                int start = 0;
                while (start < fieldData.Length && (fieldData[start] == 0x40 || fieldData[start] == 0x00)) start++;
                if (start > 0) fieldData = fieldData[start..];
            }
            data.AddRange(fieldData);
        }

        FillRecordLength(data);
        return data.ToArray();
    }

    private static bool IsNumericField(FieldFormat f) =>
        f is FieldFormat.NumericOnly or FieldFormat.NumericShift
          or FieldFormat.DigitsOnly or FieldFormat.SignedNumeric;

    /// <summary>
    /// Build the response to a Save Screen request (opcode 0x04): a data stream that
    /// recreates the current screen, which the host stores and replays on a later
    /// Restore Screen. Per RFC 1205 the client MUST send the screen image back —
    /// without it the host waits forever and the keyboard stays locked ("X SYSTEM").
    /// </summary>
    public static byte[] BuildSaveScreenResponse(ScreenBuffer screen)
    {
        var data = new List<byte>(screen.Rows * screen.Cols + 32)
        {
            // Header, opcode 0x04 (save screen)
            0x00, 0x00, 0x12, 0xA0, 0x00, 0x00, 0x04, 0x00, 0x00, 0x04,
            // Write-To-Display (ESC + WTD) + control chars, then home the buffer
            0x04, TelnetConstants.CMD_WRITE_TO_DISPLAY, 0x00, 0x00,
            TelnetConstants.ORDER_SBA, 0x01, 0x01,
        };

        // Emit the whole buffer: the attribute byte at field/attribute positions,
        // the character elsewhere. Replaying this recreates the visible screen.
        for (int r = 0; r < screen.Rows; r++)
        {
            for (int c = 0; c < screen.Cols; c++)
            {
                byte b = screen.IsFieldAttributeAt(r, c)
                    ? screen.GetAttributeByteAt(r, c)
                    : screen.GetCharAt(r, c);
                data.Add(b == 0x00 ? (byte)0x40 : b);
            }
        }

        FillRecordLength(data);
        return data.ToArray();
    }

    /// <summary>
    /// Build the response to Read Screen Immediate (opcode 0x08): the raw contents of
    /// the display's regeneration buffer, with NO Write-To-Display/SBA framing. Unlike
    /// Save Screen (0x04) — whose reply is a replayable command stream — a Read reply is
    /// pure buffer data. Sending the 7-byte "ESC WTD 00 00 SBA 01 01" prefix here made
    /// the host read the image 7 bytes late, so its next redraw was shifted 7 columns
    /// right (header values wrapped off the right edge onto the next row).
    /// </summary>
    public static byte[] BuildReadScreenResponse(ScreenBuffer screen)
    {
        var data = new List<byte>(screen.Rows * screen.Cols + 16)
        {
            // Header, opcode 0x00 (response). No ESC/WTD/SBA — the buffer follows directly.
            0x00, 0x00, 0x12, 0xA0, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00,
        };

        for (int r = 0; r < screen.Rows; r++)
        {
            for (int c = 0; c < screen.Cols; c++)
            {
                byte b = screen.IsFieldAttributeAt(r, c)
                    ? screen.GetAttributeByteAt(r, c)
                    : screen.GetCharAt(r, c);
                data.Add(b == 0x00 ? (byte)0x40 : b);
            }
        }

        FillRecordLength(data);
        return data.ToArray();
    }

    /// <summary>
    /// Acknowledge a Cancel Invite (opcode 0x0A). Per the 5250 protocol (see tn5250's
    /// tn5250_session_cancel_invite), when the host cancels an invited read the terminal
    /// must send an empty record back with the Cancel-Invite opcode. Without this ack the
    /// host blocks waiting for it — the keyboard stays locked and the screen freezes
    /// (e.g. after closing the F21 command line, leaving the input box stuck on screen).
    /// </summary>
    public static byte[] BuildCancelInviteResponse()
        => new byte[] { 0x00, 0x0A, 0x12, 0xA0, 0x00, 0x00, 0x04, 0x00, 0x00,
                        TelnetConstants.OPCODE_CANCEL_INVITE };

    private static void FillRecordLength(List<byte> data)
    {
        data[0] = (byte)(data.Count >> 8);
        data[1] = (byte)(data.Count & 0xFF);
    }
}
