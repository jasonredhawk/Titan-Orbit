# Windows late-join crash — verify after rebuild

Burst job count (e.g. 71 vs 47) is **not** a crash signal. Embedding NetCode adds Burst jobs. Judge success by DLL markers + Player.log sequence.

## Rebuild both

| Build | Why |
|-------|-----|
| **Linux headless server** | MaxSendRate on map ghosts, distance importance, GhostSend tune |
| **Windows client** | GhostSpawn v7, JoinSettle, hybrid Instantiates rate limit |

## DLL checks (PowerShell)

```powershell
$root = "…\BuildOutput\Client\windows\TitanOrbit_Data\Managed"
$nc = [Text.Encoding]::Unicode.GetString([IO.File]::ReadAllBytes("$root\Unity.NetCode.dll"))
$ecs = [Text.Encoding]::Unicode.GetString([IO.File]::ReadAllBytes("$root\TitanOrbit.ECS.dll"))
"v7=$($nc.Contains('TO_GhostSpawn_v7_joinStream'))"
"Sanitize=$($ecs.Contains('TransformSanitize'))"   # must be False
"Settle=$($ecs.Contains('ClientJoinSettle'))"      # must be True
```

## Player.log success sequence

`%LocalLow%\DefaultCompany\Titan Orbit\Player.log`

1. `[JoinSettle] TransformSystemGroup DISABLED (backlog-gated).`
2. `[MapSessionMeta] Client latched totals … asteroids=…`
3. Optional: `[TO_GhostSpawn] Re-queued N spawn entries…` (N ≥ 8)
4. `[JoinSettle] TransformSystemGroup RE-ENABLED after join settle.`
5. **No** `Crash!!!` between 1 and 4.

## If it still crashes

Note the **exact** stack (Burst hash vs UnityPlayer) and whether step 1 appeared before the crash. That tells us whether settle/GhostSpawn made it into the binary.
