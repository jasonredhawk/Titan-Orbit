# NetCode GhostSpawnSystem patch (Titan Orbit)

## Problem

On dedicated Relay late-join with hundreds of map ghosts, the Windows player hard-crashed from Instantiates floods (Burst LocalToWorld) and from map-body `ToEntityArray` during Instantiates.

## Durable location

Embedded package (git-tracked via `file:com.unity.netcode`):

`Packages/com.unity.netcode/Runtime/Snapshot/GhostSpawnSystem.cs`

Canonical copy used by the Editor menu / pre-build guard:

`tools/netcode-patches/GhostSpawnSystem.cs`

## Current patch (`TO_GhostSpawn_v10_placeholderCap1`)

1. **Safe snapshot copy** — rebuild buffers instead of resize-in-place (`TryCopySnapshotBufferSafe`).
2. **No Burst on `OnUpdate`** — managed Instantiates.
3. **1 Instantiates/frame** from delayed queues.
4. **1 CreateEntity placeholder/frame** — requeue leftovers via `RequeueDeferredGhostSpawns`; each CreateEntity is still registered in `SpawnedGhostEntityMap` (v7 forgot that).
5. **Predicted join path** — delayed Instantiates via placeholders.
6. **Never disable TransformSystemGroup** for join (disable→re-enable = LTW flood Crash!!!).
7. **Server companion:** distance `TileSize` must be ≪ map size (see `TitanOrbitGhostDistanceImportanceBootstrapSystem`).

## Companion project systems (not in this file)

- Server: `TitanOrbitGhostSendTuneSystem` + `TitanOrbitGhostDistanceImportanceSystem` (stream map ghosts).
- Client: `TitanOrbitClientJoinTransformGateSystem` + `ClientJoinSettleState` (**Settling flag only** — hybrid/UI gate; TransformSystemGroup stays enabled).
- Hybrid: `EcsWorldVisualizer` rate-limits GO Instantiates while `ClientJoinSettleCache.Settling`.

## Re-apply / verify

Unity menu: **Titan Orbit → NetCode → Re-apply GhostSpawnSystem patch**

After Windows client build, `Unity.NetCode.dll` must contain:

`TO_GhostSpawn_v10_placeholderCap1`
