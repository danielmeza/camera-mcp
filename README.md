# camera-mcp

A cross-platform [Model Context Protocol](https://modelcontextprotocol.io) server, written in C# (.NET 10),
that exposes the **cameras attached to the host machine** to LLM agents. Agents can list devices, capture
still images, record short video clips, and capture timed **frame sequences** — choosing the format,
compression, duration/timing, and a start delay.

It runs on **Windows, Linux, and macOS** and speaks MCP over **stdio**, so it plugs into any MCP client
(Claude Desktop, VS Code, etc.).

## Use cases

### Giving an agent eyes on a device that can't screenshot itself

Many devices render a UI to a **physical screen but have no framebuffer/screenshot API** — embedded boards
and microcontrollers (e.g. an ESP32 driving an LCD/OLED via LVGL), instruments and appliances with built-in
displays, kiosks, or any closed system you can't capture from the inside. Point a webcam or a phone-as-webcam
at the screen and this server becomes the agent's **eyes**:

- **`capture_image`** — a snapshot of the current screen, returned inline so a vision model can read layout,
  on-screen values, or error states.
- **`capture_scene`** — a timed sequence of frames (uniform or non-uniform intervals) so the agent can watch
  **motion, animations, transitions, or flicker** unfold over time — the kind of issue a single frame can't
  reveal (e.g. "fps drops while scrolling", "the animation freezes", screen tearing). A `startDelaySeconds`
  lets you arm the capture, then trigger the on-device action.
- **`capture_video`** — a short clip on disk for the human, plus an inline poster frame for the agent.

Because the input is just a camera, it works for **anything with a display** regardless of platform, OS, or
whether the device exposes any capture API at all. Other uses: webcam/QA automation, visual regression of a
physical product, monitoring a 3D print or a lab readout, and general "let the agent see the real world."

When the *device itself* should decide the moment to shoot — e.g. it finishes a step, hits a state, or a
person presses a button — a **device-triggered session** (`start_capture_session`) hands the device a
token-protected trigger URL; each trigger captures a still that the agent receives over MCP. And when you
want to **queue** several captures or schedule a delayed one without blocking, the `queue_*` tools return a
job id + ETA and stream the result back when it's ready.

## How it works

| Concern | Library |
|---|---|
| Device enumeration + single-frame grab | [FlashCap](https://github.com/kekyo/FlashCap) (pure managed: DirectShow / V4L2 / AVFoundation) |
| Encoding, compression, and video recording | [FFmpeg](https://ffmpeg.org) (bundled per-RID, or taken from `PATH`) |
| MCP protocol | [ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol) C# SDK |

FlashCap is used for what it does best — clean cross-platform device discovery and a fast managed frame
grab — and FFmpeg handles real container/codec/compression/duration control.

## Tools

### `list_cameras`
Lists the cameras attached to the host and the capture formats each supports. Returns JSON with a stable
`id` and friendly `name` per device; use either with the capture tools.

### `capture_image`
Captures one still and returns it **inline** (the agent can see it).

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `deviceId` | string? | first camera | `id` or name from `list_cameras` |
| `width`, `height` | int? | device default | snapped to the nearest supported mode; pass together |
| `format` | string | `jpeg` | `jpeg`, `png`, or `webp` |
| `quality` | int | `85` | 1 (smallest) – 100 (best); ignored for lossless `png` |

### `capture_video`
Records a fixed-duration clip to a file on the host and returns the path, metadata, and a single **poster
frame** inline.

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `durationSeconds` | double | **required** | capped by `MaxVideoDurationSeconds` |
| `deviceId` | string? | first camera | `id` or name from `list_cameras` |
| `width`, `height` | int? | device default | snapped to the nearest supported mode; pass together |
| `fps` | int | `30` | 1 – 240 |
| `container` | string | `mp4` | `mp4`, `webm`, or `mkv` |
| `codec` | string | `h264` | `h264`, `h265`, or `vp9` |
| `quality` | int | `75` | 1 – 100; mapped to a codec CRF when no bitrate is given |
| `bitrateKbps` | int? | — | constant target bitrate; overrides `quality` |
| `outputPath` | string? | capture dir | explicit output file path |

**Container/codec compatibility:** `mp4` → `h264`/`h265`; `webm` → `vp9`; `mkv` → any.

### `capture_scene`
Captures a **sequence of stills** and returns them as inline image frames, in order — a lightweight,
model-readable alternative to video (vision models can read each frame, but not video). Every frame is
also saved to disk. Use it to observe motion, animation, or UI changes over time.

**Timing — uniform or non-uniform:**
- **Uniform:** `frameCount` + `intervalSeconds`. `intervalSeconds` may be omitted to use the server
  default (`DefaultSceneIntervalSeconds`). Frames are evenly spaced.
- **Non-uniform:** `intervals` — an array of per-gap seconds. `[0.2, 0.5, 1.0]` captures 4 frames at
  t = 0, 0.2, 0.7, 1.7 (produces `intervals.length + 1` frames). Overrides `frameCount`/`intervalSeconds`.

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `frameCount` | int? | — | uniform frame count (≥ 2); required unless `intervals` is given |
| `intervalSeconds` | double? | server default | uniform gap between frames |
| `intervals` | double[]? | — | per-gap seconds for non-uniform timing |
| `deviceId` | string? | first camera | `id` or name from `list_cameras` |
| `width`, `height` | int? | device default | snapped to the nearest supported mode; pass together |
| `format` | string | `jpeg` | `jpeg`, `png`, or `webp` |
| `quality` | int | `85` | 1 – 100; ignored for lossless `png` |
| `outputDirectory` | string? | per-scene folder | where the frame files are written |
| `startDelaySeconds` | double | `0` | wait this long before the scene begins (ASAP by default) |

The total span is capped by `MaxVideoDurationSeconds`. **Every** frame is saved to disk and listed by
path; a bounded prefix is also returned **inline** (see `MaxInlineSceneFrames` / `MaxInlineSceneBytes`).

`capture_image` and `capture_video` also accept **`startDelaySeconds`** (default `0` = ASAP).

### `clear_captures`
Deletes captured files to reclaim space and returns how much was freed. **Destructive.**

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `directory` | string? | whole output dir | optional sub-directory to clear (e.g. one scene folder) |

A path guard ensures it can only delete **within** the configured output directory — never an arbitrary
location on the host. Returns `filesDeleted`, `directoriesDeleted`, and `bytesFreed`.

### `start_preview` / `stop_preview` (live view for a human)
Starts a **live MJPEG preview** of a camera that a **human** can watch in a browser, and returns
`{ previewId, localUrl, streamUrl }`. The stream is served by the built-in web host (loopback by default;
see [Networking](#networking--remote-access)) and **token-gated**; set `tunnel=cloudflare|devtunnel|auto`
to expose a public URL, with graceful fallback to the local URL if no tunnel tool is installed. One viewer
at a time per preview; the camera is held only while someone is watching. **`stop_preview`** takes the
`previewId`. Multiple previews can run at once.

> An **LLM agent cannot consume a live video stream** — MCP has no video type. For the *model*, use
> `capture_image` / `capture_scene` (discrete frames). `start_preview` is for a person to watch.

`start_preview` params: `deviceId?`, `width?`/`height?`, `fps` (15), `quality` (70), `tunnel` (`none`).

### Queued / async captures
Submit a capture and get a **job id + ETA** back immediately, instead of blocking the agent until the
capture finishes. Useful to queue several captures, schedule a delayed one, or keep working while a long
recording runs. Jobs for **different cameras run in parallel**; jobs for the **same camera serialize**.

- **`queue_image`** / **`queue_scene`** / **`queue_video`** — same parameters as the matching `capture_*`
  tool; return `{ jobId, etaSeconds, queuePosition, status }` right away.
- **`get_capture`** — retrieve a job by `jobId`. With `waitSeconds > 0` it **long-polls**, returning the
  result the instant the job completes (or the live status if it isn't done within the wait). When
  complete it returns the **same content** — inline images + resource links — as the blocking tools.
- **`list_captures`** — all jobs (queued, running, finished) with status, ETA, and queue position.
- **`cancel_capture`** — cancel a queued or running job by `jobId`.

### Device-triggered sessions (remote shutter)
Let **something other than the agent** decide *when* to shoot. The agent starts a session; a remote or
embedded device (or a person) triggers each capture over HTTP; the agent receives the captures in order.

- **`start_capture_session`** — registers a **token-gated** endpoint on the built-in web host and returns
  `{ sessionId, token, triggerUrl, tunnelTriggerUrl }`. `triggerUrl` is
  `<baseUrl>/sessions/{sessionId}/trigger?token=…`. Params: `deviceId?`, `width?`/`height?`, `format`
  (`jpeg`), `quality` (85), `burstCount` (`1`), `burstIntervalSeconds` (`0.3`), `tunnel` (`none`).
- The device **`POST`s the trigger URL** (token in `?token=` or the `X-Session-Token` header) to capture.
  It can also **`GET /sessions/{sessionId}`** (with the token) to health-check / discover the session.
- **Rapid-fire bursts** — a trigger captures `burstCount` frames at `burstIntervalSeconds` apart; either
  the session default or a **per-trigger override**. The whole burst is delivered to the agent as one
  capture (multiple inline frames).
- **Per-trigger metadata** — the device can attach a **`name`** and **`description`** to each trigger;
  they're surfaced to the agent alongside the frames so it knows *what* it's looking at and *why*.
  Overrides go on the **query string** or in a **JSON body**:
  `POST <triggerUrl>&name=door&description=motion&count=5&interval=0.2`
- **`await_capture`** — long-polls for the next trigger and returns the still **or** the whole burst
  (inline images + resource links) plus its `name`/`description`/`seq`; call it in a loop to follow the
  stream. Returns a `waiting` status if nothing triggers within `waitSeconds`.
- **`stop_capture_session`** — tears the session (and any tunnel) down by `sessionId`.

Many sessions can run at once; each session's token only works for its own routes. Triggers capture through
the same per-device lock as every other capture, so a session and a preview never open the camera at once.

### On-demand public tunnels
The agent can expose the built-in web host (which serves the preview + session endpoints) **publicly** on
its own, without restarting anything with `tunnel=…`:

- **`start_tunnel`** — `{ port?, provider? }` → starts a Cloudflare quick tunnel (default) or a Microsoft
  Dev Tunnel and returns `{ tunnelId, publicUrl }`. **Omit `port`** to expose this server's own web host;
  pass a port only to expose a different local server. Returns a note + null URL if the tool isn't installed.
- **`stop_tunnel`** — `{ tunnelId }`. **`list_tunnels`** — the active tunnels and their public URLs.

> Because every session and preview shares one web-host port, a single tunnel exposes them all — but each
> remains independently token-gated, so a public URL is useless without the right per-endpoint token.

Requires `cloudflared` / `devtunnel` on `PATH` (see [INSTALL.md](INSTALL.md)).

## Networking & remote access

The server runs a built-in **Kestrel** web host (alongside the stdio MCP transport) that serves the device
trigger endpoints, the live preview, and — optionally — the MCP protocol itself. By default it binds
**loopback only** (`127.0.0.1`, OS-assigned port), so off-box access goes through a tunnel.

- **Direct LAN access** — set `CameraMcp__HttpBindAddress=0.0.0.0` (and optionally `CameraMcp__HttpPort`)
  so a device on the same network reaches the host's LAN IP directly, no tunnel/internet required. URLs are
  built from the resolved LAN IP (override with `CameraMcp__PublicBaseUrl` behind a reverse proxy).
- **Remote MCP server** — set `CameraMcp__EnableHttpMcp=true` to also expose the MCP server over
  **Streamable HTTP** at `CameraMcp__HttpMcpPath` (default `/mcp`), so a *remote* agent can drive a
  networked camera (e.g. camera-mcp running on a Pi). Gate it with `CameraMcp__HttpMcpBearerToken`
  (clients send `Authorization: Bearer <token>`). For a pure-HTTP deployment, set
  `CameraMcp__StdioTransport=false` so the process doesn't exit on stdin EOF.

> Exposing camera control to a network is powerful — always bind with auth (a per-session token for device
> endpoints, a bearer token for `/mcp`) and prefer a tunnel (HTTPS) or a TLS-terminating proxy over plain
> HTTP on untrusted networks.

The device trigger/preview endpoints are reached by **devices, servers, and `curl`**, which ignore CORS —
so they need no CORS config. CORS only matters if a **browser web app on another origin** calls them via
`fetch()`; for that, set `CameraMcp__AllowedWebOrigins` to an explicit comma-separated allowlist (never a
wildcard). The per-session token is still required either way.

## Transmitting images over MCP (no file path needed)

Captures are delivered three ways, all over the MCP protocol — useful when the agent is **remote** and the
local file path is meaningless:

- **Inline** — `capture_image`/`capture_scene` embed the bytes as an `image` content block (the model sees
  them directly). The universal, most-portable delivery.
- **Resource link** — capture results also include a `resource_link` to a `camera://captures/<path>` URI
  (and the metadata carries `resourceUri`).
- **Resources** — read any saved capture by URI via `resources/read`:
  - `camera://captures/{path}` — a previously saved image, scene frame, or video (path-guarded to the
    output dir).
  - `camera://device/{deviceId}/frame` — captures and returns a **fresh** JPEG from the named camera.

## Install

See **[INSTALL.md](INSTALL.md)** for the full guide: building a **self-contained** package (the .NET
runtime *and* ffmpeg bundled, nothing required on the target machine) and registering the server with
Claude Code, Claude Desktop, and VS Code.

## Agent skill (samples)

A ready-to-use Claude skill with worked examples — stills, scenes, the async queue, **device-triggered
sessions with rapid-fire bursts + metadata**, live preview, and on-demand tunnels — lives at
[`.claude/skills/camera-capture/SKILL.md`](.claude/skills/camera-capture/SKILL.md). Copy that folder into
your own `.claude/skills/` (or a plugin) to give the agent guidance and sample calls for every tool.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (the SDK includes the ASP.NET Core
  runtime the server now hosts; the self-contained build bundles it, but the `dnx`/NuGet path needs the
  ASP.NET Core 10 runtime present).
- FFmpeg — either bundled at publish time (see [INSTALL.md](INSTALL.md)) or available on `PATH`.

## Build, test, run

```bash
dotnet build
dotnet test
dotnet run --project src/CameraMcp.Server
```

## Use from an MCP client

Register the server with a stdio command. Example (Claude Desktop `claude_desktop_config.json` /
VS Code `mcp.json`):

```jsonc
{
  "mcpServers": {
    "camera": {
      "command": "dotnet",
      "args": ["run", "--project", "/absolute/path/to/camera-mcp/src/CameraMcp.Server"]
    }
  }
}
```

After publishing (below), point `command` at the produced `camera-mcp` executable instead.

## Configuration

Settings bind from environment variables with the `CameraMcp__` prefix:

| Variable | Default | Purpose |
|---|---|---|
| `CameraMcp__OutputDirectory` | `<LocalAppData>/camera-mcp/captures` | where images/videos are written |
| `CameraMcp__MaxVideoDurationSeconds` | `300` | safety cap on recording length (also caps a scene's span) |
| `CameraMcp__MaxSceneFrames` | `60` | cap on frames per `capture_scene` |
| `CameraMcp__DefaultSceneIntervalSeconds` | `1.0` | default gap between scene frames when not specified |
| `CameraMcp__MaxStartDelaySeconds` | `3600` | cap on a capture's `startDelaySeconds` |
| `CameraMcp__MaxInlineSceneFrames` | `30` | how many scene frames are returned inline (all are saved to disk) |
| `CameraMcp__MaxInlineSceneBytes` | `25165824` | byte budget for inline scene frames |
| `CameraMcp__ImageWarmupFrames` | `15` | frames discarded before a still (avoids cold black frames) |
| `CameraMcp__FFmpegPath` | — | explicit ffmpeg path (overrides discovery) |
| `CameraMcp__FFmpegTimeoutSeconds` | `120` | encode headroom beyond the recording duration |
| `CameraMcp__HttpBindAddress` | `127.0.0.1` | web-host bind: loopback, `0.0.0.0`/`lan`, or a specific IP |
| `CameraMcp__HttpPort` | `0` | web-host port (`0` = OS-assigned) |
| `CameraMcp__PublicBaseUrl` | — | override for device-facing URLs (reverse proxy / custom domain) |
| `CameraMcp__StdioTransport` | `true` | serve the stdio MCP transport (set `false` for a pure-HTTP host) |
| `CameraMcp__EnableHttpMcp` | `false` | also expose the MCP server over Streamable HTTP for remote agents |
| `CameraMcp__HttpMcpPath` | `/mcp` | route for the HTTP MCP transport |
| `CameraMcp__HttpMcpBearerToken` | — | bearer token required on the HTTP MCP endpoint (recommended off-loopback) |
| `CameraMcp__AllowedWebOrigins` | — | comma-separated origins allowed to call device endpoints from a **browser** (never a wildcard) |

## Bundling FFmpeg (zero-setup distribution)

Publish self-contained with a bundled, per-RID ffmpeg so end users need nothing installed:

```bash
dotnet publish src/CameraMcp.Server -c Release -r win-x64   --self-contained -p:BundleFFmpeg=true
dotnet publish src/CameraMcp.Server -c Release -r linux-x64 --self-contained -p:BundleFFmpeg=true
dotnet publish src/CameraMcp.Server -c Release -r osx-arm64 --self-contained -p:BundleFFmpeg=true -p:FFmpegDownloadUrl=<url>
```

The `BundleFFmpeg` target downloads the right static build (sources in [`build/ffmpeg-sources.props`](build/ffmpeg-sources.props)),
caches it under `ffmpeg-cache/<rid>/`, and copies the binary into `<publish>/ffmpeg-bin/`. At runtime
`FFmpegLocator` resolves ffmpeg in this order: `CameraMcp__FFmpegPath` → bundled (`ffmpeg-bin/`,
`runtimes/<rid>/native/`) → `PATH`.

Override the source for a RID without a default with `-p:FFmpegDownloadUrl=<url> -p:FFmpegArchive=<zip|tar>`.

## Troubleshooting

- **`list_cameras` returns nothing** but a camera is plugged in: confirm the OS sees it
  (`ffmpeg -list_devices true -f dshow -i dummy` on Windows; `v4l2-ctl --list-devices` on Linux). Note
  that FlashCap may not enumerate some **virtual** cameras (e.g. OBS Virtual Camera); physical USB and
  built-in webcams are enumerated normally.
- **"Could not locate an ffmpeg executable"**: install ffmpeg and add it to `PATH`, set
  `CameraMcp__FFmpegPath`, or publish with `-p:BundleFFmpeg=true`.
- **Logs:** all server logs go to **stderr**; stdout carries only the MCP protocol.

## Scope

Local agents connect over **stdio**; remote agents can optionally connect over **Streamable HTTP**
(`EnableHttpMcp`). Captures are delivered to the **model** as discrete frames — inline images, resource
links, or MCP resources — since MCP has no live-video type; `start_preview` offers a live MJPEG view for a
**human** only. Async captures (`queue_*`), device-triggered sessions, on-demand tunnels, and LAN/remote
binding are supported. Audio capture is not enabled.
