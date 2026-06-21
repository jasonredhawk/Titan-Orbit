using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Generation;
using TitanOrbit.Systems;

namespace TitanOrbit.Entities
{
    public sealed class ClientMineVisual : MonoBehaviour
    {
        private static Transform s_pool;
        private static readonly Dictionary<uint, ClientMineVisual> s_bySequence = new Dictionary<uint, ClientMineVisual>(32);
        private uint sequence;

        public static GameObject Spawn(MineSpawnPayload payload)
        {
            EnsurePool();
            var go = new GameObject("ClientMineVisual");
            go.transform.SetParent(s_pool, false);
            var visual = go.AddComponent<ClientMineVisual>();
            visual.sequence = payload.Sequence;
            if (payload.Sequence != 0)
                s_bySequence[payload.Sequence] = visual;

            Vector3 pos = payload.Position;
            pos.y = 0f;
            go.transform.position = pos;
            go.transform.localScale = Vector3.one * (payload.IsLargeFlag != 0 ? 1.2f : 0.8f);

            var mesh = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            mesh.transform.SetParent(go.transform, false);
            mesh.transform.localScale = new Vector3(1f, 0.15f, 1f);
            var col = mesh.GetComponent<Collider>();
            if (col != null) Destroy(col);
            return go;
        }

        public static void DespawnBySequence(uint seq)
        {
            if (seq == 0) return;
            if (s_bySequence.TryGetValue(seq, out ClientMineVisual v) && v != null)
                Destroy(v.gameObject);
        }

        private void OnDestroy()
        {
            if (sequence != 0)
                s_bySequence.Remove(sequence);
        }

        private static void EnsurePool()
        {
            if (s_pool != null) return;
            var poolGo = new GameObject("ClientMineVisuals");
            Object.DontDestroyOnLoad(poolGo);
            s_pool = poolGo.transform;
        }

        private void LateUpdate()
        {
            var cam = UnityEngine.Camera.main;
            if (cam == null) return;
            Vector3 logical = transform.position;
            logical.y = 0f;
            transform.position = ToroidalMap.GetDisplayPosition(logical, cam.transform.position);
        }
    }
}
