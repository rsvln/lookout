# Lookout

A companion service for [Frigate NVR](https://frigate.video). Frigate's own clips can be cut short or out of sync when a stream lags; Lookout waits until the recording of an event or review is really complete ("true end"), builds the clip from the recording segments itself and sends snapshots, the clip, an optional AI description and recognized faces — to Telegram and, if you add them, ntfy / Discord / Matrix / webhook. A web UI on port 8888 sits on Frigate's database: live log, event galleries with video, statistics, search and a config tree.

Previously named **frte2tg**.

## Features

**Telegram delivery**
- Handles both Frigate **events** and **reviews** (configurable per camera): objects with confidence thresholds, zones, severity, triggers
- Waits until all recording segments are on disk, then sends **complete video clips** built with ffmpeg; large clips are split automatically
- **Snapshots** as media groups; for objects still in view (e.g. a parked car in a review that has already ended) the current best frame comes from the Frigate API
- **Animated GIF previews** (optional, per camera)
- Telegram rate limit handling with automatic retry; optional disk retry queue when Telegram / AI / CompreFace is down; local Bot API server supported for large files
- Per-camera **cooldown** and **quiet hours**; optional buttons under a notification (clip, open in Lookout, mute)

**Recognition and AI**
- **AI snapshot descriptions** via Ollama (default), OpenAI-compatible APIs or Gemini, in the language you choose (optional, per camera)
- **Face recognition** via CompreFace (optional, per camera); recognized names are passed to the AI as context

**Telegram commands**
- `/status` — current frame of every camera
- `/last` — latest events of every camera or one camera, with buttons to switch camera and object
- `/stat` — events by camera, object, hour and day for 24 h / today / 7 d / 30 d, with buttons to switch the period
- `/clip`, `/mute`, `/unmute` — fetch a clip by event id, silence a camera for a while
- Optional buttons under a notification: clip, open in Lookout, mute 1 h

**Web UI** (port 8888)
- Live log with filters, newest lines first
- **Last events** as snapshot cards with in-page **video playback and download** (clips built from recordings, also for events still in progress)
- **Statistics**: cameras × objects matrix, activity by hour and day; click any cell or bar to see the events behind it as a gallery
- Every view has its own address, so it can be bookmarked or shared; browser Back / Forward work
- **Config** as a settings tree (cameras and extra notifier channels, even when they are not in the YAML yet) or YAML, applied without restarting the container
- **Search**, stats CSV export, PWA install
- Optional login (HTTP Basic or a sign-in form; admin / viewer roles)

**Integration**
- Re-publishes an event / review to MQTT with type `trueend` once its recording is complete, for automations (optional, per camera)
- Extra notification channels besides Telegram: ntfy, Discord, Matrix, generic webhook (Config tree always shows Notifiers; omit `notifiers:` and nothing extra is sent)
- **`/health`** (MQTT, Frigate, ffmpeg, database) and Prometheus **`/metrics`**
- **Localization**: web UI, Telegram and AI languages set separately (`en`, `ru`, `zh`, `es`; add more with a JSON file)
- Runs as a Docker container (Docker Hub and GitHub Container Registry)

## Requirements

- [Frigate NVR](https://frigate.video) with MQTT enabled
- MQTT broker
- Telegram bot token + local Bot API server (optional but recommended for large files)
- ffmpeg available in container (used for clips, GIFs and resizing snapshots for AI)
- Frigate HTTP API reachable at `frigate.host:frigate.port` (for snapshots of in-progress events and clips in the web UI)
- A vision model for AI descriptions (optional): Ollama by default, or an OpenAI-compatible / Gemini endpoint
- [CompreFace](https://github.com/exadel-inc/CompreFace) instance (optional, for face recognition)

## Quick Start

Image is available from both Docker Hub and GitHub Container Registry, as `latest` or a specific version (e.g. `2.11.35`, shown in the web UI footer):

```bash
# Docker Hub
docker pull rsvln/lookout:latest

# GitHub Container Registry
docker pull ghcr.io/rsvln/lookout:latest
```

```yaml
# docker-compose.yml
services:
  lookout:
    image: ghcr.io/rsvln/lookout:latest  # or rsvln/lookout:latest
    restart: unless-stopped
    volumes:
      - /etc/lookout:/etc/lookout
      - /var/log/lookout:/var/log/lookout
      - /srv/frigate/clips:/srv/frigate/clips:ro
      - /srv/frigate/recordings:/srv/frigate/recordings:ro
      - /srv/frigate/config/frigate.db:/srv/frigate/config/frigate.db:ro
    ports:
      - "8888:8888"
```

## Configuration

Config file: `/etc/lookout/lookout.yml`

```yaml
frigate:
  host: 192.168.1.10
  port: 5000
  clipspath: /srv/frigate/clips
  dbpath: /srv/frigate/config/frigate.db
  recordingspath: /srv/frigate/recordings
  recordingsoriginalpath: /media/frigate/recordings
  cameras:
    - camera: frontdoor
      snapshot: true
      clip: true
      gif: false               # send animated GIF preview
      ai: false                # enable AI snapshot analysis for this camera
      fr: false                # enable face recognition for this camera
      trueend: false
      sctogether: false        # send snapshot and clip separately
      snapshottrigger: new     # new | update | end
      topic: reviews           # reviews | events
      severity:
        - alert
        - detection
      objects:
        - label: person
          percent: 50
        - label: dog
          percent: 70
      zones: []                # leave empty to ignore zones
      cooldown: 0              # minutes of quiet after a notification (0 = off)
      cooldownperobject: false # a person right after a car still gets through

mqtt:
  host: 192.168.1.10
  port: 1883
  user: mqtt
  password: mqtt
  eventstopic: frigate/events
  reviewstopic: frigate/reviews

telegram:
  token: YOUR_BOT_TOKEN
  chatids:
    - '-1001234567890'
  clipsizecheck: 2147483648    # 2GB — if clip exceeds this, split
  clipsizesplit: 2000000000    # split chunk size
  mediagrouplimit: 10
  sendchatstimepause: 30       # seconds between chats when multiple
  retryonratelimit: 30         # seconds to wait on Telegram 429 rate limit (fallback if Retry-After not provided)
  apiserver: http://192.168.1.10:8081/  # local bot API server, leave empty for cloud

options:
  timeoffset: 0                # minutes to add to UTC for display
  timeout: 500                 # seconds to wait for recordings to be ready
  retry: 30                    # polling interval in seconds
  sendeverythingwhatyouhave: true  # send partial clips if timeout expires
  gifwidth: 640                # GIF preview width in pixels (height is proportional)
  retrymax: 0                  # disk retry queue: how many repeats on outage (0 = off)
  retrybackoff: 30             # first delay in seconds, doubled each time
  buttons: false               # Clip / Open in Lookout / Mute 1 h under each notification
  # quiet:                     # optional quiet hours (local time; may cross midnight)
  #   from: "22:00"
  #   to: "07:00"
  #   mode: silent             # silent | snapshot | none
  correlate: 0                 # seconds; events of different cameras in this window share an incident (0 = off)
  locale:                      # languages: en, ru, zh, es (files in locales/); "locale: ru" sets one for everything
    web: en                    # web UI
    telegram: ru               # Telegram messages and commands
    ai: ru                     # AI prompts and descriptions

logger:
  file: true
  console: true

# Optional: AI snapshot analysis (Ollama by default)
ai:
  provider: ollama             # ollama | openai | gemini
  url: http://192.168.1.20:11434
  model: "qwen2.5vl:7b"
  apikey:                      # OpenAI / Gemini; unused for Ollama
  humanprompt: "Briefly describe what the person is doing. What are they holding or carrying?"  # optional, default comes from locale.ai
  nonhumanprompt: "Briefly describe what is happening."                                          # optional, default comes from locale.ai
  numpredict: 150              # max tokens in the response, limits description length
  temperature: 0.1             # lower = more deterministic, higher = more creative
  resizetowidth: 640           # resize image before sending to the model, 0 to disable
  thinking: false              # enable chain-of-thought thinking mode; only useful for debugging, not recommended for production use

# Optional extra channels besides telegram: (caption + first snapshot, once per event/review)
# notifiers:
#   - type: ntfy
#     url: https://ntfy.sh/lookout
#     token:                       # optional Bearer token
#     title: Lookout
#     attach: true                 # attach the first snapshot
#   - type: discord
#     url: https://discord.com/api/webhooks/...
#   - type: matrix
#     homeserver: https://matrix.example
#     token: syt_...
#     room: "!roomid:example"
#   - type: webhook
#     url: http://127.0.0.1:9000/hook
#   - type: telegram               # extra chats; the telegram: block is still sent by workers
#     chatids:
#       - '-1001234567890'

# Optional: face recognition via CompreFace
fr:
  url: http://192.168.1.20:8000
  apikey: YOUR_COMPREFACE_API_KEY
  confidence: 0.8              # minimum similarity to consider a match (0.0 - 1.0)
  detprobthreshold: 0.8        # minimum probability that detected area is actually a face (0.0 - 1.0)

# Optional: password for the web UI; without it the UI is open to anyone who can reach port 8888
web:
  user: admin
  password: change-me
  auth: basic                  # basic (browser prompt) | form (login page)
  publicurl:                   # public origin of the UI, for "Open in Lookout" buttons
```

### Camera options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `snapshot` | bool | `true` | Send snapshots |
| `clip` | bool | `false` | Send video clips |
| `gif` | bool | `false` | Send animated GIF preview generated from the clip |
| `ai` | bool | `false` | Enable AI snapshot analysis for this camera (requires `ai` section) |
| `fr` | bool | `false` | Enable face recognition for this camera (requires `fr` section) |
| `trueend` | bool | `false` | Re-publish event/review to the same MQTT topic with `type: trueend` once all recordings are confirmed ready in Frigate DB. Useful for triggering downstream automations only when the clip is complete. |
| `sctogether` | bool | `false` | Send snapshot and clip in one media group |
| `snapshottrigger` | string | `end` | When to send snapshot: `new`, `update`, or `end` |
| `topic` | string | `reviews` | Which MQTT topic to use: `reviews` or `events` |
| `severity` | list | `[detection, alert]` | Frigate review severity filter |
| `objects` | list | `[]` | Filter by object label and minimum confidence `percent` (the event's best score; for reviews, the best score of its detections) |
| `zones` | list | `[]` | Filter by Frigate zone names (empty = all zones) |
| `cooldown` | int | `0` | Minutes of quiet after this camera has sent a notification (`0` = off) |
| `cooldownperobject` | bool | `false` | Count the cooldown per object type |
| `quiet` | object | — | Quiet hours for this camera (`from` / `to` as `HH:mm`, `mode`: `silent`, `snapshot` or `none`); otherwise `options.quiet` |

## GIF Previews

When `gif: true` is set for a camera, Lookout generates an animated GIF from the recorded clip using ffmpeg (8 fps, 8x speed) and sends it as a separate Telegram animation. Width is controlled globally via `options.gifwidth`.

## Face Recognition

When the `fr` section is present and `url`/`apikey` are set, enabling `fr: true` on a camera will run face recognition on snapshots via [CompreFace](https://github.com/exadel-inc/CompreFace) before posting results to Telegram.

If `ai: true` is also enabled on the camera, recognized names are automatically passed to the AI as context, enriching the description prompt. If only `fr` is enabled without `ai`, the caption is updated with recognized names directly.

To set up CompreFace, use the provided [`docker-compose-compreface.yml`](https://github.com/rsvln/lookout/blob/master/docker-compose-compreface.yml) (`docker compose -f docker-compose-compreface.yml up -d`; its web UI is on port 8000, which is the address for `fr.url`), then open the web UI, create an application, add a Recognition Service, and upload face photos for each person via the Train section.

## AI Analysis

When the `ai` section is present and `url`/`model` are set, enabling `ai: true` on a camera will:

1. Send all snapshots to the configured provider after posting to Telegram
2. Edit the Telegram message caption with AI descriptions for each snapshot

`ai.provider` is `ollama` by default. `openai` talks to `{url}/v1/chat/completions` with `Authorization: Bearer {apikey}` (any OpenAI-compatible server). `gemini` talks to `{url or https://generativelanguage.googleapis.com}/v1beta/models/{model}:generateContent?key=`.

Uses the `humanprompt` if Frigate detected a person, `nonhumanprompt` otherwise. Without them, the default prompts of the `locale.ai` language are used. When `options.locale.ai` is set, every prompt ends with "answer in <that language>", so descriptions come in that language whatever language the prompt is written in; without it the prompts are sent as written. If face recognition is also enabled, recognized names are prepended to the prompt automatically.

Snapshots wider than `resizetowidth` are downscaled with ffmpeg before being sent to the model.

Tested with `qwen2.5vl:7b` on a machine with RTX 3060 — ~2 seconds per image.

## Extra notifiers

The `telegram:` block still sends albums the way it always did. Extra channels (ntfy, Discord, Matrix, webhook, extra Telegram chats) sit under **Notifiers** in the Config tree even when `notifiers:` is missing from the YAML — add a channel there to write it. Each extra channel gets the caption and the first snapshot once per event or review (not once per Telegram chat). Omit the section and nothing extra is sent.

| `type` | Required | Notes |
|--------|----------|--------|
| `ntfy` | `url` | POST to the topic URL; optional `token`, `title`, `attach` |
| `discord` | `url` | Incoming webhook; snapshot as an attachment |
| `matrix` | `homeserver`, `token`, `room` | Uploads the snapshot then `m.room.message` |
| `webhook` | `url` | JSON `{kind,id,camera,body,silent}` |
| `telegram` | `chatids` | Extra chats only; does not replace `telegram:` |

Failed extra notifiers go through the retry queue (`kind: notify`) when `options.retrymax` is set.

## Local event database

Lookout writes sent events, AI descriptions and recognized faces to `lookout.db` next to the YAML config (`/etc/lookout/lookout.db` in the usual container setup). The Search tab uses that file for face names and AI text; camera / object / id / zone still come from Frigate as well.

`options.correlate` (seconds, default `0` = off) groups events of different cameras in that window into one incident: the later caption lists the other cameras, and the AI prompt gets them as context.

## Localization

Telegram messages, bot commands, the web UI and AI descriptions are translated. `options.locale` sets the language: one value for all of them (`locale: ru`), or `web`, `telegram` and `ai` under it for each separately, e.g. the web UI in English with Telegram and AI in Russian. Default is `en`, also for an area left out.

Strings live in `locales/<locale>.json` next to the app (`/app/locales` in the container), one flat `"key": "text"` file per language; `en.json`, `ru.json`, `zh.json` and `es.json` are included. To add a language, copy `en.json`, translate the values and set `locale:` to that file's name (or that name for one area). Keys missing in a translation fall back to English.

Object names from every locale file are understood in commands, e.g. `/last человек` works with any `locale`.

## Web UI

Available at `http://<host>:8888`. The Config tab shows the bot token and MQTT password, so set `web.user` / `web.password` if the port is reachable by others.

- **Log** — live log viewer with filtering by type, camera, text, color-coded by event ID
- **Last** — latest N events of every camera (grouped by camera) or the history of one camera, as snapshot cards with object, score, time and zones. Click a snapshot to enlarge it. Every card has **▶ Video** (plays the clip in the page, with seeking) and **⬇** (downloads it); the clip is built from the camera's recording segments, so it works even when Frigate has no clip of its own, and for an event still in progress it covers the recording up to now. Cards load a snapshot from Frigate's clips folder, Lookout's `live/` cache, or Frigate's HTTP snapshot/thumbnail if the file is gone
- **Stats** — events / alerts / detections for a period (24 h, today, 7 d, 30 d), cameras × objects matrix, activity by hour of day and by day. The object filter defaults to **Config** — only cameras and objects (with their `percent` thresholds) the bot is configured to send; **All** shows everything Frigate saw. Click a matrix cell to drill down; **← Back** returns to the previous view or to the overview
- **Search** — events by camera, object, time, id, zone, recognized face or AI description (`lookout.db`)
- **Config** — **Form** is a settings tree (Frigate, each camera, MQTT, Telegram, extra notifiers, AI, face recognition, …). **+ Camera** / **+ Channel** add cameras and extra notification channels even if they are not in the YAML yet; **Remove** drops them. **YAML** is the full file with highlighting, error underlining, line numbers, folding and search (Ctrl+F); Tab and pasted tabs become spaces. **Save** writes the file (with a `.bak` backup), **Apply** restarts the services with the saved file, **Save & apply** does both. A config with YAML errors or missing sections is not saved
- **About** — version, build date, links and this manual with highlighted code

Every view has its own address, with the filters in it, so it can be bookmarked or shared and the browser's Back / Forward work: `/log?camera=homecam02&type=review`, `/last?camera=homecam01&label=car`, `/stats?period=7d&label=person`, `/stats/events?period=24h&camera=homecam01&hour=8` (the events behind a stats cell or chart bar), `/search?q=person`, `/event/<id>` (one event; the time on every card links to it), `/config`, `/about`. Opening `/` shows the view seen last. The version and build date are shown in the footer of every page.

## Telegram commands

Commands are accepted only from chats listed in `telegram.chatids`. Data for `/last` and `/stat` is read from the Frigate database (`frigate.dbpath`) and snapshots from `frigate.clipspath`.

| Command | Description |
|---------|-------------|
| `/status` | Current frame from every camera |
| `/last` | Latest event of every camera, plus buttons to pick a camera, an object or N per camera |
| `/last [N] [object]` | Last N events of every camera (default 1), e.g. `/last 3`, `/last 2 person` |
| `/last <camera> [N] [object]` | Last N events of one camera (default 5), e.g. `/last frontdoor 10`, `/last frontdoor dog` |
| `/stat [period] [camera or object]` | Event statistics by object, camera and hour of day, with buttons to switch the period; period is `24h` (default), `7d`, `30d` or `today` |
| `/clip <id>` | Clip of an event (the id is in the notification) |
| `/mute [camera] [30m / 2h / 1d]` | Turn notifications off (all cameras, 1 hour by default) |
| `/unmute [camera]` | Turn notifications back on |
| `/help` | Help |

Objects can be given by their Frigate label (`person`) or by their name in any locale file (`человек`).

## Building from source

```bash
docker build -t lookout -f lookout/Dockerfile .
```

## License

MIT