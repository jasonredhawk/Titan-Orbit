# Test Edgegap local Docker container (Titan Orbit)

Edgegap’s **Test Your Server Locally** runs your Linux headless server in Docker on your PC.  
**Titan Orbit does not connect like Mirror/NGO** (no `localhost:7777` in the NetCode transport).  
Gameplay uses **Unity Relay + UGS Lobby** — same as GCE or cloud Edgegap.

---

## What you should expect

| Step | Server (Docker logs) | Client (Unity Editor) |
|------|------------------------|------------------------|
| 1 | Container starts, IL2CPP boot | — |
| 2 | UGS auth + Relay + lobby published | — |
| 3 | `[MapGeneration] Map generated` | — |
| 4 | — | **Join Game** → select lobby → join |
| 5 | — | Loading / “Syncing map…” |
| 6 | — | **Team picker** → click a team |
| 7 | `[TeamManagementSystem] Spawned ship for networkId=…` | Ship appears, HUD on |

**Your ship is not created on connect.** You must pick a team (RPC to server).

---

## Step-by-step

### A. Start the Docker server (Edgegap plugin)

1. **Tools → Edgegap Hosting**
2. Build + containerize (see main [README.md](./README.md))
3. Section **Test locally** → **Deploy local container**
   - Optional run param: `-p 7777/udp` (metadata only; Relay carries gameplay)
4. Open **Docker Desktop → Containers** → select `edgegap-server-test` → **Logs**

Wait until logs show **both**:

```
[TitanOrbitSessionManager] Dedicated server live. Relay=... Lobby=...
[MapGeneration] Map generated. Size: ...
```

If UGS errors appear (`UGS not ready`, `PrepareDedicatedRelay failed`), the Editor client cannot join — fix Unity project link / internet first.

### B. Connect from Unity Editor (correct path)

1. **Play** in the Editor (normal client build, not Server MPPM)
2. Main menu → **Join game** (not **Local play** / not Edgegap’s “paste IP:port”)
3. **Refresh** — you should see a lobby (often `IsLatest`, game name Titan Orbit)
4. Click the lobby row to **join**
5. Wait for loading (“Syncing map from dedicated server…”)
6. When the **team panel** appears, click **Team A** (or any team)
7. Status should show “Spawning your ship…” then gameplay HUD

**Do not** configure NetCode to connect to `127.0.0.1:7777` for this game — it will not join Relay/lobby and your ship will never spawn.

### C. Optional: auto-pick team in Editor

On `NceGameFlowController` in the scene:

- Enable **Auto Pick Team A In Editor** (dev section)

Useful for quick Docker smoke tests; still requires **Join game** first.

### D. Verify server saw you

Docker logs after you pick a team:

```
[TeamManagementSystem] Spawned ship for networkId=... team=...
```

If join works but no spawn line, the client never sent `RequestTeamCommand` — team UI was skipped or not clicked.

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---------|----------------|-----|
| No lobbies in Join game | Docker server not live / UGS failed | Check container logs; wait for “Dedicated server live” |
| Loading flashes, empty space, no ship | Joined but no team picked | Look for team picker overlay; click a team |
| Stuck “Preparing teams…” | Home planets / TeamState not replicated yet | Wait 5–10s; Refresh join if needed |
| **`Client stuck on pending Relay connection (no NetworkId)`** | Client joined Relay but server never answered NetCode handshake | See **Relay zombie connection** below |
| “Join code not found” | Stale lobby / server restarted | Join game → Refresh; pick newest lobby |
| Planets pop in slowly after load | Normal — remaining asteroid ghosts stream | Playable after team spawn; not all asteroids required |
| Edgegap doc says connect to Docker port | Wrong for Titan Orbit | Use **Join game** only |

### Relay zombie connection (`no NetworkId`)

Unity Console shows:

```
Client connect diag: connections=1 withNetworkId=0 inGame=0 relay=True
Client stuck on pending Relay connection (no NetworkId).
```

This is **not** the harmless `Setting RpcSystem.DynamicAssemblyList to true` line (Unity sample package info).

**Meaning:** the Editor client reached Unity Relay, but the **dedicated server process** on the other end of that join code is not completing the NetCode connection (no `NetworkId` assigned).

**Fix checklist:**

1. **One active server** — stop old Docker containers, GCE VMs, and other local tests. Multiple lobbies often means multiple dead Relay allocations.
2. **Join Latest only** — in Join game, pick the row tagged **Latest** (not “Older”).
3. **Match Relay codes** — Editor log: `Joining Relay lobby=… code=7MB8CW`. Docker log must show `Dedicated server live. Relay=7MB8CW` (same code). If they differ, you joined the wrong lobby.
4. **Docker must show your connect** — while joining, Docker logs should print `Server connections=1 withNetworkId=1`. If Docker shows nothing, the client is not talking to that container.
5. **Server boot OK** — Docker must show `Dedicated server live` before you join. If UGS/Relay errors appear in Docker, fix those first.
6. **Refresh → rejoin** after redeploying the container (new Relay code every boot).

### Docker CLI

```powershell
docker ps
docker logs edgegap-server-test --timestamps
```

### Reset

1. Edgegap plugin → stop/delete local test container  
2. Editor → stop Play  
3. Redeploy container, wait for lobby, Join game → Refresh  

---

## Cloud Edgegap deploy

Same client flow: **Join game** → pick lobby from your live server.  
Do not use deployment IP:external port in the transport.

See [README.md](./README.md) for upload/deploy steps.
