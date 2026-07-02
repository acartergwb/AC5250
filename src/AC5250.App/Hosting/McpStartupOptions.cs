namespace AC5250.Hosting;

/// <summary>
/// Command-line options for auto-starting the MCP server at launch:
///   AC5250.exe --mcp                 start the MCP server on the default port
///   AC5250.exe --mcp --mcp-port 8300 ...on a specific port
///   AC5250.exe --mcp --mcp-token X   ...with a fixed bearer token (automation only;
///                                       avoid on shared machines — CLI args are visible
///                                       in the process list)
/// </summary>
public sealed class McpStartupOptions
{
    public bool AutoStart { get; init; }
    public int? Port { get; init; }
    public string? Token { get; init; }

    /// <summary>Optional path to append the host 5250-record trace to (diagnostics).
    /// Never records what the operator types (sent records are excluded).</summary>
    public string? LogFile { get; init; }

    public static McpStartupOptions Parse(string[] args)
    {
        bool auto = false;
        int? port = null;
        string? token = null;
        string? logFile = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--mcp":
                    auto = true;
                    break;
                case "--mcp-port":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int p)) port = p;
                    break;
                case "--mcp-token":
                    if (i + 1 < args.Length) token = args[++i];
                    break;
                case "--logfile":
                    if (i + 1 < args.Length) logFile = args[++i];
                    break;
            }
        }

        return new McpStartupOptions { AutoStart = auto, Port = port, Token = token, LogFile = logFile };
    }
}
