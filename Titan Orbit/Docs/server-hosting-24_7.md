# 24/7 Headless Server Hosting (Relay + Lobby)

Your dedicated server process (the headless Windows/Linux build) creates:
- one Relay allocation per match
- one UGS Lobby per match (with `IsOpen`, `IsLatest`, `CreatedAtEpoch`, and `RelayJoinCode` (member-only))
- a NetCode server world (listen + ghosts)

Match rotation is handled inside the server process. Lifecycle rules:

| Condition | What happens |
|-----------|----------------|
| **Players connected** | Match keeps running and stays `IsOpen=1`. Idle teardown does **not** run. |
| **Last player leaves** (0 connections) | Empty-idle countdown **starts/resets from that moment**. Orphan ships wiped; map stays until timeout. |
| **Empty for 30 minutes** (`emptyMatchRecreateSeconds`) | In-process recreate: new Relay + lobby, wipe ships, same process. |
| **Age ~30 minutes** while occupied + IsLatest + not full (`ageThresholdSeconds`) | Spawn a successor process as the new `IsLatest`. **Demote** this lobby (`IsLatest=0`) but **keep `IsOpen=1`** so conquest maps stay on Join Game. |
| **Lobby full** (max players) | Close listing (`IsOpen=0`) and spawn successor capacity. |

That means you only need to start ONE server instance 24/7; it will spawn additional match processes automatically when age/full rotation needs a fresh “latest” slot — without killing occupied maps.

## Build artifacts

Use the editor menu commands:
- `TitanOrbit/Build/WebGL Production`
- `TitanOrbit/Build/Headless Server (Windows)`
- `TitanOrbit/Build/Headless Server (Linux — Google Cloud)` for GCE

The server build output folder is controlled by `TitanOrbitBuildAutomation.cs`:
- `BuildOutput/Server/headless-windows/` (Windows)
- `BuildOutput/Server/TitanOrbitLinux1/` (Linux / GCE)

## Linux systemd example (adjust paths)

1. Copy the Linux headless binary to a persistent location on the host (example: `/opt/titanorbit/server/`).
2. Ensure the process can write logs (example: `/var/log/titanorbit/`).
3. Create `/etc/systemd/system/titanorbit-matchserver.service`:

```ini
[Unit]
Description=TitanOrbit headless match server
After=network-online.target
Wants=network-online.target

[Service]
WorkingDirectory=/opt/titanorbit/server
ExecStart=/opt/titanorbit/server/TitanOrbitServer --titanOrbitDedicated=1 --maxPlayers=60 --serverPort=7777 --relayProtocol=dtls --isLatest=1
Restart=always
RestartSec=5
StandardOutput=append:/var/log/titanorbit/server.out
StandardError=append:/var/log/titanorbit/server.err

[Install]
WantedBy=multi-user.target
```

4. `systemctl daemon-reload`
5. `systemctl enable --now titanorbit-matchserver`

Notes:
- Dedicated auto-boot is gated by `--titanOrbitDedicated=1` (and batchmode/nographics for editor-less runs).
- The process can spawn additional match server processes using the same executable path it is running from.
- Override idle/age with `--emptyMatchRecreateSeconds=` and `--ageThresholdSeconds=` (defaults: 1800 each).

## What to monitor

1. Server logs:
   - `[TitanOrbitSessionManager] Dedicated server live...`
   - `[TitanOrbitDedicatedServerHost] Age rotation...` / `Handoff complete... demoted_keep_open` / `closed`
   - `[TitanOrbitDedicatedServerHost] Last player left — empty-idle countdown started`
   - `empty_match_recreate` only when the match was empty for the idle window
2. UGS lobbies:
   - At most one lobby should be `IsLatest=1` for the free “new game” flow.
   - Occupied non-full matches after age rotation stay `IsOpen=1` with `IsLatest=0`.
   - `IsOpen` flips to `0` when full or after empty idle recreate of the old lobby.
3. Relay connections:
   - If WebGL fails to connect, check CSP headers (Cloudflare `_headers`) and verify `wss`/`dtls` end-to-end.

## Scaling / concurrency expectations

Each match server process runs its own NetCode server + Relay allocation + Lobby.
Concurrent matches scale by running more processes (spawned by the “latest” match as it rotates).

Practical guidance:
- Start with low traffic and watch how many processes are created over 1-2 hours.
- If you later want an absolute cap (for cost control), add a limit to the rotation logic (e.g., max spawned processes) before going public.
