# TVHeadend plugin — Playwright end-to-end tests

Real-browser end-to-end tests that drive a running Jellyfin instance (with the TVHeadend plugin)
and verify live-TV playback, the play-method decisions, the dashboard/metrics, and the settings.
They run against a **live Jellyfin + TVHeadend backend** — they are not unit tests.

## What is covered

| Spec | What it checks |
|------|----------------|
| `01-settings.spec.js` | Plugin installed; managed `jellyfin` profile is the effective default; diagnostics OK and **transcode capability detected** (no false "audio-only" warning); config page renders. |
| `02-dashboard.spec.js` | `Dashboard`, `RelayMetrics`, `Metrics/Live` APIs return well-formed data; dashboard page renders. |
| `03-playback-methods.spec.js` | **Direct Play**, **Direct Stream** and **Transcode** decisions via crafted device profiles; the HLS transcode playlist actually loads. |
| `04-playback-browser.spec.js` | **Regression test:** a channel actually plays in real Chromium with no fatal media/decode error (catches the AAC‑Main → `PIPELINE_ERROR_DECODE` bug). |
| `05-soak.spec.js` (`@soak`) | Sustained playback for `SOAK_SECONDS` (default **300 s / 5 min**); detects stalls/dropouts/late decode errors; cross-checks that the relay reports the active stream. |

## Prerequisites

- Node.js 18+.
- A reachable Jellyfin with the plugin configured against a TVHeadend backend with at least one channel.
- Run `npm run setup` once (installs the Chromium browser).

## Configuration (environment variables)

| Variable | Default | Meaning |
|----------|---------|---------|
| `JELLYFIN_URL` | `http://localhost:8096` | Jellyfin base URL |
| `JF_USER` / `JF_PASS` | `admin` / `admin123` | Jellyfin credentials |
| `JF_CHANNEL` | _(first channel)_ | Channel name to use for playback tests |
| `SOAK_SECONDS` | `300` | Soak duration in seconds |

## Running

```bash
cd tests/playwright
npm install
npx playwright install chromium   # first time only

npm test            # everything except the long @soak test
npm run test:soak   # only the 5-minute soak (no per-test timeout)
npm run test:all    # full suite incl. soak
npm run report      # open the last HTML report
```

Example against a remote server:

```bash
JELLYFIN_URL=http://192.168.0.169:8096 JF_USER=admin JF_PASS=admin123 SOAK_SECONDS=300 npm run test:all
```

> The tests run **serially** (`workers: 1`) because they share one backend and a limited number of
> tuners. The transcode test stops its encoding afterwards; the soak test holds one tuner for its
> whole duration.
