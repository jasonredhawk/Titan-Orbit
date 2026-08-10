# Join architecture verification (Windows client)

## Target model (2026-08 seed-hydrate)

Late-join must **not** Instantiates hundreds of asteroid ghosts. Clients build asteroids from the match seed (`MapSessionMetaRpc` + `ClientMapHydrateSystem`). Ghost relevancy streams Ship / Gem / PeopleTransport / Planet only.

Progress bar tracks: recipe → local asteroid hydrate → GoInGame → short dynamic catch-up → Join Team.

`Crash!!!` in Player.log is still a native fault if someone reintroduces a full-map GhostSpawn Instantiates flood with Transform storms. Prevention = **seed hydrate + dynamic relevancy**, not Instantiates=1 forever.

Paired rules: `.cursor/rules/titan-orbit-windows-join-crash.mdc`, `titan-orbit-teamchoice-crash-hardstop.mdc` (updated for seed-hydrate).

---

## 0. Static gate scan (run before every Windows client build)

From repo root:

```powershell
powershell -File tools/verify-join-crash-gates.ps1
```

- Exit `0` = no high-severity regressions.
- Exit `1` = **do not build / ship** until fixed.
- This does **not** replace a Windows Relay smoke test.

---

## 1. GhostSpawn patch id in the built client

DLL/log must contain `TO_GhostSpawn_v16_requeueFailedInstantiate` (or newer).

Instantiates budget may be **> 1** (e.g. 16) because asteroids are no longer Instantiates from snapshots. Do **not** stream asteroids as relevant ghosts again.

---

## 2. Player.log success path (Windows Relay late-join)

1. `[MapSessionMeta] Client latched recipe seed=…`
2. `[ClientMapHydrate] Blueprint ready` / `Asteroid hydrate complete`
3. `[TitanOrbitGoInGame] Client sending GoInGameRequest` **after** hydrate
4. `[TitanOrbitGhostRelevancy] SetIsRelevant` on server (asteroids excluded)
5. Loading bar climbs via hydrate counts (not stuck forever at soft-crawl)
6. Join Team → TeamChoiceResult → ship visible
7. **No `Crash!!!`**
8. TransformSystemGroup may log ENABLED (seed-hydrate model) — that is expected

Manual: dense map (400+ asteroids) → bar progresses during hydrate → Join Team → hybrid/local ship visible → process stays alive.

---

## 3. Required APIs

```csharp
// Ships — still skip during GhostSpawnBacklog / TeamChoice holds
if (ClientJoinSettleCache.ShouldSkipShipEntityQueries) return;

// Map bodies — prefer ShouldSkipMapBodyQueries; TransformQuarantine is no longer session-long
if (ClientJoinSettleCache.ShouldSkipMapBodyQueries) return;
```

Asteroids on the client are `ClientSeedHydratedMapBody` locals — do not assume they are ghosts.

---

## 4. Forbidden regressions

1. Making asteroids relevant again under `GhostRelevancyMode.SetIsRelevant` without seed hydrate (reintroduces Instantiates flood).
2. Completing loading / Join Team with **zero** hydrated asteroids when `HasFullRecipe` is true.
3. Entering `NetworkStreamInGame` before hydrate when a full recipe was received.
4. Permanent spawn-wait deadlock gates on `TeamChoiceConfirmed && !HasOwnedShipSeed`.
