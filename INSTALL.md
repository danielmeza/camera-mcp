# Installing camera-mcp

Three ways to run the server:

- **[A. Self-contained build](#a-self-contained-build-recommended-for-shipping)** — one standalone folder
  with the .NET runtime *and* ffmpeg bundled in. The target machine needs **nothing** installed. Best for
  shipping to others. (Also published as per-OS archives on the [GitHub Releases](https://github.com/danielmeza/camera-mcp/releases) page.)
- **C. From NuGet** — `dnx DanielMeza.CameraMcp` (needs the .NET 10 SDK and **ffmpeg on `PATH`**). The
  lightweight option; an MCP client entry looks like:
  ```jsonc
  { "command": "dnx", "args": ["DanielMeza.CameraMcp", "--yes"] }
  ```
- **[B. From source](#b-from-source-for-development)** — `dotnet run`; needs the .NET 10 SDK and ffmpeg
  on `PATH`. Best while developing.

Then **[register it with your MCP client](#register-with-an-mcp-client)**.

---

## A. Self-contained build (recommended for shipping)

Publish a standalone build for the target operating system. `-p:BundleFFmpeg=true` downloads the correct
ffmpeg for that RID (once, then cached under `ffmpeg-cache/`) and drops it next to the app in `ffmpeg-bin/`.

```bash
# Windows x64
dotnet publish src/CameraMcp.Server -c Release -r win-x64   --self-contained -p:BundleFFmpeg=true

# Linux x64
dotnet publish src/CameraMcp.Server -c Release -r linux-x64 --self-contained -p:BundleFFmpeg=true

# macOS Apple Silicon  (no default ffmpeg source — supply one)
dotnet publish src/CameraMcp.Server -c Release -r osx-arm64 --self-contained -p:BundleFFmpeg=true \
  -p:FFmpegDownloadUrl=https://.../ffmpeg-osx-arm64.zip -p:FFmpegArchive=zip
```

Output lands in `src/CameraMcp.Server/bin/Release/net10.0/<rid>/publish/`:

```
publish/
  camera-mcp(.exe)        # the server — run this directly, no dotnet needed
  ffmpeg-bin/
    ffmpeg(.exe)          # bundled encoder
  *.dll, *.json           # bundled .NET runtime + deps
```

Ship the whole `publish/` folder. To launch: run `camera-mcp` (it speaks MCP over stdio).

### One-file variant

Add `-p:PublishSingleFile=true` to collapse the app into a single executable. `ffmpeg-bin/` still sits
next to it — keep them together.

```bash
dotnet publish src/CameraMcp.Server -c Release -r win-x64 --self-contained \
  -p:BundleFFmpeg=true -p:PublishSingleFile=true
```

### Supplying ffmpeg another way

`BundleFFmpeg` is optional. Alternatives, in the order the server searches at runtime:

1. `CameraMcp__FFmpegPath` env var → explicit path to an ffmpeg binary.
2. Bundled (`ffmpeg-bin/`) — what `-p:BundleFFmpeg=true` produces.
3. `ffmpeg` on the system `PATH`.

So you can skip bundling and instead require ffmpeg on `PATH`, or point `CameraMcp__FFmpegPath` at one.

### Optional: public tunnels (for live preview / device sessions)

`start_preview` and `start_capture_session` can expose their loopback endpoint **publicly** through a
tunnel, so a browser or a remote device can reach it. This is opt-in (`tunnel=cloudflare|devtunnel`) and
needs the matching tool on `PATH`; without it the server falls back to the local URL and says so:

- **Cloudflare** — install [`cloudflared`](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/) (no account needed for a quick tunnel).
- **Microsoft Dev Tunnels** — install [`devtunnel`](https://learn.microsoft.com/azure/developer/dev-tunnels/get-started).

ffmpeg alone (above) is enough for every capture tool; tunnels are only for the optional public URLs.

---

## B. From source (for development)

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and `ffmpeg` on `PATH`.

```bash
dotnet run --project src/CameraMcp.Server
```

---

## Register with an MCP client

The server is a standard **stdio** MCP server: a client launches the executable and talks JSON-RPC over
stdin/stdout. Point your client at either the published `camera-mcp` executable (A) or `dotnet run` (B).

### Claude Code (CLI)

```bash
# Self-contained build:
claude mcp add camera -- "C:\path\to\publish\camera-mcp.exe"

# From source:
claude mcp add camera -- dotnet run --project "D:\src\nnf\camera-mcp\src\CameraMcp.Server"
```

Add `-s user` for all projects, or `-s project` to share via a checked-in `.mcp.json`. Verify with
`claude mcp list`, then ask the agent to use `list_cameras`.

### Claude Desktop

Edit `claude_desktop_config.json` (Settings → Developer → Edit Config):

```jsonc
{
  "mcpServers": {
    "camera": {
      "command": "C:\\path\\to\\publish\\camera-mcp.exe",
      "args": [],
      "env": { "CameraMcp__OutputDirectory": "C:\\Users\\me\\camera-captures" }
    }
  }
}
```

Restart Claude Desktop; the camera tools appear in the tools list.

### VS Code (GitHub Copilot agent mode)

`.vscode/mcp.json` (or the global MCP config):

```jsonc
{
  "servers": {
    "camera": { "type": "stdio", "command": "C:\\path\\to\\publish\\camera-mcp.exe" }
  }
}
```

### Any other MCP client

Use the generic stdio form: **command** = the `camera-mcp` executable (or `dotnet` with
`args: ["run","--project","<path>"]`), transport = **stdio**.

---

## Configuration (optional)

Set via environment variables in your client's `env` block (prefix `CameraMcp__`):

| Variable | Default | Purpose |
|---|---|---|
| `CameraMcp__OutputDirectory` | `<LocalAppData>/camera-mcp/captures` | where images/videos are written |
| `CameraMcp__MaxVideoDurationSeconds` | `300` | cap on `capture_video` duration / scene span |
| `CameraMcp__MaxSceneFrames` | `60` | cap on frames per `capture_scene` |
| `CameraMcp__MaxInlineSceneFrames` | `30` | scene frames returned inline (all saved to disk regardless) |
| `CameraMcp__ImageWarmupFrames` | `15` | frames discarded before a still (avoids cold black frames) |
| `CameraMcp__FFmpegPath` | — | explicit ffmpeg path (overrides discovery) |
| `CameraMcp__FFmpegTimeoutSeconds` | `120` | encode headroom beyond the recording duration |

## Verify the install

Without a client, confirm the server starts and lists its tools by piping a request to its stdin:

```bash
printf '%s\n' \
 '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"c","version":"1"}}}' \
 '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
 '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}' | ./camera-mcp
```

The response advertises the full tool set — `list_cameras`, `capture_image`, `capture_video`,
`capture_scene`, `clear_captures`, the live-preview tools (`start_preview` / `stop_preview`), the async
queue (`queue_image` / `queue_scene` / `queue_video`, `get_capture`, `list_captures`, `cancel_capture`),
the device-triggered session tools (`start_capture_session` / `await_capture` / `stop_capture_session`),
and the on-demand tunnel tools (`start_tunnel` / `stop_tunnel` / `list_tunnels`) — plus the `camera://…`
resources. Logs go to stderr; stdout carries only the protocol.
