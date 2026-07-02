# AC5250

A TN5250 terminal emulator for IBM i / AS-400 (iSeries) systems, written in C# /
.NET 10 WinForms — **with MCP integration** so an AI client (Claude) can view the
screen and drive the keyboard.

> Base emulator by [@stubbornmarlin3](https://github.com/stubbornmarlin3/AC5250).
> This fork adds the Model Context Protocol layer described below.

---

## Solution layout

| Project | Target | Purpose |
|---|---|---|
| `AC5250.Core` | `net10.0` | The TN5250 engine — telnet/socket client, 5250 data-stream parser/writer, EBCDIC, screen buffer, fields, sessions. No UI dependency. |
| `AC5250.App` | `net10.0-windows` | The WinForms desktop emulator, **plus** the in-process HTTP MCP host. |
| `AC5250.Mcp` | `net10.0` | Host-agnostic MCP layer: the shared `EmulatorController`, screen serialization, and the tool definitions. Used by both hosts. |
| `AC5250.Headless` | `net10.0` | A console MCP server (`ac5250-mcp`) that exposes the engine over **stdio**, no window. |

The MCP layer is deliberately host-agnostic: the desktop app and the headless
server construct the same `EmulatorController` with (a) a shared `SessionManager`,
(b) an `IThreadMarshal` that runs work on the session-owning thread, and (c) a
factory that wires each session to that thread's dispatcher. That single seam is
what makes concurrent MCP access thread-safe against the socket-driven parser.

## Prerequisites & building

Requires the **.NET 10 SDK**. The WinForms build needs the `Microsoft.WindowsDesktop.App`
runtime; the in-process MCP host needs `Microsoft.AspNetCore.App` (both ship with the SDK).

```sh
dotnet build AC5250.slnx -c Debug
```

> **Runtime note:** the produced `AC5250.exe` / `ac5250-mcp.exe` are framework-dependent —
> they need the .NET 10 runtime resolvable at launch. If .NET 10 is installed only in a
> non-default location, set `DOTNET_ROOT` (or run via `dotnet <dll>`). For distribution,
> publish self-contained: `dotnet publish src/AC5250.App -c Release -r win-x64 --self-contained`.

## Running the desktop emulator

```sh
dotnet run --project src/AC5250.App
```

`Ctrl+N` to connect; `F1` for the 5250 key map.

---

## MCP integration

Seven tools are exposed by **both** hosts:

| Tool | What it does |
|---|---|
| `list_sessions` | List open sessions (id, title, host, connected, active). |
| `get_screen` | The screen as a text grid + cursor, keyboard/status flags, and the input-field list. |
| `connect` | Open a new TN5250 session to a host and wait for the first screen. |
| `disconnect` | Close a session. |
| `send_text` | Type text at the cursor (client-side; does not submit). |
| `set_field` | Set an input field's value by its index from `get_screen`. |
| `press_key` | Press a 5250 key — AID keys (`Enter`, `F1`-`F24`, `Clear`, `Attn`, `SysReq`, `Help`, `Print`, `PageUp`, `PageDown`) submit to the host and wait for the repaint; navigation/edit keys (`Tab`, `Home`, arrows, `FieldExit`, `EraseInput`, `Reset`, …) act locally. |

A typical loop: `get_screen` → `set_field`/`send_text` → `press_key Enter` → `get_screen`.

### Mode 1 — in-process (drive the visible desktop app)

Launch the emulator, then **Tools → Start MCP Server** (or launch with `--mcp`).
The server binds to `127.0.0.1:8250` and issues a per-launch bearer token. The
**Tools → MCP Connection Info…** dialog shows a ready-to-paste command:

```sh
claude mcp add --transport http ac5250 http://127.0.0.1:8250/mcp \
  --header "Authorization: Bearer <token-from-dialog>"
```

Launch flags: `--mcp` (auto-start), `--mcp-port <n>`, `--mcp-token <t>` (automation
only — CLI args are visible in the process list).

With this mode you watch Claude drive the real terminal window.

### Mode 2 — headless (stdio, no window)

```sh
claude mcp add ac5250 -- dotnet /abs/path/src/AC5250.Headless/bin/Debug/net10.0/ac5250-mcp.dll
# or, against a self-contained/published build, point directly at ac5250-mcp.exe
```

Same tools, no GUI. Best for pure automation.

---

## Security model

This tooling can drive a **live IBM i session** (sign-on, commands, screen data),
so it is treated as sensitive:

- **Localhost only.** The HTTP host binds `127.0.0.1` and requires a per-launch
  bearer token (constant-time compared). The token is generated in-process and
  never persisted.
- **Manual start.** The in-process server never starts on its own except with the
  explicit `--mcp` flag; the default is off.
- **Hidden fields are masked.** Non-display (password) fields are never surfaced in
  `get_screen` text or field content — only their presence is reported.
- **No credentials in the tool.** `connect` takes a host/port/device; it holds no
  stored passwords. Sign-on is performed on-screen like a normal 5250 session.
- **Production caution.** Do **not** point `connect` at a production IBM i (or any
  PCI-scoped environment) without explicit authorization. Screen contents pass into
  the model's context.

## Known limitations (inherited from the base emulator)

The TN5250 engine is a solid first cut, not a hardened emulator. Start-of-Field
parsing ignores optional FCWs, Write-Extended-Attribute is skipped, the display-attribute
decode is approximate, and only the 5250 Query structured field is answered. Complex
host screens may not render perfectly; verify against a real host before relying on it.
