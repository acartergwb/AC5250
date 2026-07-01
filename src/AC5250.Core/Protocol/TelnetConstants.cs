namespace AC5250.Protocol;

public static class TelnetConstants
{
    // Telnet commands
    public const byte IAC = 0xFF;
    public const byte DONT = 0xFE;
    public const byte DO = 0xFD;
    public const byte WONT = 0xFC;
    public const byte WILL = 0xFB;
    public const byte SB = 0xFA;   // Subnegotiation Begin
    public const byte SE = 0xF0;   // Subnegotiation End
    public const byte EOR = 0xEF;  // End of Record

    // Telnet options
    public const byte OPT_BINARY = 0x00;
    public const byte OPT_ECHO = 0x01;
    public const byte OPT_TERMINAL_TYPE = 0x18;
    public const byte OPT_EOR = 0x19;
    public const byte OPT_NEW_ENVIRON = 0x27;

    // Subnegotiation sub-commands
    public const byte TERMINAL_TYPE_IS = 0x00;
    public const byte TERMINAL_TYPE_SEND = 0x01;

    // NEW-ENVIRON sub-commands
    public const byte NEW_ENVIRON_IS = 0x00;
    public const byte NEW_ENVIRON_SEND = 0x01;
    public const byte NEW_ENVIRON_INFO = 0x02;
    public const byte NEW_ENVIRON_VAR = 0x00;
    public const byte NEW_ENVIRON_VALUE = 0x01;
    public const byte NEW_ENVIRON_ESC = 0x02;
    public const byte NEW_ENVIRON_USERVAR = 0x03;

    // 5250 data stream header
    public const int HEADER_LENGTH = 10;

    // 5250 record types (byte 9 of header, the opcode)
    public const byte OPCODE_NO_OP = 0x00;
    public const byte OPCODE_INVITE = 0x01;
    public const byte OPCODE_OUTPUT = 0x02;
    public const byte OPCODE_PUT_GET = 0x03;
    public const byte OPCODE_SAVE_SCREEN = 0x04;
    public const byte OPCODE_RESTORE_SCREEN = 0x05;
    public const byte OPCODE_READ_IMMEDIATE = 0x06;
    public const byte OPCODE_CANCEL_INVITE = 0x0A;
    public const byte OPCODE_TURN_ON_MSG_LIGHT = 0x07;
    public const byte OPCODE_TURN_OFF_MSG_LIGHT = 0x08;

    // 5250 data stream orders (within WTD)
    public const byte ORDER_SOH = 0x01;   // Start of Header
    public const byte ORDER_RA = 0x02;    // Repeat to Address
    public const byte ORDER_EA = 0x03;    // Erase to Address
    public const byte ORDER_TD = 0x10;    // Transparent Data
    public const byte ORDER_SBA = 0x11;   // Set Buffer Address
    public const byte ORDER_WEA = 0x12;   // Write Extended Attribute
    public const byte ORDER_IC = 0x13;    // Insert Cursor
    public const byte ORDER_MC = 0x14;    // Move Cursor
    public const byte ORDER_SF = 0x1D;    // Start of Field

    // 5250 Write commands
    public const byte CMD_WRITE_TO_DISPLAY = 0x11;
    public const byte CMD_CLEAR_UNIT = 0x40;
    public const byte CMD_CLEAR_FORMAT_TABLE = 0x50;
    public const byte CMD_CLEAR_UNIT_ALT = 0x20;
    public const byte CMD_WRITE_STRUCTURED_FIELD = 0xF3;
    public const byte CMD_READ_MDT_FIELDS = 0x52;
    public const byte CMD_READ_INPUT_FIELDS = 0x42;
    public const byte CMD_READ_SCREEN = 0x62;

    // WSF structured field types
    public const byte WSF_5250_QUERY = 0xD9;
    public const byte WSF_5250_QUERY_STATION = 0x70;

    // Terminal type strings
    public const string TERMINAL_IBM_3179_2 = "IBM-3179-2";     // 24x80
    public const string TERMINAL_IBM_3477_FC = "IBM-3477-FC";   // 27x132
}
