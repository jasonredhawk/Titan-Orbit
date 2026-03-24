# Unity Multiplay Hosting Setup (Option A: Relay+Lobby discovery)

This guide assumes:
- WebGL runs on Cloudflare Pages and joins matches via your existing **UGS Lobby + Unity Relay** flow.
- Your headless Linux server process runs your match server logic and creates:
  - one Relay allocation per match
  - one UGS Lobby per match (with `IsOpen`, `IsLatest`, `CreatedAtEpoch`, and member-only `RelayJoinCode`)

Your match server logic lives in:
- `Assets/Scripts/Networking/DedicatedMatchServerBootstrap.cs`

## 1) Create/configure Multiplay Hosting
1. In the Unity Dashboard, enable **Multiplay Hosting** under Gaming Services > Multiplayer.
2. Create a **Build** for your dedicated server executable:
   - Target: `Linux`
   - Build config: headless Linux.
3. Create a **Fleet** for the region(s) you want to run in.
4. Create a **Server Build Configuration** (or equivalent) for your server:
   - Ensure the server query protocol is configured (Multiplay uses SQP).

## 2) Deploy the Linux headless server build
1. Build your server for `StandaloneLinux64` (headless).
2. Upload that Linux executable to the Multiplay Hosting build configuration.

## 3) Configure launch parameters
Multiplay supports command-line launch parameters. The idea is to pass values required by your bootstrap:

- `--maxPlayers`
- `--serverPort`
- `--relayProtocol=wss`
- `--isLatest` (true for the first match instance)

Example concept (exact syntax depends on your Multiplay UI):
- `--maxPlayers=60`
- `--serverPort=$$port$$` (or hardcode 7777 if your build expects the same port)
- `--relayProtocol=wss`
- `--isLatest=1`

Multiplay-injected variables are documented as launch parameters variables (see Unity “Launch parameters” docs).

## 4) Initial “latest” instance trigger
Because Option A uses the server process to create lobbies, you need to start at least one server instance at launch so it can create:
- the first “latest” lobby (`IsLatest=1`)
- and start listening via Relay.

Typical approach:
1. Create a Multiplay fleet allocation at startup (manual test first).
2. Ensure that initial instance gets `--isLatest=1`.

## 5) Rotation behavior integration (important)
Your current prototype rotates by spawning child processes (`Process.Start`) inside the match server.

For Multiplay Hosting, the recommended rotation pattern is:
- when a match becomes “not latest” and you need a new match, request another server allocation from Multiplay/Matchmaker

This requires code changes covered in:
- `rotation-to-allocation` and `multiplay-lifecycle-hook`

## 6) WebGL join verification
After you have at least one allocated server instance running and it has created a lobby:
1. Deploy your WebGL build to Cloudflare Pages.
2. Free users should query open lobbies filtered by `IsLatest=1`.
3. WebGL client should join lobby by id using:
   - `NetworkGameManager.PlayWebGLJoinByLobbyIdAsync(...)`

If join fails:
- verify CSP allows `wss:` (your repo has `Assets/CloudflarePages/_headers`)
- check browser console/network errors
- check server logs for Unity Services auth / lobby creation / relay allocation errors

