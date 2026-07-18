# Join crash verification (Windows client)

## Build check

After **TitanOrbit → Build → Windows Client**:

```powershell
$nc = [System.IO.File]::ReadAllText("BuildOutput/Client/windows/TitanOrbit_Data/Managed/Unity.NetCode.dll")
"v9=$($nc.Contains('TO_GhostSpawn_v9_transformsAlwaysOn'))"
```

Must print `v9=True`.

## Player.log success path

1. `[JoinSettle] Settling ON (hybrid/UI gate; TransformSystemGroup stays enabled).`
2. `[MapSessionMeta] Client latched totals…`
3. Optional: `[TO_GhostSpawn] Created N placeholders this frame…`
4. `[JoinSettle] Settling OFF (Instantiates backlog idle).`
5. **No** `Crash!!!`
6. **No** `TransformSystemGroup RE-ENABLED` / `DISABLED` (those messages are obsolete and bad)

## Failure signatures (regressions)

| Log | Meaning |
|-----|---------|
| `TransformSystemGroup RE-ENABLED` then `Crash!!!` | Someone reintroduced LTW disable during settle |
| `MarkFromQuery` / `DrawAsteroids` / `MinimapEcsEntitySync` + `Crash!!!` | Map-body `ToEntityArray` during settle |
| `baseline for a ghost we do not have` | Placeholder defer without ghost-map registration |
