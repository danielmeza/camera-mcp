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

## Install

See **[INSTALL.md](INSTALL.md)** for the full guide: building a **self-contained** package (the .NET
runtime *and* ffmpeg bundled, nothing required on the target machine) and registering the server with
Claude Code, Claude Desktop, and VS Code.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
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

## Scope (v1)

stdio transport only; capture is request/response (no live streaming); audio capture is not enabled.
