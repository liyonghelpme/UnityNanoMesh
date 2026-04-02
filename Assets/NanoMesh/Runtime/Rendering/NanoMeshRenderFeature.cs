using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace NanoMesh
{
    public sealed class NanoMeshRenderFeature : ScriptableRendererFeature
    {
        private const string DefaultShaderName = "NanoMesh/BaselineLitURP";
        private const string InstanceCullingShaderPath = "Assets/NanoMesh/Runtime/Shaders/NanoMeshPlaceholder.compute";

        private NanoMeshRenderPass pass;
        private Material baselineMaterial;
        [SerializeField] private ComputeShader instanceCullingShader;

        public override void Create()
        {
            if (baselineMaterial == null)
            {
                var shader = Shader.Find(DefaultShaderName);
                if (shader != null)
                {
                    baselineMaterial = new Material(shader)
                    {
                        hideFlags = HideFlags.HideAndDontSave
                    };
                }
            }

            #if UNITY_EDITOR
            if (instanceCullingShader == null)
            {
                instanceCullingShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(InstanceCullingShaderPath);
            }
            #endif

            pass ??= new NanoMeshRenderPass();
            pass.Initialize(baselineMaterial, instanceCullingShader);
            pass.renderPassEvent = RenderPassEvent.BeforeRenderingOpaques;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!isActive || pass == null || baselineMaterial == null)
            {
                return;
            }

            renderer.EnqueuePass(pass);
        }

        protected override void Dispose(bool disposing)
        {
            if (baselineMaterial != null)
            {
                DestroyImmediate(baselineMaterial);
            }

            baselineMaterial = null;
        }

        private sealed class NanoMeshRenderPass : ScriptableRenderPass
        {
            private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
            private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
            private static readonly int BaseMapStId = Shader.PropertyToID("_BaseMap_ST");
            private static readonly int ClusterIndexId = Shader.PropertyToID("_NanoMeshClusterIndex");
            private static readonly int ClusterBufferId = Shader.PropertyToID("_NanoMeshClusterBuffer");
            private static readonly int VertexPayloadBufferId = Shader.PropertyToID("_NanoMeshVertexPayloadBuffer");
            private static readonly int IndexPayloadBufferId = Shader.PropertyToID("_NanoMeshIndexPayloadBuffer");
            private static readonly int UvDecodeId = Shader.PropertyToID("_NanoMeshUvDecode");
            private static readonly int DebugClustersId = Shader.PropertyToID("_NanoMeshDebugClusters");

            private readonly MaterialPropertyBlock propertyBlock = new MaterialPropertyBlock();
            private Material baselineMaterial;
            private ComputeShader instanceCullingShader;

            public void Initialize(Material material, ComputeShader cullingShader)
            {
                baselineMaterial = material;
                instanceCullingShader = cullingShader;
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (renderingData.cameraData.cameraType != CameraType.Game &&
                    renderingData.cameraData.cameraType != CameraType.SceneView)
                {
                    return;
                }

                var manager = NanoMeshManager.Active;
                if (manager == null || baselineMaterial == null)
                {
                    return;
                }

                manager.PrepareFrame(renderingData.cameraData.camera);
                manager.ExecuteInstanceCulling(renderingData.cameraData.camera, instanceCullingShader);
                manager.ExecuteClusterCullingAndLod(renderingData.cameraData.camera, instanceCullingShader);
                if (manager.CurrentVisibleClusterCount == 0)
                {
                    return;
                }

                var commandBuffer = new CommandBuffer
                {
                    name = "NanoMesh Baseline"
                };
                for (var visibleClusterIndex = 0; visibleClusterIndex < manager.CurrentVisibleClusterCount; visibleClusterIndex++)
                {
                    if (!manager.TryGetVisibleCluster(visibleClusterIndex, out var visibleCluster) ||
                        !manager.TryGetPreparedInstanceData(visibleCluster.preparedInstanceIndex, out var instanceData))
                    {
                        continue;
                    }

                    if (!manager.ShouldRenderClusterHierarchyLevel(visibleCluster.hierarchyLevel))
                    {
                        continue;
                    }

                    if (visibleCluster.preparedInstanceIndex < 0 ||
                        visibleCluster.preparedInstanceIndex >= manager.PreparedRenderers.Count)
                    {
                        continue;
                    }

                    var renderer = manager.PreparedRenderers[visibleCluster.preparedInstanceIndex];
                    if (renderer == null || renderer.Asset == null)
                    {
                        continue;
                    }

                    if (!manager.TryGetUploadedAsset(visibleCluster.assetIndex, out var uploadedAsset))
                    {
                        continue;
                    }

                    if (visibleCluster.clusterIndex < 0 || visibleCluster.clusterIndex >= renderer.Asset.clusters.Length)
                    {
                        continue;
                    }

                    var materialSlot = 0;
                    if (renderer.Asset.materialRanges != null &&
                        visibleCluster.materialRangeIndex >= 0 &&
                        visibleCluster.materialRangeIndex < renderer.Asset.materialRanges.Length)
                    {
                        materialSlot = renderer.Asset.materialRanges[visibleCluster.materialRangeIndex].materialSlot;
                    }

                    var sourceMaterial = manager.ResolveMaterialForSlot(renderer, materialSlot);
                    var cluster = renderer.Asset.clusters[visibleCluster.clusterIndex];
                    if (cluster.indexCount <= 0)
                    {
                        continue;
                    }

                    PopulatePropertyBlock(manager, uploadedAsset, renderer.Asset, sourceMaterial, visibleCluster.clusterIndex);
                    commandBuffer.DrawProcedural(
                        instanceData.localToWorld,
                        baselineMaterial,
                        0,
                        MeshTopology.Triangles,
                        cluster.indexCount,
                        1,
                        propertyBlock);
                }

                context.ExecuteCommandBuffer(commandBuffer);
                commandBuffer.Release();
            }

            private void PopulatePropertyBlock(
                NanoMeshManager manager,
                NanoMeshManager.NanoMeshUploadedAssetRecord uploadedAsset,
                NanoMeshAsset asset,
                Material sourceMaterial,
                int clusterIndex)
            {
                propertyBlock.Clear();
                propertyBlock.SetBuffer(ClusterBufferId, uploadedAsset.clusterBuffer);
                propertyBlock.SetBuffer(VertexPayloadBufferId, uploadedAsset.vertexPayloadBuffer);
                propertyBlock.SetBuffer(IndexPayloadBufferId, uploadedAsset.indexPayloadBuffer);
                propertyBlock.SetInt(ClusterIndexId, clusterIndex);
                propertyBlock.SetVector(
                    UvDecodeId,
                    new Vector4(
                        asset.uvMin.x,
                        asset.uvMin.y,
                        asset.uvMax.x - asset.uvMin.x,
                        asset.uvMax.y - asset.uvMin.y));
                propertyBlock.SetFloat(DebugClustersId, manager.DebugView == NanoMeshManager.DebugViewMode.ClusterColor ? 1f : 0f);

                var baseColor = Color.white;
                Texture baseMap = Texture2D.whiteTexture;
                var baseMapSt = new Vector4(1f, 1f, 0f, 0f);

                if (sourceMaterial != null)
                {
                    if (sourceMaterial.HasProperty(BaseColorId))
                    {
                        baseColor = sourceMaterial.GetColor(BaseColorId);
                    }
                    else if (sourceMaterial.HasProperty("_Color"))
                    {
                        baseColor = sourceMaterial.GetColor("_Color");
                    }

                    if (sourceMaterial.HasProperty(BaseMapId))
                    {
                        var texture = sourceMaterial.GetTexture(BaseMapId);
                        if (texture != null)
                        {
                            baseMap = texture;
                        }

                        baseMapSt = sourceMaterial.GetVector(BaseMapStId);
                    }
                    else if (sourceMaterial.HasProperty("_MainTex"))
                    {
                        var texture = sourceMaterial.GetTexture("_MainTex");
                        if (texture != null)
                        {
                            baseMap = texture;
                        }

                        var scale = sourceMaterial.GetTextureScale("_MainTex");
                        var offset = sourceMaterial.GetTextureOffset("_MainTex");
                        baseMapSt = new Vector4(scale.x, scale.y, offset.x, offset.y);
                    }
                }

                propertyBlock.SetColor(BaseColorId, baseColor);
                propertyBlock.SetTexture(BaseMapId, baseMap);
                propertyBlock.SetVector(BaseMapStId, baseMapSt);
            }
        }
    }
}
