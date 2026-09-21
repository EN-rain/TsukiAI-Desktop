# TsukiAI domains: three independent tunnels, five subdomains

Deployment verified 2026-09-19. These are Cloudflare hostnames under the
`tsukihime.dpdns.org` zone, **not** one shared notebook tunnel.

| Tunnel / runtime | Hostname | Origin / function |
| --- | --- | --- |
| `tsukis-bedroom` (VPS) | `api.tsukihime.dpdns.org` | Cloudflare tunnel → Caddy port 80 → C# API/SPA |
| `tsukis-bedroom` (VPS) | `profile.tsukihime.dpdns.org` | Same Caddy → read-only `profile-site/` |
| `tsukis-bedroom` (VPS) | `controls.tsukihime.dpdns.org` | Host-only port 8099, password-protected VPS dashboard |
| `tsuki-molab-qwen-custom` (Molab) | `molab-tts.tsukihime.dpdns.org` | Manual notebook's separate cloudflared connector → TTS |
| `tsuki-colab-qwen-custom` (Colab) | `colab-tts.tsukihime.dpdns.org` | Manual notebook's separate cloudflared connector → TTS |

VPS tunnel ID: `74225131-b66e-423e-8a0c-4a4f71a52ba8`. Its remotely
managed ingress routes `api` and `profile` to `http://127.0.0.1:80`,
`controls` to `http://127.0.0.1:8099`, then ends in `http_status:404`.
The profile page was mirrored from its already-live public HTML/CSS before
its preexisting Cloudflare DNS record was switched to the VPS tunnel. The
profile site is now served from `profile-site/index.html` and
`profile-site/styles.css`; publishing changes to its previous host will
not automatically update these VPS copies.

Molab and Colab tunnels, notebook Cloudflare credentials, manual startup
and TTS endpoints are **not redirected through the VPS**, nor are their
DNS routes overwritten. An offline notebook can report a Cloudflare 530
without indicating an outage of the VPS tunnel.

## Controls

`controls/` is a separate Python stdlib service installed on the VPS as
`tsuki-controls.service`. It binds only to `127.0.0.1:8099` on the VPS
host. No VPS public listener on port 8099. HTTPS is handled by Cloudflare.
The control site requires the existing VPS `TSUKI_WEB_PASSWORD` (or an
explicit `TSUKI_CONTROLS_PASSWORD` override), Secure/HttpOnly/SameSite
cookies, CSRF token and Origin checks. It publishes no arbitrary command
runner, raw Docker daemon/API, full environment, tokens or raw logs.
Further Cloudflare Access restrictions can be layered on but are not
configured by this deployment.

Dashboard: container running states and CPU/memory stats, host load/RAM/disk/
uptime, bot guild/voice/text channel IDs, Molab/Colab availability. Allowed
actions: start/stop/restart bot bridge, restart API, Caddy and ChromaDB.
Validated bot configuration: guild/voice/text IDs, mention-vs-all text reply,
and text-chat voice off/keywords/all. Configuration writes create private
backups, preserve other `.env` values, and recreate **only** the bot bridge.
`/t` in Discord handles live channel join/leave. There is no shell or
unrestricted container mutation via the dashboard.

## Deployment / rollback

VPS: `/opt/tsukiai`; Caddy uses `Caddyfile`, Compose uses
`docker-compose.yml`, static site files use `profile-site/`.
Existing VPS Caddy and Compose files were backed up to
`/opt/tsukiai/.deploy-backups/three-domains-20260919/`.
The previous remote tunnel configuration was saved locally at
`~/.cloudflared/tsukis-bedroom-config-backup-20260919.json`.
Profile's prior DNS record target was not readable with the available DNS
API permission; do not assume an automatic DNS rollback is available.
Avoid sharing tunnel tokens, bot tokens, populated notebooks, or VPS `.env`.
