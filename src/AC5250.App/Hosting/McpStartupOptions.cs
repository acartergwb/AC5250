namespace AC5250.Hosting;

/// <summary>
/// Command-line options for the MCP server at launch:
///   AC5250.exe --mcp                 force-start the MCP server (also on by default)
///   AC5250.exe --mcp-port 8300       use a specific port
/// The server binds to loopback only and uses no auth token.
/// </summary>
public sealed class McpStartupOptions
{
    public bool AutoStart { get; init; }
    public int? Port { get; init; }

    /// <summary>Optional path to append the host 5250-record trace to (diagnostics).
    /// Never records what the operator types (sent records are excluded).</summary>
    public string? LogFile { get; init; }

    public static McpStartupOptions Parse(string[] args)
    {
        bool auto = false;
        int? port = null;
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
                case "--logfile":
                    if (i + 1 < args.Length) logFile = args[++i];
                    break;
            }
        }

        return new McpStartupOptions { AutoStart = auto, Port = port, LogFile = logFile };
    }
}
