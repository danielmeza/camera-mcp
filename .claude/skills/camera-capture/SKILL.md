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

```bash
# single still, labelled
curl -X POST "$TRIGGER_URL&name=front-door&description=motion%20detected"

# a 5-frame rapid-fire burst, 0.2s apart
curl -X POST "$TRIGGER_URL&name=swing&description=golf%20follow-through&count=5&interval=0.2"

# discover the current session id (device knows the token, not the id)
curl "http://127.0.0.1:PORT/session?token=$TOKEN"
```

**Receive** with `await_capture` in a loop:

```json
await_capture { "sessionId": "sess_ab12cd34", "waitSeconds": 60 }
```

It returns a status block (`seq`, `kind` = `still`|`burst`, `frameCount`, the device's `name` and
`description`) followed by the image(s). A burst comes back as one capture with all its frames inline.
A `waiting` status means nothing triggered within `waitSeconds` — just call it again.

> Only one session is active at a time; starting another replaces it. Triggers share the per-device lock
> with every other capture, so a session and a preview never open the same camera at once.

## 5. Let a human WATCH (live preview)

You can't consume a live video stream (MCP has no video type) — for *you*, use `capture_image` /
`capture_scene`. For a **person** to watch, **`start_preview`** serves a token-gated MJPEG page and
returns its URL; **`stop_preview`** ends it. Add `tunnel` to expose it publicly.

## 6. Expose an endpoint publicly (on-demand tunnels)

If you started a session or preview with `tunnel: "none"` and later need a public URL, do it yourself:

```json
start_tunnel { "port": 53017, "provider": "cloudflare" }   // → { tunnelId, publicUrl }
```

Take `port` from the local URL of the session/preview you want to share. Then hand the device
`<publicUrl>/trigger?token=<token>`. **`stop_tunnel`** / **`list_tunnels`** manage them. Needs
`cloudflared` (or `devtunnel`) on `PATH`; if it's missing you get a note and a null URL.

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
