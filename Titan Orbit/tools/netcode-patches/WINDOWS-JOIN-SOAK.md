# Windows Relay late-join soak (seed-hydrate)

Run after client + headless rebuild/redeploy.

## Setup

1. Dense map (400+ asteroids) on dedicated Linux server.
2. As many live ships as practical (target ~100).
3. Windows player build with patched NetCode (`TO_GhostSpawn_v16_*`).

## Steps

1. Late-join via Relay.
2. Confirm loading overlay while recipe + asteroid hydrate run (bar should move).
3. Wait for Join Team.
4. Pick a team; confirm ship appears.
5. Mine an asteroid; confirm HP / destroy / respawn still work.
6. Leave and rejoin once (statics reset).

## Player.log must show

- `[MapSessionMeta] Client latched recipe seed=… full=1`
- `[ClientMapHydrate] Blueprint ready` / `Asteroid hydrate complete`
- `[TitanOrbitGoInGame] Client sending GoInGameRequest` after hydrate
- `[TitanOrbitGhostRelevancy] SetIsRelevant` (server)
- `[JoinSettle] TransformSystemGroup ENABLED` (seed-hydrate — expected)
- **No `Crash!!!`**

## Fail if

- Bar stuck with no hydrate logs
- GoInGame before hydrate complete
- Asteroids never appear
- Crash!!! on Join Team
