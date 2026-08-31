# Test Edgegap local Docker container (Titan Orbit)

Edgegap’s **Test Your Server Locally** runs your Linux headless server in Docker on your PC.  
Titan Orbit clients join via **UGS Lobby**, then connect with **direct UDP** to the advertised `Host=` (not Unity Relay).

For local Docker the lobby must publish **`127.0.0.1:7777`**. The Edgegap plugin injects a dummy `ARBITRIUM_PUBLIC_IP=162.254.141.66` — ignore that; our boot remaps it.

---

## What you should expect

| Step | Server (Docker logs) | Client (Unity Editor) |
|------|------------------------|------------------------|
| 1 | Container starts, IL2CPP boot | — |
| 2 | UGS auth + lobby published (`Host=127.0.0.1:7777`) | — |
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
   - Run param **required**: `-p 7777:7777/udp` (Editor UDP-connects to localhost)
4. Open **Docker Desktop → Containers** → select `edgegap-server-test` → **Logs**

Wait until logs show **both**:

```
[TitanOrbitSessionManager] Dedicated server live. Host=127.0.0.1:7777 Lobby=...
[MapGeneration] Map generated. Size: ...
```

If Host is `162.254.141.66:31504`, this binary is old — rebuild **Headless Server (Linux — Edgegap)** and re-containerize.

If UGS errors appear (`UGS not ready`, `PrepareDedicatedHost failed`), the Editor client cannot join — fix Unity project link / internet first.

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
| **`Client stuck on pending dedicated connection (no NetworkId)`** `host=162.254.141.66:31504` | Dummy Edgegap plugin IP — UDP never reached Docker | Rebuild Linux Edgegap server; confirm logs say `Host=127.0.0.1:7777`; docker run `-p 7777:7777/udp` |
| **`Client stuck on pending dedicated connection`** `host=127.0.0.1:7777` | UDP 7777 not published or Windows firewall | Redeploy with `-p 7777:7777/udp`; Docker logs must show `Server connections=1` |
| Container CPU ~100% then exits | Same GCE BusyWait + struggling-recycle (fixed: Edgegap no longer quits) | Rebuild; 1 core BusyWait is expected; `wallSim` should stay listed |
| Join Game **Server sim: ~11 Hz wall** / ships snap back | Docker used `-nographics` + `SDL_VIDEODRIVER=dummy` (NullGfx PresentAndWait ~300 ms/frame). Relay never paced ticks. | Recreate container with current `start-server.sh` (no those flags). Logs: `wallSim≈55–60Hz`. No Unity rebuild. |
| Planets pop in slowly after load | Normal — remaining asteroid ghosts stream | Playable after team spawn; not all asteroids required |
| Edgegap doc says connect to Docker port | Wrong for Titan Orbit | Use **Join game** only |

### Pending dedicated connection (`no NetworkId`)

Unity Console shows:

```
Client stuck on pending dedicated connection (no NetworkId). host=127.0.0.1:7777
```

**Meaning:** the Editor reached UGS Lobby, then UDP-connect to `Host=`. The container never completed the NetCode handshake (`NetworkId`).

**Fix checklist:**

1. **Stop GCE / other Docker servers** so you do not join the wrong `IsLatest` lobby.
2. **Join Latest** — pick the Docker lobby; confirm Editor `host=` matches Docker `Dedicated server live. Host=`.
3. **UDP published** — `docker ps` must show `0.0.0.0:7777->7777/udp`.
4. **Docker must show your connect** — `Server connections=1 withNetworkId=1`. If still 0, packets never arrived.
5. **Rebuild** if Host is still `162.254.141.66:31504`.

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
