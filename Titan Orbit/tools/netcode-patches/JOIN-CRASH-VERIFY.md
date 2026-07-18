# Join crash verification (Windows client)

## Build check

```powershell
$nc = [System.IO.File]::ReadAllText("BuildOutput/Client/windows/TitanOrbit_Data/Managed/Unity.NetCode.dll")
"v10=$($nc.Contains('TO_GhostSpawn_v10_placeholderCap1'))"
```

Must print `v10=True`.

## Player.log success path

1. `[JoinSettle] Settling ON (hybrid/UI gate; TransformSystemGroup stays enabled).`
2. `[MapSessionMeta] Client latched totals…`
3. `[TO_GhostSpawn] Placeholder cap: created 1/frame, re-queued N…` (throttled log)
4. `[JoinSettle] Settling OFF…`
5. **No** `Crash!!!`

## About "silent" crashes

`Crash!!!` is a native fault in Burst/`UnityPlayer`. It cannot be turned into a managed exception or soft disconnect after the fact. Prevention only (caps + settle gates + server tile size).
