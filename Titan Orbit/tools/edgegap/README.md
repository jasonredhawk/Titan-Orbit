# Titan Orbit — Edgegap dedicated server setup

Host the authoritative Linux headless server on [Edgegap](https://docs.edgegap.com/unity) instead of (or alongside) GCE.

**Important:** Clients use **Join Game** (UGS Lobby). Standalone/Android then UDP-connect to the lobby `HostAddress:HostPort`. WebGL still uses the WebSocket driver. Edgegap runs the dedicated process; it does not replace the lobby browser.

---

## Prerequisites (one-time)

| Requirement | Notes |
|-------------|--------|
| [Edgegap account](https://app.edgegap.com/auth/register) | Free tier; verify email |
| [Docker Desktop](https://www.docker.com/products/docker-desktop/) | Required for containerize + local test |
| Unity Hub modules | **Linux Build Support (IL2CPP)**, **Linux Dedicated Server Build Support** |
| [Edgegap Unity plugin](https://github.com/edgegap/edgegap-unity-plugin) | Package Manager → Add from git URL: `https://github.com/edgegap/edgegap-unity-plugin.git` |
| Git for Windows | Needed for Package Manager git URLs |

After install you should see **Tools → Edgegap Hosting** in the Unity menu.

Optional later: [Edgegap Unity SDK](https://github.com/edgegap/edgegap-unity-sdk.git) for matchmaking / deployment API (not required for first deploy).

---

## Architecture (Titan Orbit specific)

```
Edgegap deployment (Docker)
  → TitanOrbitServer / ServerBuild (UNITY_SERVER, IL2CPP)
  → Unity Gaming Services (guest auth)
  → Relay allocation + UGS Lobby (IsLatest, RelayJoinCode)
  → Clients join via Join Game browser (WebGL / standalone)
```

You do **not** need Edgegap’s “Port Verification” bootstrap scripts (those target Mirror, NGO, FishNet, etc.). NetCode + Relay is already wired in `TitanOrbitSessionManager`.

---

## Step-by-step in Unity

### 1. Connect Edgegap account

1. Open **Tools → Edgegap Hosting**
2. Sign in / connect account
3. Confirm no errors in the Console

### 2. Build the Linux server

**Recommended:** **Tools → Edgegap Hosting → Build server**

Output: `Builds/EdgegapServer/ServerBuild.x86_64` (+ `ServerBuild_Data/`). Delete that folder first for a clean rebuild.

The plugin names the binary `ServerBuild` so it matches Edgegap’s Dockerfile and ours.

**Build settings checklist:**

- `Assets/Scenes/SampleScene.unity` is enabled in **File → Build Settings**
- If build fails with OpenXR errors: disable OpenXR for server / remove from server build (client-only)

### 3. Containerize (Docker)

In **Tools → Edgegap Hosting**, section **Containerize**:

| Field | Value |
|-------|--------|
| **Build path** | `Builds/EdgegapServer` |
| **Path to Dockerfile** | `tools/edgegap/Dockerfile` |
| **Image name** | e.g. `titan-orbit-server` |
| **Image tag** | timestamp, e.g. `2026.07.11-14.00.00-UTC` |

Click **Validate** (Docker running), then **Containerize with Docker**.

Our Dockerfile:

- Uses Ubuntu 22.04 + CA certs (Unity Services HTTPS)
- Runs `tools/edgegap/start-server.sh` with GCE-equivalent CLI flags (`-batchmode` only — no `-nographics` / SDL dummy; those stall NullGfx at ~11 Hz)

### 4. Test locally (Titan Orbit — read this)

Use **Join game** (UGS lobby list). The client then UDP-connects to the lobby `Host=` — for local Docker that must be `127.0.0.1:7777`.

**Full walkthrough:** [LOCAL-DOCKER-TEST.md](./LOCAL-DOCKER-TEST.md)

Quick version:

1. **Deploy local container** with `-p 7777:7777/udp`.
2. Confirm Docker logs: `Dedicated server live. Host=127.0.0.1:7777 Lobby=...` and `[MapGeneration] Map generated`.
3. Unity Editor **Play** → main menu → **Join game** → **Refresh** → join the listed lobby.
4. After loading, **pick a team** — your ship spawns only after team selection (not on connect).
5. Do **not** paste Edgegap’s dummy `162.254.141.66:31504` into the transport.

Stop/delete the test container when done.

### 5. Upload to Edgegap

In the plugin, **Upload image and create App version**:

- Application name: e.g. `titan-orbit-server`
- Version tag: same as Docker tag
- Complete the dashboard form when it opens

**Port mapping (App version):**

| Name | Internal | Protocol |
|------|----------|----------|
| `gameport` | `7777` | **UDP** |

Titan Orbit uses Relay for player traffic; this port satisfies Edgegap’s mapping and matches `--serverPort` defaults. Internal port can be overridden at runtime via `ARBITRIUM_PORT_GAMEPORT_INTERNAL`.

**Resources (free tier):** 1.5 vCPU, 3 GB RAM — increase in app version if you see `OOM kill` in deployment logs.

### 6. Deploy to cloud

1. Select app + version in the plugin → **Deploy to Cloud**
2. Wait until deployment status is **Ready**
3. Open [Deployments](https://app.edgegap.com/deployment-management/deployments/list) → container logs

**Verify:**

- Logs show dedicated boot + UGS lobby published
- Client **Join Game** lists an open `IsLatest` lobby (may take 30–90s after Ready)
- Free-tier deployments auto-stop after **60 minutes** — stop manually when testing

---

## Environment variables

Edgegap injects `ARBITRIUM_*` variables at runtime. `TitanOrbitEdgegapEnvironment` reads port mapping and logs deployment id/IP.

Optional overrides in the Edgegap app version (custom env):

| Variable | Default | Purpose |
|----------|---------|---------|
| `TITANORBIT_MAX_PLAYERS` | `60` | Lobby capacity |
| `TITANORBIT_IS_LATEST` | `1` | First instance publishes `IsLatest=1` lobby (funnel badge) |
| `TITANORBIT_EMPTY_MATCH_RECREATE_SECONDS` | `1800` | Recycle empty rooms after 30 min |
| `TITANORBIT_AGE_THRESHOLD_SECONDS` | `900` | Open a successor after ~15 min when enough players are in |
| `TITANORBIT_SOFT_FILL_MIN_PLAYERS` | `8` | Min players before age-split |
| `TITANORBIT_MAX_CONCURRENT_GAMES` | `5` | Cap on Edgegap deployments |
| `EDGEGAP_API_TOKEN` | (required for overflow) | Server-only token for v2 deploy |
| `EDGEGAP_APP_NAME` / `EDGEGAP_APP_VERSION` | (required for overflow) | App identity for successor deploys |
| `UNITY_COMMANDLINE_ARGS` | (empty) | Extra flags appended by Edgegap plugin |

---

## GCE vs Edgegap

| | GCE (existing) | Edgegap (this guide) |
|--|----------------|----------------------|
| Build | Headless Server (Linux — Google Cloud) | **Tools → Edgegap Hosting → Build server** |
| Output | `BuildOutput/Server/TitanOrbitLinux1/` | `Builds/EdgegapServer/` |
| Deploy | `tools/gce/*.bat` | Edgegap plugin + Docker |
| Client join | UGS Lobby + Relay | Same |
| 24/7 | systemd on VM | Edgegap fleet / matchmaking (see below) |

You can keep GCE for a permanent “latest” lobby and use Edgegap for on-demand matches later, or migrate fully once Edgegap is stable.

---

## Troubleshooting

| Symptom | What to check |
|---------|----------------|
| `PrepareDedicatedRelay failed: UGS not ready` | Container outbound HTTPS; `cloudProjectId` baked in build (`ProjectSettings`); Unity Dashboard services enabled |
| Container exits immediately | `docker logs <container>` — IL2CPP missing `.so`, wrong binary path, or boot timeout |
| No lobbies in Join Game | Deployment logs for lobby publish errors; wait after Ready; check UGS project matches client |
| `Port verification failed` | Ignore for Relay architecture unless you add direct LAN listen; ensure app version UDP 7777 anyway |
| Docker build huge / slow | `.dockerignore` excludes `Library/`, `Assets/`, etc. — only `Builds/EdgegapServer` + `tools/edgegap` sent to daemon |
| `missing script on Asteroid` in Docker logs | Fixed: headless server no longer runs `EcsWorldVisualizer` (rebuild server after pull) |
| Free tier limits | 2 apps / 2 versions — delete old versions in dashboard |

Edgegap Discord: [Community Discord](https://discord.gg/NgCnkHbsGp)

---

## Next steps (production)

Manual deploy + paste IP/port does not scale. When ready:

1. **[Edgegap Matchmaking](https://docs.edgegap.com/learn/matchmaking.md)** or **Server Browser** — start/stop deployments per session
2. **Edgegap Unity SDK** — `DeploymentAgent` sample for stop-on-empty and typed env vars
3. **Endpoint Storage** — persist deployment logs after stop

Related repo docs: `Docs/server-hosting-24_7.md`, `tools/gce/README.md`

Official guide: [Unity - Getting Started](https://docs.edgegap.com/unity)
