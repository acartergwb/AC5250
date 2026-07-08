using AC5250.Headless;
using AC5250.Mcp;
using AC5250.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// AC5250 headless MCP server.
//
// Exposes the AC5250 5250-terminal engine as an MCP server over stdio, with no
// GUI window. Point an MCP client at it, e.g.:
//   claude mcp add ac5250 -- <path>\ac5250-mcp.exe
//
// The emulator's screen state is owned by a single dispatcher thread; the MCP
// tools and the TN5250 data-stream parser both run on it, so access is serial
// and race-free. This process stores NO credentials of its own and connects
// nowhere until a 'connect' tool call is made. The 'signon' tool reads the
// password from this machine's Windows Credential Manager on demand to fill the
// hidden field locally — it is never a tool parameter and never returned.

var builder = Host.CreateApplicationBuilder(args);

// stdio is the JSON-RPC channel: all logging MUST go to stderr, never stdout.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// The dispatcher thread owns all screen/session state.
var dispatcher = new DispatcherThread();
var sessions = new SessionManager();
var controller = new EmulatorController(
    sessions,
    dispatcher,
    settings => sessions.CreateSession(settings, dispatcher.Context),
    // Sign-on credentials from the platform-appropriate source: Windows Credential Manager
    // on Windows, environment variables (AC5250_<CONNECTION>_USER / _PASSWORD, keyed by the
    // saved connection) elsewhere. The password is read on demand only to fill the field,
    // never stored here, never returned.
    AC5250.Security.CredentialSources.CreateDefault());

builder.Services.AddSingleton(controller);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(McpTools).Assembly);

await builder.Build().RunAsync();
