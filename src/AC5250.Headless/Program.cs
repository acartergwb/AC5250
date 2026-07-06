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
    // Sign-on credentials come from this machine's Windows Credential Manager
    // (DPAPI, per-user); the password is read here only to fill the field, never returned.
    // Guarded to Windows (the only place a Credential Manager exists); elsewhere signon is
    // simply unavailable.
    settings => OperatingSystem.IsWindows()
        ? AC5250.Security.CredentialStore.Get(settings.HostName)
        : null);

builder.Services.AddSingleton(controller);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(McpTools).Assembly);

await builder.Build().RunAsync();
