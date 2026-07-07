using AC5250.Mcp;
using AC5250.Session;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AC5250.Hosting;

/// <summary>
/// Hosts the MCP server over loopback HTTP inside the WinForms process, so an MCP
/// client (Claude) drives the very same sessions the user sees in the window.
///
/// Security: bound to 127.0.0.1 and additionally rejects any request whose remote
/// address is not loopback (defense in depth). The server exists only while the app
/// is running and is reachable only from this machine, so no auth token is used.
/// Note: any process running as the current user can therefore reach it — the same
/// trust boundary as the user's own desktop session.
/// </summary>
internal sealed class McpHost : IAsyncDisposable
{
    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}/mcp";

    private readonly EmulatorController _controller;
    private WebApplication? _app;

    public McpHost(SessionManager sessions, Control uiControl, SynchronizationContext uiContext, int port)
    {
        Port = port;

        var marshal = new ControlThreadMarshal(uiControl);
        _controller = new EmulatorController(
            sessions,
            marshal,
            settings => sessions.CreateSession(settings, uiContext),
            // Sign-on credentials: Windows Credential Manager on this machine, with
            // environment variables as a fallback/override. The password is read only to
            // fill the field, never returned.
            AC5250.Security.CredentialSources.CreateDefault());
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{Port}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services.AddSingleton(_controller);
        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly(typeof(McpTools).Assembly);

        var app = builder.Build();

        // Defense in depth: even though we bind to 127.0.0.1, reject anything whose
        // remote address isn't loopback so the endpoint can never serve a non-local peer.
        app.Use(async (ctx, next) =>
        {
            var ip = ctx.Connection.RemoteIpAddress;
            if (ip is null || !System.Net.IPAddress.IsLoopback(ip))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next();
        });

        app.MapMcp("/mcp");

        _app = app;
        await app.StartAsync();
    }

    /// <summary>Ready-to-paste command to register this server with Claude Code.</summary>
    public string ClaudeAddCommand =>
        $"claude mcp add --transport http ac5250 {Url}";

    public async ValueTask DisposeAsync()
    {
        if (_app != null)
        {
            // Stop quickly. On shutdown we don't want Kestrel's graceful drain — it would wait
            // the full timeout for the MCP client's long-lived SSE connection to close — so give
            // it only a brief grace period, then abort. ConfigureAwait(false) keeps these
            // continuations OFF the UI thread: OnFormClosing blocks on .Wait(), and without this
            // the awaited continuation (posted back to the WinForms context) would deadlock
            // against that Wait until it timed out — which was the slow app close.
            try { await _app.StopAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false); }
            catch { /* best effort */ }
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }
    }
}
