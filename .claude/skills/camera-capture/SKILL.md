---
name: camera-capture
description: Use when capturing photos or video from the host's cameras through the camera-mcp server — stills, frame sequences, video, the async capture queue, device-triggered "remote shutter" sessions (with rapid-fire bursts and per-trigger name/description metadata), the live preview, and on-demand public tunnels.
---

# Using camera-mcp

`camera-mcp` exposes the host machine's cameras to you over MCP. Pick the tool by **who decides when to
shoot** and **whether you need to block**.

## 1. Find a camera

Call **`list_cameras`** first. It returns each device's `id`, friendly `name`, and supported formats.
Pass the `id` (or name) as `deviceId` to any capture tool; omit it to use the first camera.

## 2. Capture now (blocking)

- **`capture_image`** — one still, returned inline so you can see it.
  `{ "deviceId": "0", "format": "jpeg", "quality": 85 }`
- **`capture_scene`** — a sequence of stills to watch motion/animation over time. Uniform
  (`frameCount` + `intervalSeconds`) or non-uniform (`intervals: [0.2, 0.5, 1.0]`). Every frame is saved;
  a bounded prefix comes back inline.
  `{ "frameCount": 6, "intervalSeconds": 0.5 }`
- **`capture_video`** — a clip on disk + an inline poster frame. `{ "durationSeconds": 5, "codec": "h264" }`

All three accept `startDelaySeconds` to arm the capture, then trigger the scene yourself.

## 3. Capture without blocking (the queue)

When you want to queue several captures, schedule a delayed one, or keep working while a long recording
runs, use the queue. Different cameras run in parallel; the same camera serializes.

1. **`queue_image`** / **`queue_scene`** / **`queue_video`** — same params as `capture_*`; returns
   `{ jobId, etaSeconds, queuePosition }` immediately.
2. **`get_capture`** `{ "jobId": "cap_0001", "waitSeconds": 30 }` — long-polls and returns the result the
   instant it's ready (inline images + resource links), or the live status if it isn't done yet.
3. **`list_captures`** / **`cancel_capture`** to manage jobs.

## 4. Let a device decide WHEN to shoot (remote shutter)

Use this when an **embedded device, remote machine, or a person** should fire the capture — e.g. a board
finishes a step, a sensor trips, someone presses a button. The device triggers over HTTP; you receive
each capture in order.

```text
1. start_capture_session  →  { sessionId, token, triggerUrl, tunnelTriggerUrl }
2. Hand triggerUrl + token to the device (or tunnelTriggerUrl if it's remote — see §6).
3. Loop await_capture(sessionId, waitSeconds) to receive each triggered capture.
4. stop_capture_session(sessionId) when done.
```

**Start** (optionally set a default burst + expose publicly):

```json
start_capture_session { "deviceId": "0", "burstCount": 1, "tunnel": "cloudflare" }
```

**The device triggers** by POSTing the trigger URL. The token goes in `?token=` or the
`X-Session-Token` header. Optional per-trigger overrides — on the query string or a JSON body:

| Field | Meaning |
|---|---|
| `name` | short label for this capture, shown to you |
| `description` | longer context, shown to you |
| `count` | **rapid-fire burst** size (frames captured this trigger) |
| `interval` | seconds between burst frames |

`triggerUrl` is `<baseUrl>/sessions/{sessionId}/trigger?token=…` — hand the device the whole URL.

```bash
# single still, labelled
curl -X POST "$TRIGGER_URL&name=front-door&description=motion%20detected"

# a 5-frame rapid-fire burst, 0.2s apart
curl -X POST "$TRIGGER_URL&name=swing&description=golf%20follow-through&count=5&interval=0.2"

# health-check / discover the session (token required)
curl "$BASE_URL/sessions/$SESSION_ID?token=$TOKEN"
```

**Receive** with `await_capture` in a loop:

```json
await_capture { "sessionId": "sess_ab12cd34", "waitSeconds": 60 }
```

It returns a status block (`seq`, `kind` = `still`|`burst`, `frameCount`, the device's `name` and
`description`) followed by the image(s). A burst comes back as one capture with all its frames inline.
A `waiting` status means nothing triggered within `waitSeconds` — just call it again.

> Many sessions can run at once; each session's token only works for its own routes. Triggers share the
> per-device lock with every other capture, so a session and a preview never open the same camera at once.

**Reaching the device endpoint.** By default the host binds loopback, so a remote/LAN device needs a
tunnel (`tunnel: "cloudflare"` → a public HTTPS `tunnelTriggerUrl`). For a device on the **same Wi-Fi**,
start the server with `CameraMcp__HttpBindAddress=0.0.0.0` and the trigger URL uses the host's LAN IP
directly — no tunnel/internet needed.

## 5. Let a human WATCH (live preview)

You can't consume a live video stream (MCP has no video type) — for *you*, use `capture_image` /
`capture_scene`. For a **person** to watch, **`start_preview`** serves a token-gated MJPEG page and
returns `{ previewId, localUrl }`; **`stop_preview { previewId }`** ends it. Add `tunnel` to expose it
publicly. Multiple previews can run at once.

## 6. Expose an endpoint publicly (on-demand tunnels)

If you started a session or preview with `tunnel: "none"` and later need a public URL, do it yourself:

```json
start_tunnel { "provider": "cloudflare" }   // → { tunnelId, publicUrl }
```

**Omit `port`** to expose this server's own web host (which serves all sessions + previews); the device
then uses `<publicUrl>/sessions/{sessionId}/trigger?token=<token>`. One tunnel exposes every endpoint on
the host, but each stays token-gated. **`stop_tunnel`** / **`list_tunnels`** manage them. Needs
`cloudflared` (or `devtunnel`) on `PATH`; if it's missing you get a note and a null URL.

To let a **remote agent** drive this camera (not just a device), run the server with
`CameraMcp__EnableHttpMcp=true` + `CameraMcp__HttpMcpBearerToken=<secret>` and connect to
`https://<host>/mcp` with `Authorization: Bearer <secret>`.

## 7. Re-read a saved capture

Every capture is saved and addressable as an MCP **resource**:
- `camera://captures/<path>` — a saved still, scene frame, or video.
- `camera://device/<deviceId>/frame` — a **fresh** grab from that camera.

Capture results also include a `resource_link` so a remote client can fetch the bytes without a file path.

## Tips

- Formats: images `jpeg|png|webp`; video `mp4`(h264/h265) / `webm`(vp9) / `mkv`(any). `quality` 1–100.
- A burst is bounded by the server's `MaxSceneFrames`; an over-large `count` fails the trigger with a
  clear error rather than running unbounded.
- Logs go to stderr; tool results carry the data. Black first frame is avoided via a warm-up.
