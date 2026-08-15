# Join world-ready (live dedicated-server match)

Late-join must see the **current** match, not t=0 layout. Loading and Join Team both read
`JoinWorldReadyCache.IsComplete`.

## Per-element path

| Element | How it arrives | Ready when |
|---|---|---|
| Asteroids | Local seed Instantiates (`ClientMapHydrateSystem`, 24/frame) + `AsteroidOccupancyRpc` SoftDestroy of dead slots. **Not** ghosts. | Hydrate complete + occupancy applied (or 8s timeout) |
| Planets | Always-relevant ghosts. GhostSpawn Instantiates the prefab. | Planet ghosts ≥ 92% of `LivePlanetCount` |
| Moons | Not separate ghosts. `PlanetGemMoonState` on the planet + `PlanetGemMoonVisualProxy` / colliders | Moon proxies ≥ 92% of expected planets |
| Ships | Always-relevant ghosts. Instantiates at GhostSpawn budget (16/frame patch) | Ship ghosts ≥ 92% of `LiveShipCount`; 0 ships is ready |
| Gems | Interpolated ghosts. Nearby 40u (spatial hash) + **tractor pin** (stay relevant while this connection's ship is locking). Join window: near any live ship. GhostSpawn Instantiates that subset (16/frame). **Never all-map** and never skip Instantiates of relevant gems. | Not a loading gate |
| People transports | Server `CreateEntity` + SpawnRpc / PoseRpc. **Not** ghosts. Late join: catch-up SpawnRpc + pose-from-unknown Active | Catch-up sent; VFX Instantiates from RPC or pose |
| GhostCount | Unity `GhostCountReceivedOnClient` + `GhostCountInstantiatedOnClient` vs relevancy-filtered server count | Both ≥ 85%, or 20s timeout, or planets+ships+moons+proxy |

## Player.log success path

1. `[MapSessionMeta] Client latched recipe seed=…`
2. `[ClientMapHydrate] Asteroid hydrate complete`
3. `[AsteroidOccupancy] Applied dead=… / slots=…`
4. `[TitanOrbitGoInGame] Client sending GoInGameRequest`
5. `[JoinWorldReady] complete=true` (planets, moons, ships, occupancy, InGame)
6. Loading overlay hides → Join Team
7. In-flight people transports Instantiates from catch-up SpawnRpc / pose
8. Process stays alive (no native `Crash!!!`)

## Manual soak

Join a **busy** dedicated match (rocks already gone, transports flying, several ships).
Loading stays until occupancy + planet/ship GhostCount catch-up, then Join Team.

Optional diagnostic (not a ship veto):

```powershell
powershell -File tools/verify-join-crash-gates.ps1
```

Do **not** put `AsteroidTag` in `GhostRelevancy` SetIsRelevant. Do **not** GoInGame before
seed-hydrate when `HasFullRecipe`. Skip `ToEntityArray` only while that archetype’s
GhostSpawn Instantiates is in flight (`ShouldSkipShipEntityQueries`).

Gems Instantiates in the 40u band + tractor pin is required. Do not skip gem Instantiates
and do not stream every gem on the torus.
