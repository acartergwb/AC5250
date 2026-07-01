using System.Security.Cryptography;
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
/// Security: bound to 127.0.0.1 only, and every request to the MCP endpoint must
/// carry a per-launch bearer token. The token is generated in-process and never
/// persisted. The server is started manually by the user (Tools menu), never
/// automatically.
/// </summary>
internal sealed class McpHost : IAsyncDisposable
{
    public int Port { get; }
    public string Token { get; }
    public string Url => $"http://127.0.0.1:{Port}/mcp";

    private readonly EmulatorController _controller;
    private WebApplication? _app;

    public McpHost(SessionManager sessions, Control uiControl, SynchronizationContext uiContext, int port, string? token = null)
    {
        Port = port;
        Token = string.IsNullOrWhiteSpace(token) ? GenerateToken() : token!;

        var marshal = new ControlThreadMarshal(uiControl);
        _controller = new EmulatorController(
            sessions,
            marshal,
            settings => sessions.CreateSession(settings, uiContext));
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

        // Loopback bind + bearer-token guard on the MCP endpoint.
        string expected = "Bearer " + Token;
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/mcp"))
            {
                var provided = ctx.Request.Headers.Authorization.ToString();
                if (!CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.UTF8.GetBytes(provided),
                        System.Text.Encoding.UTF8.GetBytes(expected)))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
            }
            await next();
        });

        app.MapMcp("/mcp");

        _app = app;
        await app.StartAsync();
    }

    /// <summary>Ready-to-paste command to register this server with Claude Code.</summary>
    public string ClaudeAddCommand =>
        $"claude mcp add --transport http ac5250 {Url} --header \"Authorization: Bearer {Token}\"";

    private static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app != null)
        {
            try { await _app.StopAsync(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
            await _app.DisposeAsync();
            _app = null;
        }
    }
}
