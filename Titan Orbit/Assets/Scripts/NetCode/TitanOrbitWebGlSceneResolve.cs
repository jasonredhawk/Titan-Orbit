using System;
using System.Reflection;
using TitanOrbit.Diagnostics;
using Unity.Collections;
using Unity.Entities;
using Unity.Scenes;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// WebGL-safe SubScene header recipe. Stock
    /// <c>SceneHeaderUtility.ScheduleHeaderLoadOnEntity</c> does
    /// <c>AddComponentData&lt;RequestSceneHeader&gt;</c> on an existing
    /// <see cref="SceneReference"/> entity. <c>RequestSceneHeader</c> is
    /// <c>ICleanupComponentData</c> — the same WASM-OOB class as
    /// <see cref="TitanOrbitWebGlClientConnect"/>.
    /// One <see cref="ComponentTypeSet"/> for the cleanup + hash, then the stock
    /// schedule call (now a set, not an add).
    /// </summary>
    public static class TitanOrbitWebGlSceneResolve
    {
        static Type s_RequestSceneHeaderType;
        static Type s_ResolvedSceneHashType;
        static Action<EntityManager, Entity, Hash128, RequestSceneLoaded, Hash128, string> s_Schedule;
        static bool s_ResolveFailed;

        /// <summary>
        /// Adds the header cleanup recipe on every <see cref="SceneReference"/> that
        /// still needs a header, then invokes Unity's schedule so FinishHeaderLoad
        /// can complete. Safe to call every tick.
        /// </summary>
        public static void EnsureHeaderRecipe(World world)
        {
            if (s_ResolveFailed || world == null || !world.IsCreated)
                return;

            if (!TryBind(out string bindError))
            {
                s_ResolveFailed = true;
                // #region agent log
                WebGlBootDebugProbe.Emit("H-SR", "TitanOrbitWebGlSceneResolve.EnsureHeaderRecipe",
                    "bind-failed", "{\"error\":\"" + Escape(bindError) + "\"}");
                // #endregion
                return;
            }

            var em = world.EntityManager;
            var headerCt = new ComponentType(s_RequestSceneHeaderType);
            var hashCt = new ComponentType(s_ResolvedSceneHashType);
            int seeded = 0;
            int already = 0;
            using (var q = em.CreateEntityQuery(
                       ComponentType.ReadOnly<SceneReference>(),
                       ComponentType.ReadOnly<RequestSceneLoaded>(),
                       ComponentType.Exclude<ResolvedSectionEntity>(),
                       ComponentType.Exclude<DisableSceneResolveAndLoad>()))
            {
                using NativeArray<Entity> entities = q.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity sceneEntity = entities[i];
                    if (em.HasComponent(sceneEntity, headerCt))
                    {
                        already++;
                        continue;
                    }

                    // #region agent log
                    WebGlBootDebugProbe.Emit("H-SR", "TitanOrbitWebGlSceneResolve.EnsureHeaderRecipe",
                        "typeset-before", "{\"index\":" + sceneEntity.Index + "}");
                    // #endregion
                    em.AddComponent(sceneEntity, new ComponentTypeSet(new[] { headerCt, hashCt }));
                    var scene = em.GetComponentData<SceneReference>(sceneEntity);
                    var request = em.GetComponentData<RequestSceneLoaded>(sceneEntity);
                    s_Schedule(em, sceneEntity, scene.SceneGUID, request, default, Application.streamingAssetsPath);
                    seeded++;
                    // #region agent log
                    WebGlBootDebugProbe.Emit("H-SR", "TitanOrbitWebGlSceneResolve.EnsureHeaderRecipe",
                        "typeset-after",
                        "{\"index\":" + sceneEntity.Index +
                        ",\"hasHeader\":" + (em.HasComponent(sceneEntity, headerCt) ? "true" : "false") +
                        ",\"hasHash\":" + (em.HasComponent(sceneEntity, hashCt) ? "true" : "false") + "}");
                    // #endregion
                }
            }

            if (seeded > 0)
            {
                // #region agent log
                WebGlBootDebugProbe.Emit("H-SR", "TitanOrbitWebGlSceneResolve.EnsureHeaderRecipe",
                    "seeded", "{\"seeded\":" + seeded + ",\"already\":" + already + "}");
                // #endregion
            }
        }

        static bool TryBind(out string error)
        {
            error = null;
            if (s_Schedule != null)
                return true;

            var scenesAsm = typeof(SceneSystem).Assembly;
            s_RequestSceneHeaderType = scenesAsm.GetType("Unity.Scenes.RequestSceneHeader");
            s_ResolvedSceneHashType = scenesAsm.GetType("Unity.Scenes.ResolvedSceneHash");
            Type utilType = scenesAsm.GetType("Unity.Scenes.SceneHeaderUtility");
            if (s_RequestSceneHeaderType == null || s_ResolvedSceneHashType == null || utilType == null)
            {
                error = "missing-scene-types";
                return false;
            }

            MethodInfo method = utilType.GetMethod(
                "ScheduleHeaderLoadOnEntity",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[]
                {
                    typeof(EntityManager),
                    typeof(Entity),
                    typeof(Hash128),
                    typeof(RequestSceneLoaded),
                    typeof(Hash128),
                    typeof(string)
                },
                null);
            if (method == null)
            {
                error = "missing-schedule-method";
                return false;
            }

            s_Schedule = (Action<EntityManager, Entity, Hash128, RequestSceneLoaded, Hash128, string>)
                Delegate.CreateDelegate(
                    typeof(Action<EntityManager, Entity, Hash128, RequestSceneLoaded, Hash128, string>),
                    method);
            return s_Schedule != null;
        }

        static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
