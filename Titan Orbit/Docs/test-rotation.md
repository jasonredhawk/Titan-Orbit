# End-to-end rotation tests (WebGL + headless server)

These tests verify:
- free users join only the newest `IsLatest` lobby
- a new match is spawned when the current latest match reaches the rotation age threshold
- a new match is spawned when a lobby reaches `maxPlayers` (full) and the old lobby is closed

## 0) Build

1. WebGL production build: `TitanOrbit/Build/WebGL Production`
2. Headless server build: `TitanOrbit/Build/Headless Server (Windows)`

## 1) Start server with a short rotation threshold

Run the server executable in a shell on your server host (or locally for testing):

```bash
TitanOrbitServer.exe -batchmode -nographics ^
  --maxPlayers=4 ^
  --serverPort=7777 ^
  --relayProtocol=wss ^
  --isLatest=1 ^
  --ageThresholdSeconds=30
```

`--ageThresholdSeconds` is an optional debug/testing override. It defaults to 20 minutes in production.

Wait for the logs to show:
- `[DedicatedMatchServerBootstrap] Starting server for lobby ... (isLatest=...)`

## 2) Verify free routing joins the latest

1. Open the WebGL site in a browser (after deploying your WebGL build to something that works with the Cloudflare `_headers` CSP).
2. Set `MainMenu.paidPlaceholder` to `false` (free flow) for this test build.
3. Click `Play`.
4. Confirm the lobby you joined is the most recent (in practice: watch `roomNameText` and/or check server logs for the lobby id).

Then:
1. Wait ~35 seconds.
2. Click `Play` again (fresh session) as free.
3. Confirm you joined a new lobby (server should have logged a 20min rotation equivalent using your short `ageThresholdSeconds`).

Expected server behavior:
- old lobby gets updated to `IsLatest=0`
- new lobby gets created with `IsLatest=1`

## 3) Verify “full” rotation closes a lobby and spawns next

With `--maxPlayers=4`, you need 3 additional browser clients beyond the lobby host member for the lobby to be “full”.

1. Open 3 separate browser tabs (or different browsers / incognito windows) and click `Play` each time as free.
2. You should see the server log indicate the lobby reached max players and performed “Lobby full rotation”.
3. Click `Play` again as free and confirm you join a new lobby.

Expected server behavior:
- closed lobby gets updated to `IsOpen=0` and `IsLatest=0`
- a new match process spawns immediately

## 4) Verify paid selection can join non-latest open lobbies

1. Create a separate WebGL build where `MainMenu.paidPlaceholder` is `true` (or update `PlayerPrefs` key `TitanOrbit_WebPaid` if you have a way to set it).
2. Click `Play` as paid. The UI will show a short “Open lobbies (paid)” list in `joinCodeDisplayText`.
3. Use `joinCodeInputField` to enter:
   - an index shown in the list (e.g., `0`), or
   - a lobby id
4. Confirm you can join a lobby that is not `IsLatest=1`.

## Troubleshooting

If WebGL can’t connect:
- Verify you’re using `wss` end-to-end (Relay protocol + Cloudflare CSP `connect-src ... wss:`).
- Check browser console for CSP or WebSocket failures.
- Check server logs for Unity Services auth / lobby creation / relay allocation errors.

