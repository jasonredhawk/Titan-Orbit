# Join crash verification (Windows client)

`Crash!!!` in Player.log is a **native** fault (Burst / UnityPlayer). C# try/catch cannot catch it. Soft loading UI does not prevent it. Prevention = gates + Instantiates=1 + CreateEntity-all placeholders + hybrid GO proxies.

Paired: `.cursor/rules/titan-orbit-teamchoice-crash-hardstop.mdc`, `titan-orbit-windows-join-crash.mdc`.

---

## 0. Static gate scan (run before every Windows client build)

From repo root:

```powershell
powershell -File tools/verify-join-crash-gates.ps1
```

- Exit `0` = no high-severity regressions in client gather gates.
- Exit `1` = **do not build / ship** until fixed.
- This does **not** replace a Windows Relay smoke test.

---

## 1. GhostSpawn patch id in the built client

DLL/log must contain a **v13+** marker (map-body Instantiates visual hook), e.g. `TO_GhostSpawn_v13_mapBodyVisualHook`.

```powershell
$dll = "BuildOutput/Client/windows/TitanOrbit_Data/Managed/Unity.NetCode.dll"
$nc = [System.IO.File]::ReadAllText($dll)
"v13+=$($nc -match 'TO_GhostSpawn_v1[3-9]|TO_GhostSpawn_v[2-9]\d')"
```

Must print `v13+=True`.

**Forbidden (regression):** CreateEntity-cap+requeue leftovers, Instantiates > 1/frame, missing Instantiates→`MapBodyHybridVisualSpawnRequest` hook.

---

## 2. Player.log success path (Windows Relay late-join)

1. `[JoinSettle] Settling ON` (or equivalent) during map Instantiates
2. Instantiates + Pending/SpawnRequest drain; loading bar climbs via `MapLoadingProxyCount` / meta N (**not stuck 0/N**)
3. `Settling OFF` then proxies ≈ N → Join Team available
4. After Join Team / `TeamChoiceResult`: ship Instantiates under `GhostSpawnBacklog` / `ShouldSkipShipEntityQueries` — **no Crash!!!**
5. **No** `TransformSystemGroup RE-ENABLED`
6. **No** `Crash!!!`

Manual: dense map (400+ asteroids) → bar progresses → Join Team → hybrid ship visible → process stays alive.

---

## 3. Required gate APIs (do not invent weaker ones)

```csharp
// Ships — Settling OR GhostSpawnBacklog OR post–TeamChoice hold
if (ClientJoinSettleCache.ShouldSkipShipEntityQueries) return;

// Map bodies — TransformQuarantine OR Settling (quarantine is session-long on Windows)
if (ClientJoinSettleCache.ShouldSkipMapBodyQueries) return;
```

`Settling` alone is **not** safe after Join Team.
