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
// and race-free. This process holds NO credentials and connects nowhere until a
// 'connect' tool call is made.

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
    settings => sessions.CreateSession(settings, dispatcher.Context));

builder.Services.AddSingleton(controller);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(McpTools).Assembly);

await builder.Build().RunAsync();
