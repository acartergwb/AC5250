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

        // Shut down fast. The MCP client holds a long-lived SSE stream that never drains on
        // its own, so the host's graceful-shutdown wait is pure delay on exit — and it governs
        // BOTH StopAsync and DisposeAsync's internal stop (the default 30s was what made
        // closing the app hang ~3s). Cap it hard: 250ms is imperceptible but still lets a
        // genuinely in-flight request finish before connections are aborted.
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromMilliseconds(250));

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
            // StopAsync (and the stop DisposeAsync runs internally) are both bounded by the
            // 500ms ShutdownTimeout configured in StartAsync, so this returns quickly instead of
            // waiting on the SSE stream. ConfigureAwait(false) keeps the continuations OFF the UI
            // thread that OnFormClosing blocks on, so its .Wait() can't deadlock against them.
            try { await _app.StopAsync().ConfigureAwait(false); } catch { /* best effort */ }
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }
    }
}
