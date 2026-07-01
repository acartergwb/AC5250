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

            // Field data in EBCDIC
            var fieldData = field.GetData();
            data.AddRange(fieldData);
        }

        FillRecordLength(data);
        return data.ToArray();
    }

    private static void FillRecordLength(List<byte> data)
    {
        data[0] = (byte)(data.Count >> 8);
        data[1] = (byte)(data.Count & 0xFF);
    }
}
