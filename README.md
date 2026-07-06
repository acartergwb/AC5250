# AC5250

A TN5250 terminal emulator for IBM i / AS-400 (iSeries) systems, written in C# /
.NET 10 WinForms — **with MCP integration** so an AI client (Claude) can view the
screen and drive the keyboard.

> Base emulator by [@stubbornmarlin3](https://github.com/stubbornmarlin3/AC5250).
> This fork adds the Model Context Protocol layer described below.

---

## Install & updates

Download **`Setup.exe`** from the [latest release](https://github.com/acartergwb/AC5250/releases/latest)
and run it **once**. It installs per-user to `%LocalAppData%\AC5250` and creates Start/Desktop
shortcuts you can pin (taskbar/Start/desktop) — the pinned icon always launches the current
version.

After that it's self-updating: on each launch the app checks GitHub Releases and, if a newer
version exists, downloads it (delta) and relaunches into it — no re-download, no re-pin. Powered
by [Velopack](https://velopack.io/).

Releases are produced by the `Release` workflow when a `v*` tag is pushed (`git tag v2.0.0 && git push origin v2.0.0`).
(Installers are unsigned, so Windows SmartScreen may warn on first run — *More info → Run anyway*.)

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

Nine tools are exposed by **both** hosts:

| Tool | What it does |
|---|---|
| `list_sessions` | List open sessions (id, title, host, connected, active). |
| `get_screen` | The screen as a text grid + cursor, keyboard/status flags, and the input-field list. An instant read — if `keyboardInhibited` is true the host is still working. |
| `connect` | Open a new TN5250 session to a host and wait for the first screen. |
| `disconnect` | Close a session. |
| `send_text` | Type text at the cursor (client-side; does not submit). |
| `set_field` | Set an input field's value by its index from `get_screen`. |
| `press_key` | Press a 5250 key — AID keys (`Enter`, `F1`-`F24`, `Clear`, `Attn`, `SysReq`, `Help`, `Print`, `PageUp`, `PageDown`) submit to the host and **block until it finishes and re-invites input** (the keyboard stays locked, "X SYSTEM", while it works), so you get the real response, not an intermediate paint; navigation/edit keys (`Tab`, `Home`, arrows, `FieldExit`, `EraseInput`, `Reset`, …) act locally. |
| `wait_for_screen` | Block until the host finishes working and the screen is ready again — or until a given text appears — then return it. The primitive for "start a long host job, then wait for it." |
| `signon` | Sign on to the current session using credentials the user saved on this machine (Windows Credential Manager) for the session's host. A host can have several logins under short labels; omit `credentialLabel` for the host's default (or its only login), or pass one to choose. You never provide the password — it is read from the OS vault, typed into the hidden field locally, and never returned. |

A typical loop: `get_screen` → `set_field`/`send_text` → `press_key Enter` → `get_screen`. For an
operation the host takes a while on, `press_key` already waits; `wait_for_screen` is there for the
rest (unsolicited updates, or waiting for a specific screen to land).

**Waiting / readiness.** Pressing an AID key locks the keyboard until the host replies, so the
tools treat an inhibited keyboard as "the system is still working" and keep waiting through it
(up to a 30 s ceiling, tunable via `timeoutMs`) rather than returning a half-painted screen. If a
wait hits the ceiling, the returned screen still carries `keyboardInhibited=true` so the client
knows to wait again.

### Mode 1 — in-process (drive the visible desktop app)

The server **starts automatically at launch** (Tools → *Start MCP on Startup*, on by
default) and binds to `127.0.0.1:8250`. Because it is loopback-only and lives only
while the app is open, it uses **no auth token**. Register it once:

```sh
claude mcp add --transport http ac5250 http://127.0.0.1:8250/mcp
```

Launch flags: `--mcp` (force-start even if the setting is off), `--mcp-port <n>`.

With this mode you watch Claude drive the real terminal window. To let Claude get past
the IBM i sign-on, save credentials under **Session → Manage Saved Credentials** (stored
in Windows Credential Manager) and Claude calls the `signon` tool. A host can hold several
logins under short **labels** (e.g. `ADMIN`, `TESTUSER`); mark one the default, and Claude
picks a specific one by passing `credentialLabel`.

### Mode 2 — headless (stdio, no window)

```sh
claude mcp add ac5250 -- dotnet /abs/path/src/AC5250.Headless/bin/Debug/net10.0/ac5250-mcp.dll
# or, against a self-contained/published build, point directly at ac5250-mcp.exe
```

Same tools, no GUI — best for unattended automation: the MCP client spawns the process on
demand, so there is no app to start first. `signon` works here too. Credentials come from a
platform-appropriate source (see below), so this runs on Windows, Linux, or macOS.

**Where `signon` reads credentials** (via a pluggable `ICredentialSource`):

- **Windows** — the Windows Credential Manager (DPAPI, per-user), with environment variables as
  a fallback/override. Manage vault entries from the desktop app's **Session → Manage Saved
  Credentials** dialog or Windows' *Credential Manager* control panel.
- **Linux / macOS** (and as an override anywhere) — environment variables the spawner injects.
  No Credential Manager exists off-Windows, and nothing is written to a file. Set a host-specific
  pair, or a host-agnostic default for the single-host case:

  ```sh
  # HOST / LABEL = upper-cased, non-alphanumerics -> '_'
  #   host "test400.gwb.local", default login  ->  AC5250_TEST400_GWB_LOCAL_USER / _PASSWORD
  #   host "10.1.1.3", label "ADMIN"           ->  AC5250_10_1_1_3_ADMIN_USER    / _PASSWORD
  claude mcp add ac5250 \
    -e AC5250_TEST400_GWB_LOCAL_USER=myuser \
    -e AC5250_TEST400_GWB_LOCAL_PASSWORD=... \
    -- dotnet /abs/path/ac5250-mcp.dll
  # or the host-agnostic default:  AC5250_USER / AC5250_PASSWORD
  ```

  Lookup order: `signon`'s `credentialLabel` (`AC5250_<HOST>_<LABEL>_*`) → the host default
  (`AC5250_<HOST>_*`) → the global default (`AC5250_*`). Both the user and password must be
  present or the lookup yields nothing (it never returns half a credential). The password is read
  on demand to fill the hidden field — never stored in the process, never returned to the client.

---

## Security model

This tooling can drive a **live IBM i session** (sign-on, commands, screen data),
so it is treated as sensitive:

- **Localhost only, no token.** The HTTP host binds `127.0.0.1` and rejects any
  non-loopback remote (defense in depth). It carries no auth token: it is reachable
  only from this machine and only while the app is running. Trade-off: any process
  running as the current user can reach it — the same trust boundary as your desktop
  session. Do not enable it on a shared/multi-user machine.
- **On by default.** Starts at launch so an MCP client can connect with no manual
  step (toggle under Tools → *Start MCP on Startup*).
- **Hidden fields are masked.** Non-display (password) fields are never surfaced in
  `get_screen` text or field content — only their presence is reported.
- **Credentials never in a file.** Sign-on credentials come from a pluggable
  `ICredentialSource`: the Windows Credential Manager (DPAPI-encrypted, per-user) on
  Windows, or spawner-injected environment variables on Linux/macOS (see the headless
  section) — never the connection JSON, a config file, or logs. The `signon` tool reads
  the password only to fill the hidden field; it is never a tool parameter and never
  returned to the client.
- **Production caution.** Do **not** point `connect` at a production IBM i (or any
  PCI-scoped environment) without explicit authorization. Screen contents pass into
  the model's context.

## Known limitations (inherited from the base emulator)

The TN5250 engine is a solid first cut, not a hardened emulator. Start-of-Field
parsing ignores optional FCWs, Write-Extended-Attribute is skipped, the display-attribute
decode is approximate, and only the 5250 Query structured field is answered. Complex
host screens may not render perfectly; verify against a real host before relying on it.
