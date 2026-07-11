# Titan Orbit — Edgegap dedicated server setup

Host the authoritative Linux headless server on [Edgegap](https://docs.edgegap.com/unity) instead of (or alongside) GCE.

**Important:** Titan Orbit clients do **not** connect to the deployment IP/port directly. The headless process creates a **UGS Lobby + Unity Relay** allocation; WebGL and standalone clients join through the normal **Join Game** flow (same as GCE today). Edgegap runs the server **process**; Relay still carries gameplay traffic.

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

**Recommended (this repo):**

**TitanOrbit → Build → Headless Server (Linux — Edgegap)**

Output: `Builds/EdgegapServer/ServerBuild.x86_64` (+ `ServerBuild_Data/`).

This uses the same IL2CPP Dedicated Server settings as the GCE build, but names the binary `ServerBuild` so it matches Edgegap’s default Dockerfile and our custom one.

Alternatively, use **Build server** inside the Edgegap window (same folder if you keep defaults).

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
- Runs `tools/edgegap/start-server.sh` with GCE-equivalent CLI flags
- Sets `SDL_VIDEODRIVER=dummy` for headless Linux

### 4. Test locally (optional but recommended)

In the Edgegap window, **Deploy local container**:

- **Optional docker run parameters:** `-p 7777/udp` (metadata port; Relay still used for gameplay)
- Start container, then open **Docker Desktop → Containers** and check logs

**Success in logs:**

- `[TitanOrbitSessionManager] Dedicated server live. Relay=... Lobby=...`
- `[TitanOrbitEdgegapEnvironment]` line if you passed mock ARBITRIUM env vars

**How to test gameplay:** use your normal client (Editor or WebGL) → **Join Game** → pick the new lobby. Do **not** point the client at `localhost:7777` unless you are doing a LAN test without Relay.

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
| `TITANORBIT_RELAY_PROTOCOL` | `dtls` | Relay connection type on Linux server |
| `TITANORBIT_IS_LATEST` | `1` | First instance publishes `IsLatest=1` lobby |
| `UNITY_COMMANDLINE_ARGS` | (empty) | Extra flags appended by Edgegap plugin |

---

## GCE vs Edgegap

| | GCE (existing) | Edgegap (this guide) |
|--|----------------|----------------------|
| Build menu | Headless Server (Linux — Google Cloud) | Headless Server (Linux — Edgegap) |
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
| Docker build huge / slow | `.dockerignore` at project root excludes `Library/`, `BuildOutput/` |
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
