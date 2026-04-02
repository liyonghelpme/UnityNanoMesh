using UnityEngine;

namespace NanoMesh
{
    [DisallowMultipleComponent]
    [ExecuteAlways]
    public sealed class NanoMeshRenderer : MonoBehaviour
    {
        [SerializeField] private NanoMeshAsset asset;
        [SerializeField] private Material[] materials = System.Array.Empty<Material>();
        [SerializeField] private float lodBias = 1f;
        [SerializeField] private float errorScale = 1f;

        public NanoMeshAsset Asset => asset;
        public Material[] Materials => materials;
        public float LodBias => lodBias;
        public float ErrorScale => errorScale;
        public bool TryGetFallbackMeshRenderer(out MeshRenderer meshRenderer)
        {
            meshRenderer = GetComponent<MeshRenderer>();
            return meshRenderer != null;
        }

        private void OnEnable()
        {
            NanoMeshManager.RegisterRenderer(this);
        }

        private void OnDisable()
        {
            NanoMeshManager.UnregisterRenderer(this);
        }

        private void OnValidate()
        {
            lodBias = Mathf.Max(0.01f, lodBias);
            errorScale = Mathf.Max(0.01f, errorScale);
            NanoMeshManager.NotifyRendererChanged(this);
        }
    }
}
