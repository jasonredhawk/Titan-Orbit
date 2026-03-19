# 24/7 Headless Server Hosting (Relay + Lobby)

Your dedicated server process (the headless Windows build) creates:
- one Relay allocation per match
- one UGS Lobby per match (with `IsOpen`, `IsLatest`, `CreatedAtEpoch`, and `RelayJoinCode` (member-only))
- a Netcode server (`NetworkManager.StartServer()`)

Match rotation is handled inside the server process. When it hits:
- `20 minutes` age (only if the match is currently `IsLatest` and not full): it updates its own lobby to `IsLatest=0` and spawns the next match as `IsLatest=1`
- `max players` (60): it closes its lobby (`IsOpen=0`, `IsLatest=0`) and spawns the next match (the next match will only be `IsLatest=1` if the closed lobby was `IsLatest=1`)

That means you only need to start ONE server instance 24/7; it will spawn additional match processes automatically.

## Build artifacts

Use the editor menu commands:
- `TitanOrbit/Build/WebGL Production`
- `TitanOrbit/Build/Headless Server (Windows)`

The server build output folder is controlled by `TitanOrbitBuildAutomation.cs`:
- `BuildOutput/Server/headless-windows/TitanOrbitServer.exe` (name may vary slightly after build)

## Linux systemd example (adjust paths)

1. Copy `TitanOrbitServer` to a persistent location on the host (example: `/opt/titanorbit/server/`).
2. Ensure the process can write logs (example: `/var/log/titanorbit/`).
3. Create `/etc/systemd/system/titanorbit-matchserver.service`:

```ini
[Unit]
Description=TitanOrbit headless match server
After=network-online.target
Wants=network-online.target

[Service]
WorkingDirectory=/opt/titanorbit/server
ExecStart=/opt/titanorbit/server/TitanOrbitServer.exe -batchmode -nographics ^
  --maxPlayers=60 ^
  --serverPort=7777 ^
  --relayProtocol=wss ^
  --isLatest=1
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
- `-batchmode -nographics` is important: the server bootstrap starts only when `Application.isBatchMode` is true.
- The process spawns additional match server processes using the same executable path it is running from.

## What to monitor

1. Server logs:
   - `[DedicatedMatchServerBootstrap] Starting server for lobby ...`
   - `[DedicatedMatchServerBootstrap] 20min rotation: spawned next match ...`
   - `[DedicatedMatchServerBootstrap] Lobby full rotation: spawned next match ...`
2. UGS lobbies:
   - Ensure at most one lobby is `IsLatest=1` for the free flow.
   - Ensure `IsOpen` flips to `0` when full (so free/paid queries won’t show closed matches).
3. Relay connections:
   - If WebGL fails to connect, check CSP headers (Cloudflare `_headers`) and verify `wss` is used end-to-end.

## Scaling / concurrency expectations

Each match server process runs its own Netcode server + Relay allocation + Lobby.
Concurrent matches scale by running more processes (spawned by the “latest” match as it rotates).

Practical guidance:
- Start with low traffic and watch how many processes are created over 1-2 hours.
- If you later want an absolute cap (for cost control), add a limit to the rotation logic (e.g., max spawned processes) before going public.

