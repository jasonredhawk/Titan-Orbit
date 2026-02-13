using UnityEngine;
using TitanOrbit.Generation;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Handles toroidal rendering - renders entities at multiple positions when near map boundaries
    /// so they appear seamlessly when wrapping around the map edges.
    /// Uses Graphics.DrawMesh to render duplicates without creating GameObjects.
    /// </summary>
    [RequireComponent(typeof(Renderer))]
    public class ToroidalRenderer : MonoBehaviour
    {
        private Mesh mesh;
        private Material[] materials;
        private Renderer entityRenderer;
        private bool isInitialized = false;

        private void Awake()
        {
            entityRenderer = GetComponent<Renderer>();
        }

        private void Start()
        {
            Initialize();
        }

        private void Initialize()
        {
            if (isInitialized) return;

            // Get mesh from MeshFilter
            MeshFilter meshFilter = GetComponent<MeshFilter>();
            if (meshFilter != null && meshFilter.sharedMesh != null)
            {
                mesh = meshFilter.sharedMesh;
            }
            else
            {
                // Try to get mesh from renderer bounds (fallback)
                Debug.LogWarning($"ToroidalRenderer on {gameObject.name} has no MeshFilter. Toroidal rendering may not work correctly.");
                return;
            }

            // Get materials from renderer
            if (entityRenderer != null)
            {
                materials = entityRenderer.sharedMaterials;
            }

            isInitialized = (mesh != null && materials != null && materials.Length > 0);
        }

        private void OnRenderObject()
        {
            if (!isInitialized)
            {
                Initialize();
                if (!isInitialized) return;
            }

            if (UnityEngine.Camera.current == null) return;
            if (mesh == null || materials == null || materials.Length == 0) return;

            Vector3 entityPos = transform.position;
            float mapWidth = ToroidalMap.GetMapWidth();
            float mapHeight = ToroidalMap.GetMapHeight();
            Matrix4x4 baseMatrix = transform.localToWorldMatrix;

            // Toroidal fix: Always render this entity at all 8 wrapped positions.
            // The main GameObject already renders at entityPos. We draw 8 copies at offsets
            // so that no matter where the camera is (including near edges), at least one
            // copy is in view. Camera frustum culling hides the rest. This eliminates
            // any disappearing: when you're near the right edge you see the left-edge
            // content because we draw it again at (pos.x + mapWidth, ...).

            DrawMeshAtMatrix(OffsetMatrix(baseMatrix, entityPos, mapWidth, 0f));
            DrawMeshAtMatrix(OffsetMatrix(baseMatrix, entityPos, -mapWidth, 0f));
            DrawMeshAtMatrix(OffsetMatrix(baseMatrix, entityPos, 0f, mapHeight));
            DrawMeshAtMatrix(OffsetMatrix(baseMatrix, entityPos, 0f, -mapHeight));
            DrawMeshAtMatrix(OffsetMatrix(baseMatrix, entityPos, mapWidth, mapHeight));
            DrawMeshAtMatrix(OffsetMatrix(baseMatrix, entityPos, mapWidth, -mapHeight));
            DrawMeshAtMatrix(OffsetMatrix(baseMatrix, entityPos, -mapWidth, mapHeight));
            DrawMeshAtMatrix(OffsetMatrix(baseMatrix, entityPos, -mapWidth, -mapHeight));
        }

        private static Matrix4x4 OffsetMatrix(Matrix4x4 baseMatrix, Vector3 entityPos, float offsetX, float offsetZ)
        {
            Matrix4x4 m = baseMatrix;
            m.SetColumn(3, new Vector4(entityPos.x + offsetX, entityPos.y, entityPos.z + offsetZ, 1f));
            return m;
        }

        private void DrawMeshAtMatrix(Matrix4x4 matrix)
        {
            if (mesh == null || materials == null) return;

            // Draw main mesh
            for (int i = 0; i < materials.Length; i++)
            {
                Material mat = materials[i];
                if (mat != null)
                {
                    Graphics.DrawMesh(mesh, matrix, mat, gameObject.layer, UnityEngine.Camera.current, i);
                }
            }

            // Draw child meshes (like planet rings)
            DrawChildMeshes(transform, matrix);
        }

        private void DrawChildMeshes(Transform parent, Matrix4x4 parentMatrix)
        {
            foreach (Transform child in parent)
            {
                Renderer childRenderer = child.GetComponent<Renderer>();
                MeshFilter childMeshFilter = child.GetComponent<MeshFilter>();
                
                if (childRenderer != null && childMeshFilter != null && childMeshFilter.sharedMesh != null)
                {
                    // Calculate child's world matrix relative to parent
                    Matrix4x4 childLocalMatrix = Matrix4x4.TRS(child.localPosition, child.localRotation, child.localScale);
                    Matrix4x4 childWorldMatrix = parentMatrix * childLocalMatrix;
                    
                    Material[] childMaterials = childRenderer.sharedMaterials;
                    Mesh childMesh = childMeshFilter.sharedMesh;
                    
                    for (int i = 0; i < childMaterials.Length; i++)
                    {
                        Material mat = childMaterials[i];
                        if (mat != null)
                        {
                            Graphics.DrawMesh(childMesh, childWorldMatrix, mat, child.gameObject.layer, UnityEngine.Camera.current, i);
                        }
                    }
                }
                
                // Recursively draw grandchildren
                if (child.childCount > 0)
                {
                    Matrix4x4 childLocalMatrix = Matrix4x4.TRS(child.localPosition, child.localRotation, child.localScale);
                    Matrix4x4 childWorldMatrix = parentMatrix * childLocalMatrix;
                    DrawChildMeshes(child, childWorldMatrix);
                }
            }
        }
    }
}
