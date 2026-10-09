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
| **Empty for 1 hour** (`emptyMatchRecreateSeconds`, default 3600) | In-process recreate: new Relay + lobby, wipe ships, same process (only when **0 players**). |
| **Empty process recycle** (RSS / struggling / idle count) | Spawn new IsLatest sibling **first**, wait until browseable, then close old + exit **0**. systemd must be `Restart=on-failure` so a second Unity is not started. Handoff failure → demote-keep-open + exit 1 (cold restart with brief overlap). |
| **In-process idle recreate** | Create new lobby **before** closing old (no zero-lobby window). |
| **RSS over budget** (`rssRecycleMb`, default **3500**) while empty | Triggers empty recycle handoff above. |
| **Sustained STRUGGLING** (`strugglingSamplesBeforeRecycle`, default **3** ≈ 30s) while empty | Triggers empty recycle handoff above. |
| **Main thread hung** (`mainThreadHangQuitSeconds`, default **300**; paused during recreate) | Background watchdog hard-exits code 1 so systemd restarts. |
| **Memory telemetry** (`memoryLogIntervalSeconds`, default **60**) | `memory` lines in `TitanOrbitDedicatedServer.log`: rssMb, entity counts, emptyRecreates, rssDeltaMb. |
| **Match age** (`ageThresholdSeconds`, default **0**) | Does **not** open a second game. Set above 0 only for rotation tests. |
| **Roster full** (teams × max per team, else `--maxPlayers`) | Close listing (`IsOpen=0`) and spawn successor capacity. Not while seats remain. |

That means you only need to start ONE server instance. It keeps that match until the roster is full, then spawns the next one. An empty match is replaced in-process after one hour. It does not open a second lobby just because the first one is old.

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
- Override idle with `--emptyMatchRecreateSeconds=` (default 3600). `--ageThresholdSeconds=0` (default) does not open a second game; set it above 0 only for rotation tests.
- Process recycle: `--maxInProcessEmptyRecreates=6`, `--rssRecycleMb=3500`, `--strugglingSamplesBeforeRecycle=3`, `--memoryLogIntervalSeconds=60` (0 disables each). Hang quit: `--mainThreadHangQuitSeconds=300`.
- systemd: **`Restart=on-failure`** (not `always`). Successful empty handoff exits **0** with a live sibling; exit **1** still restarts after crashes.
- Grep handoff / gaps: `grep -E 'Recycle handoff|published NEW lobby first|memory' TitanOrbitDedicatedServer.log`
- Grep overnight logs: `grep memory TitanOrbitDedicatedServer.log` (watch `rssMb` vs `emptyRecreates` / `rssDeltaMb`).

## What to monitor

1. Server logs:
   - `[TitanOrbitSessionManager] Dedicated server live...`
   - `[TitanOrbitDedicatedServerHost] Full rotation...` / `Handoff complete... closed` (age rotation only if `--ageThresholdSeconds` is set)
   - `[TitanOrbitDedicatedServerHost] Last player left — empty-idle countdown started`
   - `empty_match_recreate` only when the match was empty for the idle window
2. UGS lobbies:
   - One open game while it still has seats. A second lobby appears only after that roster is full.
   - Two empty lobbies must not stay listed together. The older empty process closes its lobby and exits.
   - `IsOpen` flips to `0` when the roster is full or after the 1-hour empty recreate of the old lobby.
3. Relay connections:
   - If WebGL fails to connect, check CSP headers (Cloudflare `_headers`) and verify `wss`/`dtls` end-to-end.

## Scaling / concurrency expectations

Each match server process runs its own NetCode server + Relay allocation + Lobby.
Concurrent matches scale by running more processes (spawned by the “latest” match as it rotates).

Practical guidance:
- Start with low traffic and watch how many processes are created over 1-2 hours.
- If you later want an absolute cap (for cost control), add a limit to the rotation logic (e.g., max spawned processes) before going public.
