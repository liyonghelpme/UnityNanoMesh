using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace NanoMesh
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class NanoMeshManager : MonoBehaviour
    {
        private const int InstanceCullingCounterCount = 8;
        private const int ClusterCounterCurrentFrontier = 0;
        private const int ClusterCounterNextFrontier = 1;
        private const int ClusterCounterVisibleClusters = 2;
        private const int ClusterCounterTraversedNodes = 3;
        private const int ClusterCounterAcceptedClusters = 4;

        public enum DebugViewMode
        {
            Lit = 0,
            ClusterColor = 1
        }

        [Serializable]
        public struct RuntimeStats
        {
            public int registeredRendererCount;
            public int validInstanceCount;
            public int totalPreparedInstanceCount;
            public int visibleInstanceCount;
            public int visibleClusterCount;
            public int traversedNodeCount;
            public int acceptedClusterCount;
            public int uploadedAssetCount;
            public int instanceBufferCapacity;
            public int visibleClusterCapacity;
            public int traversalCapacity;
            public long staticGpuBytes;
            public long transientGpuBytes;
            public long totalEstimatedGpuBytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NanoMeshInstanceData
        {
            public Matrix4x4 localToWorld;
            public Matrix4x4 worldToLocal;
            public Vector4 worldBoundsSphere;
            public Vector3 worldBoundsExtents;
            public int assetIndex;
            public float lodBias;
            public float errorScale;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct VisibleClusterRecord
        {
            public int preparedInstanceIndex;
            public int assetIndex;
            public int clusterIndex;
            public int materialRangeIndex;
            public int hierarchyLevel;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct TraversalNodeRecord
        {
            public int preparedInstanceIndex;
            public int hierarchyNodeIndex;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ClusterRecordGpu
        {
            public int vertexDataOffsetBytes;
            public int vertexCount;
            public int indexDataOffsetBytes;
            public int indexCount;
            public int materialRangeIndex;
            public int hierarchyNodeIndex;
            public int hierarchyLevel;
            public float geometricError;
            public Vector3 positionOrigin;
            public float padding0;
            public Vector3 positionExtent;
            public float padding1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ClusterCullRecordGpu
        {
            public Vector3 localBoundsCenter;
            public float padding0;
            public Vector3 localBoundsExtents;
            public float geometricError;
            public Vector4 boundingSphere;
            public Vector4 normalCone;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HierarchyNodeGpu
        {
            public int clusterIndex;
            public int firstClusterIndex;
            public int renderClusterCount;
            public int parentNodeIndex;
            public int firstChildNodeIndex;
            public int childCount;
            public int hierarchyLevel;
            public int isLeaf;
            public Vector3 localBoundsCenter;
            public float geometricError;
            public Vector3 localBoundsExtents;
            public float parentError;
            public Vector4 boundingSphere;
            public Vector4 normalCone;
        }

        internal sealed class NanoMeshUploadedAssetRecord : IDisposable
        {
            public readonly NanoMeshAsset asset;
            public readonly int assetIndex;
            public readonly GraphicsBuffer vertexPayloadBuffer;
            public readonly GraphicsBuffer indexPayloadBuffer;
            public readonly GraphicsBuffer clusterBuffer;
            public readonly GraphicsBuffer clusterCullBuffer;
            public readonly GraphicsBuffer hierarchyNodeBuffer;
            public readonly GraphicsBuffer coarseRootNodeBuffer;
            public readonly long staticGpuBytes;

            public NanoMeshUploadedAssetRecord(
                NanoMeshAsset asset,
                int assetIndex,
                GraphicsBuffer vertexPayloadBuffer,
                GraphicsBuffer indexPayloadBuffer,
                GraphicsBuffer clusterBuffer,
                GraphicsBuffer clusterCullBuffer,
                GraphicsBuffer hierarchyNodeBuffer,
                GraphicsBuffer coarseRootNodeBuffer,
                long staticGpuBytes)
            {
                this.asset = asset;
                this.assetIndex = assetIndex;
                this.vertexPayloadBuffer = vertexPayloadBuffer;
                this.indexPayloadBuffer = indexPayloadBuffer;
                this.clusterBuffer = clusterBuffer;
                this.clusterCullBuffer = clusterCullBuffer;
                this.hierarchyNodeBuffer = hierarchyNodeBuffer;
                this.coarseRootNodeBuffer = coarseRootNodeBuffer;
                this.staticGpuBytes = staticGpuBytes;
            }

            public void Dispose()
            {
                vertexPayloadBuffer?.Release();
                indexPayloadBuffer?.Release();
                clusterBuffer?.Release();
                clusterCullBuffer?.Release();
                hierarchyNodeBuffer?.Release();
                coarseRootNodeBuffer?.Release();
            }
        }

        private static NanoMeshManager s_activeManager;

        [SerializeField] private RuntimeStats runtimeStats;
        [SerializeField] private DebugViewMode debugView = DebugViewMode.Lit;
        [SerializeField] private bool enableInstanceCulling = true;
        [SerializeField] private bool enableClusterCulling = true;
        [SerializeField] private bool enableConeCulling;
        [SerializeField] private float lodErrorThreshold = 12f;
        [SerializeField] private bool enableHierarchyLevelFilter;
        [SerializeField] private int hierarchyLevelFilter;

        private readonly List<NanoMeshRenderer> registeredRenderers = new List<NanoMeshRenderer>();
        private readonly List<NanoMeshRenderer> validRenderers = new List<NanoMeshRenderer>();
        private readonly List<NanoMeshInstanceData> instanceUploadData = new List<NanoMeshInstanceData>();
        private readonly List<int> currentRenderInstanceIndices = new List<int>();
        private readonly List<VisibleClusterRecord> currentVisibleClusters = new List<VisibleClusterRecord>();
        private readonly List<string> prepareWarnings = new List<string>();
        private readonly Dictionary<NanoMeshAsset, NanoMeshUploadedAssetRecord> uploadedAssets = new Dictionary<NanoMeshAsset, NanoMeshUploadedAssetRecord>();
        private readonly List<NanoMeshUploadedAssetRecord> uploadedAssetsByIndex = new List<NanoMeshUploadedAssetRecord>();

        private NanoMeshRuntimeBuffers runtimeBuffers;
        private uint[] debugCounterReadback;
        private uint[] visibleInstanceReadback;
        private VisibleClusterRecord[] visibleClusterReadback;
        private int preparedMaterialRangeCount;
        private int preparedClusterCount;
        private int totalPreparedInstanceCount;
        private int visibleInstanceCount;
        private int visibleClusterCount;
        private int traversedNodeCount;
        private int acceptedClusterCount;

        private struct ClusterKernelSet
        {
            public int resetTraversalState;
            public int seedCoarseRoots;
            public int traverseFrontier;
            public int swapFrontierCounts;
            public int buildIndirectArgs;
        }

        public static NanoMeshManager Active => ResolveActiveManager();

        public RuntimeStats Stats => runtimeStats;
        public int RegisteredRendererCount => runtimeStats.registeredRendererCount;
        public int ValidInstanceCount => runtimeStats.validInstanceCount;
        public int UploadedAssetCount => runtimeStats.uploadedAssetCount;
        public DebugViewMode DebugView => debugView;
        public IReadOnlyList<NanoMeshRenderer> PreparedRenderers => validRenderers;
        public int PreparedMaterialRangeCount => preparedMaterialRangeCount;
        public int PreparedClusterCount => preparedClusterCount;
        public bool EnableInstanceCulling => enableInstanceCulling;
        public bool EnableClusterCulling => enableClusterCulling;
        public bool EnableConeCulling => enableConeCulling;
        public float LodErrorThreshold => lodErrorThreshold;
        public bool EnableHierarchyLevelFilter => enableHierarchyLevelFilter;
        public int HierarchyLevelFilter => hierarchyLevelFilter;
        public int CurrentRenderInstanceCount => currentRenderInstanceIndices.Count;
        public int CurrentVisibleClusterCount => currentVisibleClusters.Count;
        internal NanoMeshRuntimeBuffers RuntimeBuffers => runtimeBuffers;

        public bool ShouldRenderClusterHierarchyLevel(int hierarchyLevel)
        {
            return !enableHierarchyLevelFilter || hierarchyLevel == hierarchyLevelFilter;
        }

        private void OnEnable()
        {
            if (s_activeManager != null && s_activeManager != this)
            {
                Debug.LogWarning("NanoMeshManager found multiple active instances. Using the first enabled manager.", this);
                enabled = false;
                return;
            }

            s_activeManager = this;
            runtimeBuffers ??= new NanoMeshRuntimeBuffers();
            RefreshRegistrations();
        }

        private void OnDisable()
        {
            if (s_activeManager == this)
            {
                s_activeManager = null;
            }
        }

        private void OnDestroy()
        {
            if (s_activeManager == this)
            {
                s_activeManager = null;
            }

            ReleaseAll();
        }

        public static bool TryGetActiveManager(out NanoMeshManager manager)
        {
            manager = ResolveActiveManager();
            return manager != null;
        }

        public static void RegisterRenderer(NanoMeshRenderer renderer)
        {
            if (renderer == null || !TryGetActiveManager(out var manager))
            {
                return;
            }

            manager.RegisterInternal(renderer);
        }

        public static void UnregisterRenderer(NanoMeshRenderer renderer)
        {
            if (renderer == null || !TryGetActiveManager(out var manager))
            {
                return;
            }

            manager.UnregisterInternal(renderer);
        }

        public static void NotifyRendererChanged(NanoMeshRenderer renderer)
        {
            if (renderer == null || !TryGetActiveManager(out var manager))
            {
                return;
            }

            manager.MarkRendererDirty(renderer);
        }

        public void RefreshRegistrations()
        {
            registeredRenderers.Clear();
            var renderers = FindObjectsByType<NanoMeshRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (var i = 0; i < renderers.Length; i++)
            {
                RegisterInternal(renderers[i]);
            }

            UpdateStats();
        }

        public void PrepareFrame(Camera camera)
        {
            if (camera == null)
            {
                return;
            }

            runtimeBuffers ??= new NanoMeshRuntimeBuffers();
            validRenderers.Clear();
            instanceUploadData.Clear();
            currentVisibleClusters.Clear();
            prepareWarnings.Clear();
            preparedMaterialRangeCount = 0;
            preparedClusterCount = 0;
            totalPreparedInstanceCount = 0;
            visibleInstanceCount = 0;
            visibleClusterCount = 0;
            traversedNodeCount = 0;
            acceptedClusterCount = 0;

            var assetIndexLookup = new Dictionary<NanoMeshAsset, int>();
            for (var i = 0; i < registeredRenderers.Count; i++)
            {
                var renderer = registeredRenderers[i];
                if (!IsRendererValid(renderer, out var validationError))
                {
                    if (!string.IsNullOrWhiteSpace(validationError))
                    {
                        prepareWarnings.Add(validationError);
                    }

                    continue;
                }

                var asset = renderer.Asset;
                if (!uploadedAssets.TryGetValue(asset, out var uploadedAsset))
                {
                    uploadedAsset = UploadAsset(asset, uploadedAssets.Count);
                    uploadedAssets.Add(asset, uploadedAsset);
                    EnsureUploadedAssetIndex(uploadedAsset);
                }

                if (!assetIndexLookup.ContainsKey(asset))
                {
                    assetIndexLookup.Add(asset, uploadedAsset.assetIndex);
                }

                validRenderers.Add(renderer);
                instanceUploadData.Add(BuildInstanceData(renderer, uploadedAsset.assetIndex));
                preparedMaterialRangeCount += asset.materialRanges != null ? asset.materialRanges.Length : 0;
                preparedClusterCount += asset.clusterCount;
            }

            runtimeBuffers.EnsureFrameBuffers(
                instanceUploadData.Count,
                ComputeVisibleClusterCapacity(),
                ComputeTraversalNodeCapacity(),
                InstanceCullingCounterCount);
            runtimeBuffers.UploadInstances(instanceUploadData);
            runtimeBuffers.ResetPerFrameBuffers();
            FillCurrentRenderInstancesWithPrepared();
            SyncVisibleInstanceBufferWithCurrentRenderInstances();
            FillCurrentVisibleClustersWithCurrentRenderInstances();
            totalPreparedInstanceCount = instanceUploadData.Count;
            visibleInstanceCount = currentRenderInstanceIndices.Count;

            for (var i = 0; i < prepareWarnings.Count; i++)
            {
                Debug.LogWarning(prepareWarnings[i], this);
            }

            UpdateStats();
        }

        internal bool TryGetPreparedInstanceData(int preparedRendererIndex, out NanoMeshInstanceData instanceData)
        {
            if (preparedRendererIndex < 0 || preparedRendererIndex >= instanceUploadData.Count)
            {
                instanceData = default;
                return false;
            }

            instanceData = instanceUploadData[preparedRendererIndex];
            return true;
        }

        internal bool TryGetCurrentRenderInstance(int renderInstanceIndex, out NanoMeshRenderer renderer, out NanoMeshInstanceData instanceData)
        {
            renderer = null;
            instanceData = default;

            if (renderInstanceIndex < 0 || renderInstanceIndex >= currentRenderInstanceIndices.Count)
            {
                return false;
            }

            var preparedIndex = currentRenderInstanceIndices[renderInstanceIndex];
            if (preparedIndex < 0 || preparedIndex >= validRenderers.Count)
            {
                return false;
            }

            renderer = validRenderers[preparedIndex];
            return TryGetPreparedInstanceData(preparedIndex, out instanceData);
        }

        internal bool TryGetVisibleCluster(int visibleClusterIndex, out VisibleClusterRecord visibleCluster)
        {
            if (visibleClusterIndex < 0 || visibleClusterIndex >= currentVisibleClusters.Count)
            {
                visibleCluster = default;
                return false;
            }

            visibleCluster = currentVisibleClusters[visibleClusterIndex];
            return true;
        }

        internal bool TryGetUploadedAsset(NanoMeshAsset asset, out NanoMeshUploadedAssetRecord uploadedAsset)
        {
            if (asset == null)
            {
                uploadedAsset = null;
                return false;
            }

            return uploadedAssets.TryGetValue(asset, out uploadedAsset);
        }

        internal bool TryGetUploadedAsset(int assetIndex, out NanoMeshUploadedAssetRecord uploadedAsset)
        {
            if (assetIndex < 0 || assetIndex >= uploadedAssetsByIndex.Count)
            {
                uploadedAsset = null;
                return false;
            }

            uploadedAsset = uploadedAssetsByIndex[assetIndex];
            return uploadedAsset != null;
        }

        public bool ExecuteInstanceCulling(Camera camera, ComputeShader instanceCullingShader)
        {
            FillCurrentRenderInstancesWithPrepared();
            SyncVisibleInstanceBufferWithCurrentRenderInstances();
            FillCurrentVisibleClustersWithCurrentRenderInstances();
            visibleInstanceCount = currentRenderInstanceIndices.Count;
            UpdateStats();

            if (!enableInstanceCulling || camera == null || instanceCullingShader == null || runtimeBuffers == null || instanceUploadData.Count == 0)
            {
                return false;
            }

            if (!TryResolveKernel(instanceCullingShader, "CSMain", out var kernelIndex))
            {
                return false;
            }

            try
            {
                runtimeBuffers.ResetPerFrameBuffers();
                instanceCullingShader.SetInt("_PreparedInstanceCount", instanceUploadData.Count);
                instanceCullingShader.SetVectorArray("_FrustumPlanes", BuildFrustumPlaneVectorArray(camera));
                instanceCullingShader.SetBuffer(kernelIndex, "_NanoMeshInstanceBuffer", runtimeBuffers.InstanceBuffer);
                instanceCullingShader.SetBuffer(kernelIndex, "_VisibleInstanceAppendBuffer", runtimeBuffers.VisibleInstanceBuffer);
                instanceCullingShader.SetBuffer(kernelIndex, "_DebugCounters", runtimeBuffers.DebugCountersBuffer);

                var dispatchCount = Mathf.Max(1, Mathf.CeilToInt(instanceUploadData.Count / 64f));
                instanceCullingShader.Dispatch(kernelIndex, dispatchCount, 1, 1);

                var counters = EnsureDebugCounterReadback();
                runtimeBuffers.DebugCountersBuffer.GetData(counters, 0, 0, counters.Length);
                totalPreparedInstanceCount = Mathf.Clamp((int)counters[0], 0, instanceUploadData.Count);
                visibleInstanceCount = Mathf.Clamp((int)counters[1], 0, instanceUploadData.Count);

                currentRenderInstanceIndices.Clear();
                if (visibleInstanceCount > 0)
                {
                    var visible = EnsureVisibleInstanceReadback(visibleInstanceCount);
                    runtimeBuffers.VisibleInstanceBuffer.GetData(visible, 0, 0, visibleInstanceCount);
                    for (var i = 0; i < visibleInstanceCount; i++)
                    {
                        var preparedIndex = (int)visible[i];
                        if (preparedIndex >= 0 && preparedIndex < validRenderers.Count)
                        {
                            currentRenderInstanceIndices.Add(preparedIndex);
                        }
                    }
                }

                visibleInstanceCount = currentRenderInstanceIndices.Count;
                FillCurrentVisibleClustersWithCurrentRenderInstances();
                UpdateStats();
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"NanoMesh instance culling failed. Falling back to no-culling path. {exception.Message}", this);
                FillCurrentRenderInstancesWithPrepared();
                SyncVisibleInstanceBufferWithCurrentRenderInstances();
                FillCurrentVisibleClustersWithCurrentRenderInstances();
                visibleInstanceCount = currentRenderInstanceIndices.Count;
                UpdateStats();
                return false;
            }
        }

        public bool ExecuteClusterCullingAndLod(Camera camera, ComputeShader clusterCullingShader)
        {
            FillCurrentVisibleClustersWithCurrentRenderInstances();
            visibleClusterCount = currentVisibleClusters.Count;
            acceptedClusterCount = visibleClusterCount;
            traversedNodeCount = 0;
            UpdateStats();

            if (!enableClusterCulling || camera == null || clusterCullingShader == null || runtimeBuffers == null || currentRenderInstanceIndices.Count == 0)
            {
                return false;
            }

            if (!TryResolveClusterKernelSet(clusterCullingShader, out var kernels))
            {
                return false;
            }

            try
            {
                runtimeBuffers.ResetClusterCullingBuffers();
                var frustumPlanes = BuildFrustumPlaneVectorArray(camera);
                var projectionScale = 0.5f * camera.pixelHeight / Mathf.Max(0.001f, Mathf.Tan(0.5f * camera.fieldOfView * Mathf.Deg2Rad));
                var visibleInstanceDispatchCount = Mathf.Max(1, Mathf.CeilToInt(currentRenderInstanceIndices.Count / 64f));
                var frontierDispatchCount = Mathf.Max(1, Mathf.CeilToInt(runtimeBuffers.TraversalCapacity / 64f));

                clusterCullingShader.SetInt("_PreparedInstanceCount", instanceUploadData.Count);
                clusterCullingShader.SetInt("_VisibleInstanceCount", currentRenderInstanceIndices.Count);
                clusterCullingShader.SetInt("_VisibleClusterCapacity", runtimeBuffers.VisibleClusterCapacity);
                clusterCullingShader.SetInt("_TraversalCapacity", runtimeBuffers.TraversalCapacity);
                clusterCullingShader.SetFloat("_LodErrorThreshold", Mathf.Max(0.01f, lodErrorThreshold));
                clusterCullingShader.SetInt("_UseConeCulling", enableConeCulling ? 1 : 0);
                clusterCullingShader.SetVector("_CameraPositionWS", camera.transform.position);
                clusterCullingShader.SetFloat("_ProjectionScale", projectionScale);
                clusterCullingShader.SetVectorArray("_FrustumPlanes", frustumPlanes);
                clusterCullingShader.SetBuffer(kernels.seedCoarseRoots, "_NanoMeshInstanceBuffer", runtimeBuffers.InstanceBuffer);
                clusterCullingShader.SetBuffer(kernels.seedCoarseRoots, "_VisibleInstanceBuffer", runtimeBuffers.VisibleInstanceBuffer);
                clusterCullingShader.SetBuffer(kernels.seedCoarseRoots, "_CurrentFrontierBuffer", runtimeBuffers.TraversalFrontierBufferA);
                clusterCullingShader.SetBuffer(kernels.seedCoarseRoots, "_DebugCounters", runtimeBuffers.DebugCountersBuffer);

                clusterCullingShader.SetBuffer(kernels.traverseFrontier, "_NanoMeshInstanceBuffer", runtimeBuffers.InstanceBuffer);
                clusterCullingShader.SetBuffer(kernels.traverseFrontier, "_VisibleClusterBuffer", runtimeBuffers.VisibleClusterBuffer);
                clusterCullingShader.SetBuffer(kernels.traverseFrontier, "_DebugCounters", runtimeBuffers.DebugCountersBuffer);
                clusterCullingShader.SetBuffer(kernels.swapFrontierCounts, "_DebugCounters", runtimeBuffers.DebugCountersBuffer);
                clusterCullingShader.SetBuffer(kernels.resetTraversalState, "_DebugCounters", runtimeBuffers.DebugCountersBuffer);
                clusterCullingShader.SetBuffer(kernels.buildIndirectArgs, "_DebugCounters", runtimeBuffers.DebugCountersBuffer);
                clusterCullingShader.SetBuffer(kernels.buildIndirectArgs, "_IndirectArgsBuffer", runtimeBuffers.IndirectArgsBuffer);

                for (var assetIndex = 0; assetIndex < uploadedAssetsByIndex.Count; assetIndex++)
                {
                    var uploadedAsset = uploadedAssetsByIndex[assetIndex];
                    if (uploadedAsset == null || uploadedAsset.asset == null)
                    {
                        continue;
                    }

                    clusterCullingShader.SetInt("_ActiveAssetIndex", assetIndex);
                    clusterCullingShader.SetInt("_CoarseRootCount", uploadedAsset.asset.coarseRootCount);
                    clusterCullingShader.Dispatch(kernels.resetTraversalState, 1, 1, 1);

                    clusterCullingShader.SetBuffer(kernels.seedCoarseRoots, "_CoarseRootNodeBuffer", uploadedAsset.coarseRootNodeBuffer);
                    clusterCullingShader.Dispatch(kernels.seedCoarseRoots, visibleInstanceDispatchCount, 1, 1);

                    var useFrontierA = true;
                    var hierarchyIterations = Mathf.Max(1, uploadedAsset.asset.hierarchyDepth);
                    for (var iteration = 0; iteration < hierarchyIterations; iteration++)
                    {
                        clusterCullingShader.SetBuffer(
                            kernels.traverseFrontier,
                            "_CurrentFrontierBuffer",
                            useFrontierA ? runtimeBuffers.TraversalFrontierBufferA : runtimeBuffers.TraversalFrontierBufferB);
                        clusterCullingShader.SetBuffer(
                            kernels.traverseFrontier,
                            "_NextFrontierBuffer",
                            useFrontierA ? runtimeBuffers.TraversalFrontierBufferB : runtimeBuffers.TraversalFrontierBufferA);
                        clusterCullingShader.SetBuffer(kernels.traverseFrontier, "_NanoMeshClusterBuffer", uploadedAsset.clusterBuffer);
                        clusterCullingShader.SetBuffer(kernels.traverseFrontier, "_NanoMeshClusterCullBuffer", uploadedAsset.clusterCullBuffer);
                        clusterCullingShader.SetBuffer(kernels.traverseFrontier, "_NanoMeshHierarchyNodeBuffer", uploadedAsset.hierarchyNodeBuffer);
                        clusterCullingShader.Dispatch(kernels.traverseFrontier, frontierDispatchCount, 1, 1);
                        clusterCullingShader.Dispatch(kernels.swapFrontierCounts, 1, 1, 1);
                        useFrontierA = !useFrontierA;
                    }
                }

                clusterCullingShader.Dispatch(kernels.buildIndirectArgs, 1, 1, 1);

                var counters = EnsureDebugCounterReadback();
                runtimeBuffers.DebugCountersBuffer.GetData(counters, 0, 0, counters.Length);
                traversedNodeCount = (int)counters[ClusterCounterTraversedNodes];
                acceptedClusterCount = (int)counters[ClusterCounterAcceptedClusters];
                BuildCurrentVisibleClustersFromReadback((int)counters[ClusterCounterVisibleClusters]);
                visibleClusterCount = currentVisibleClusters.Count;
                UpdateStats();
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"NanoMesh cluster culling failed. Falling back to CPU-visible cluster list. {exception.Message}", this);
                FillCurrentVisibleClustersWithCurrentRenderInstances();
                visibleClusterCount = currentVisibleClusters.Count;
                traversedNodeCount = 0;
                acceptedClusterCount = visibleClusterCount;
                UpdateStats();
                return false;
            }
        }

        public static bool TryResolveRequiredKernels(ComputeShader computeShader, params string[] kernelNames)
        {
            if (computeShader == null || kernelNames == null || kernelNames.Length == 0)
            {
                return false;
            }

            for (var i = 0; i < kernelNames.Length; i++)
            {
                if (!TryResolveKernel(computeShader, kernelNames[i], out _))
                {
                    return false;
                }
            }

            return true;
        }

        public Material ResolveMaterialForSlot(NanoMeshRenderer renderer, int materialSlot)
        {
            if (renderer == null || materialSlot < 0)
            {
                return null;
            }

            var overrideMaterials = renderer.Materials;
            if (overrideMaterials != null && materialSlot < overrideMaterials.Length && overrideMaterials[materialSlot] != null)
            {
                return overrideMaterials[materialSlot];
            }

            if (renderer.TryGetFallbackMeshRenderer(out var meshRenderer))
            {
                var sharedMaterials = meshRenderer.sharedMaterials;
                if (sharedMaterials != null && materialSlot < sharedMaterials.Length)
                {
                    return sharedMaterials[materialSlot];
                }
            }

            return null;
        }

        private void RegisterInternal(NanoMeshRenderer renderer)
        {
            if (renderer == null || registeredRenderers.Contains(renderer))
            {
                return;
            }

            registeredRenderers.Add(renderer);
            UpdateStats();
        }

        private void UnregisterInternal(NanoMeshRenderer renderer)
        {
            if (renderer == null)
            {
                return;
            }

            registeredRenderers.Remove(renderer);
            UpdateStats();
        }

        private void MarkRendererDirty(NanoMeshRenderer renderer)
        {
            if (renderer == null)
            {
                return;
            }

            if (!registeredRenderers.Contains(renderer) && renderer.isActiveAndEnabled)
            {
                registeredRenderers.Add(renderer);
            }

            UpdateStats();
        }

        private bool IsRendererValid(NanoMeshRenderer renderer, out string error)
        {
            error = null;
            if (renderer == null)
            {
                return false;
            }

            if (!renderer.isActiveAndEnabled || !renderer.gameObject.activeInHierarchy)
            {
                return false;
            }

            var asset = renderer.Asset;
            if (asset == null)
            {
                error = "NanoMeshRenderer on " + renderer.name + " has no NanoMeshAsset assigned.";
                return false;
            }

            if (!asset.TryGetRuntimeValidationError(out error))
            {
                error = "NanoMeshRenderer on " + renderer.name + " uses an invalid asset: " + error;
                return false;
            }

            return true;
        }

        private NanoMeshUploadedAssetRecord UploadAsset(NanoMeshAsset asset, int assetIndex)
        {
            var clusterStride = Marshal.SizeOf<ClusterRecordGpu>();
            var clusterCullStride = Marshal.SizeOf<ClusterCullRecordGpu>();
            var hierarchyStride = Marshal.SizeOf<HierarchyNodeGpu>();
            var coarseRootStride = sizeof(int);

            var vertexPayloadWords = ConvertBytesToUInt32(asset.packedVertexData);
            var indexPayloadWords = ConvertBytesToUInt32(asset.packedIndexData);
            var clusterData = BuildClusterGpuData(asset.clusters);
            var clusterCullData = BuildClusterCullGpuData(asset.clusterCullData);
            var hierarchyNodeData = BuildHierarchyNodeGpuData(asset.hierarchyNodes);

            var vertexPayloadBuffer = CreateStructuredBuffer(vertexPayloadWords.Length, sizeof(uint), GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, vertexPayloadWords);
            var indexPayloadBuffer = CreateStructuredBuffer(indexPayloadWords.Length, sizeof(uint), GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, indexPayloadWords);
            var clusterBuffer = CreateStructuredBuffer(clusterData.Length, clusterStride, GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopySource, clusterData);
            var clusterCullBuffer = CreateStructuredBuffer(clusterCullData.Length, clusterCullStride, GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopySource, clusterCullData);
            var hierarchyNodeBuffer = CreateStructuredBuffer(hierarchyNodeData.Length, hierarchyStride, GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopySource, hierarchyNodeData);
            var coarseRootNodeBuffer = CreateStructuredBuffer(asset.coarseRootNodeIndices.Length, coarseRootStride, GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopySource, asset.coarseRootNodeIndices);
            var staticGpuBytes =
                ComputeBufferBytes(vertexPayloadBuffer) +
                ComputeBufferBytes(indexPayloadBuffer) +
                ComputeBufferBytes(clusterBuffer) +
                ComputeBufferBytes(clusterCullBuffer) +
                ComputeBufferBytes(hierarchyNodeBuffer) +
                ComputeBufferBytes(coarseRootNodeBuffer);

            return new NanoMeshUploadedAssetRecord(
                asset,
                assetIndex,
                vertexPayloadBuffer,
                indexPayloadBuffer,
                clusterBuffer,
                clusterCullBuffer,
                hierarchyNodeBuffer,
                coarseRootNodeBuffer,
                staticGpuBytes);
        }

        private static NanoMeshInstanceData BuildInstanceData(NanoMeshRenderer renderer, int assetIndex)
        {
            var transform = renderer.transform;
            var localToWorld = transform.localToWorldMatrix;
            var worldToLocal = transform.worldToLocalMatrix;
            var worldBounds = TransformBounds(localToWorld, renderer.Asset.assetBounds);
            var extents = worldBounds.extents;
            var radius = extents.magnitude;
            return new NanoMeshInstanceData
            {
                localToWorld = localToWorld,
                worldToLocal = worldToLocal,
                worldBoundsSphere = new Vector4(worldBounds.center.x, worldBounds.center.y, worldBounds.center.z, radius),
                worldBoundsExtents = extents,
                assetIndex = assetIndex,
                lodBias = renderer.LodBias,
                errorScale = renderer.ErrorScale
            };
        }

        private int ComputeVisibleClusterCapacity()
        {
            var count = 0;
            for (var i = 0; i < validRenderers.Count; i++)
            {
                var renderer = validRenderers[i];
                if (renderer != null && renderer.Asset != null)
                {
                    count += Math.Max(1, renderer.Asset.clusterCount);
                }
            }

            return Math.Max(1, count);
        }

        private int ComputeTraversalNodeCapacity()
        {
            var count = 0;
            for (var i = 0; i < validRenderers.Count; i++)
            {
                var renderer = validRenderers[i];
                if (renderer != null && renderer.Asset != null)
                {
                    count += Math.Max(1, renderer.Asset.hierarchyNodeCount);
                }
            }

            return Math.Max(1, count);
        }

        private void UpdateStats()
        {
            var staticGpuBytes = 0L;
            foreach (var pair in uploadedAssets)
            {
                staticGpuBytes += pair.Value.staticGpuBytes;
            }

            runtimeStats = new RuntimeStats
            {
                registeredRendererCount = registeredRenderers.Count,
                validInstanceCount = instanceUploadData.Count,
                totalPreparedInstanceCount = totalPreparedInstanceCount,
                visibleInstanceCount = visibleInstanceCount,
                visibleClusterCount = visibleClusterCount,
                traversedNodeCount = traversedNodeCount,
                acceptedClusterCount = acceptedClusterCount,
                uploadedAssetCount = uploadedAssets.Count,
                instanceBufferCapacity = runtimeBuffers != null ? runtimeBuffers.InstanceCapacity : 0,
                visibleClusterCapacity = runtimeBuffers != null ? runtimeBuffers.VisibleClusterCapacity : 0,
                traversalCapacity = runtimeBuffers != null ? runtimeBuffers.TraversalCapacity : 0,
                staticGpuBytes = staticGpuBytes,
                transientGpuBytes = runtimeBuffers != null ? runtimeBuffers.TransientGpuBytes : 0,
                totalEstimatedGpuBytes = staticGpuBytes + (runtimeBuffers != null ? runtimeBuffers.TransientGpuBytes : 0)
            };
        }

        private void ReleaseAll()
        {
            foreach (var pair in uploadedAssets)
            {
                pair.Value.Dispose();
            }

            uploadedAssets.Clear();
            uploadedAssetsByIndex.Clear();
            runtimeBuffers?.Dispose();
            runtimeBuffers = null;
            preparedMaterialRangeCount = 0;
            preparedClusterCount = 0;
            totalPreparedInstanceCount = 0;
            visibleInstanceCount = 0;
            visibleClusterCount = 0;
            traversedNodeCount = 0;
            acceptedClusterCount = 0;
            currentRenderInstanceIndices.Clear();
            currentVisibleClusters.Clear();
            UpdateStats();
        }

        private void FillCurrentRenderInstancesWithPrepared()
        {
            currentRenderInstanceIndices.Clear();
            for (var i = 0; i < validRenderers.Count; i++)
            {
                currentRenderInstanceIndices.Add(i);
            }
        }

        private void FillCurrentVisibleClustersWithCurrentRenderInstances()
        {
            currentVisibleClusters.Clear();
            for (var renderInstanceIndex = 0; renderInstanceIndex < currentRenderInstanceIndices.Count; renderInstanceIndex++)
            {
                var preparedInstanceIndex = currentRenderInstanceIndices[renderInstanceIndex];
                if (preparedInstanceIndex < 0 || preparedInstanceIndex >= validRenderers.Count)
                {
                    continue;
                }

                var renderer = validRenderers[preparedInstanceIndex];
                if (renderer == null || renderer.Asset == null)
                {
                    continue;
                }

                var asset = renderer.Asset;
                var fallbackClusterCount = asset.leafClusterCount > 0 ? asset.leafClusterCount : asset.clusterCount;
                fallbackClusterCount = Mathf.Clamp(fallbackClusterCount, 0, asset.clusters.Length);
                for (var clusterIndex = 0; clusterIndex < fallbackClusterCount; clusterIndex++)
                {
                    var cluster = asset.clusters[clusterIndex];
                    currentVisibleClusters.Add(new VisibleClusterRecord
                    {
                        preparedInstanceIndex = preparedInstanceIndex,
                        assetIndex = instanceUploadData[preparedInstanceIndex].assetIndex,
                        clusterIndex = clusterIndex,
                        materialRangeIndex = cluster.materialRangeIndex,
                        hierarchyLevel = cluster.hierarchyLevel
                    });
                }
            }

            visibleClusterCount = currentVisibleClusters.Count;
            acceptedClusterCount = visibleClusterCount;
        }

        private void BuildCurrentVisibleClustersFromReadback(int count)
        {
            currentVisibleClusters.Clear();
            var safeCount = Mathf.Clamp(count, 0, runtimeBuffers != null ? runtimeBuffers.VisibleClusterCapacity : 0);
            if (safeCount <= 0 || runtimeBuffers == null)
            {
                return;
            }

            var visibleClusters = EnsureVisibleClusterReadback(safeCount);
            runtimeBuffers.VisibleClusterBuffer.GetData(visibleClusters, 0, 0, safeCount);
            for (var i = 0; i < safeCount; i++)
            {
                var visibleCluster = visibleClusters[i];
                if (visibleCluster.preparedInstanceIndex < 0 || visibleCluster.preparedInstanceIndex >= validRenderers.Count)
                {
                    continue;
                }

                currentVisibleClusters.Add(visibleCluster);
            }

            acceptedClusterCount = currentVisibleClusters.Count;
        }

        private void SyncVisibleInstanceBufferWithCurrentRenderInstances()
        {
            if (runtimeBuffers?.VisibleInstanceBuffer == null)
            {
                return;
            }

            runtimeBuffers.VisibleInstanceBuffer.SetCounterValue(0);
            if (currentRenderInstanceIndices.Count == 0)
            {
                return;
            }

            var visibleInstances = EnsureVisibleInstanceReadback(currentRenderInstanceIndices.Count);
            for (var i = 0; i < currentRenderInstanceIndices.Count; i++)
            {
                visibleInstances[i] = (uint)currentRenderInstanceIndices[i];
            }

            runtimeBuffers.VisibleInstanceBuffer.SetData(visibleInstances, 0, 0, currentRenderInstanceIndices.Count);
            runtimeBuffers.VisibleInstanceBuffer.SetCounterValue((uint)currentRenderInstanceIndices.Count);
        }

        private void EnsureUploadedAssetIndex(NanoMeshUploadedAssetRecord uploadedAsset)
        {
            while (uploadedAssetsByIndex.Count <= uploadedAsset.assetIndex)
            {
                uploadedAssetsByIndex.Add(null);
            }

            uploadedAssetsByIndex[uploadedAsset.assetIndex] = uploadedAsset;
        }

        private static bool TryResolveKernel(ComputeShader computeShader, string kernelName, out int kernelIndex)
        {
            kernelIndex = -1;
            if (computeShader == null || string.IsNullOrWhiteSpace(kernelName))
            {
                return false;
            }

            try
            {
                kernelIndex = computeShader.FindKernel(kernelName);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"NanoMesh compute shader kernel lookup failed for '{kernelName}'. {exception.Message}");
                return false;
            }

            if (kernelIndex < 0)
            {
                Debug.LogWarning($"NanoMesh compute shader is missing kernel '{kernelName}'.");
                return false;
            }

            return true;
        }

        private static bool TryResolveClusterKernelSet(ComputeShader computeShader, out ClusterKernelSet kernels)
        {
            kernels = default;
            if (!TryResolveKernel(computeShader, "ResetTraversalState", out kernels.resetTraversalState) ||
                !TryResolveKernel(computeShader, "SeedCoarseRoots", out kernels.seedCoarseRoots) ||
                !TryResolveKernel(computeShader, "TraverseFrontier", out kernels.traverseFrontier) ||
                !TryResolveKernel(computeShader, "SwapFrontierCounts", out kernels.swapFrontierCounts) ||
                !TryResolveKernel(computeShader, "BuildIndirectArgs", out kernels.buildIndirectArgs))
            {
                return false;
            }

            return true;
        }

        private uint[] EnsureDebugCounterReadback()
        {
            var count = runtimeBuffers != null ? Math.Max(1, runtimeBuffers.DebugCounterCount) : 1;
            if (debugCounterReadback == null || debugCounterReadback.Length < count)
            {
                debugCounterReadback = new uint[count];
            }
            else
            {
                Array.Clear(debugCounterReadback, 0, debugCounterReadback.Length);
            }

            return debugCounterReadback;
        }

        private uint[] EnsureVisibleInstanceReadback(int count)
        {
            var safeCount = Math.Max(1, count);
            if (visibleInstanceReadback == null || visibleInstanceReadback.Length < safeCount)
            {
                visibleInstanceReadback = new uint[safeCount];
            }
            else
            {
                Array.Clear(visibleInstanceReadback, 0, visibleInstanceReadback.Length);
            }

            return visibleInstanceReadback;
        }

        private VisibleClusterRecord[] EnsureVisibleClusterReadback(int count)
        {
            var safeCount = Math.Max(1, count);
            if (visibleClusterReadback == null || visibleClusterReadback.Length < safeCount)
            {
                visibleClusterReadback = new VisibleClusterRecord[safeCount];
            }
            else
            {
                Array.Clear(visibleClusterReadback, 0, visibleClusterReadback.Length);
            }

            return visibleClusterReadback;
        }

        private static Vector4[] BuildFrustumPlaneVectorArray(Camera camera)
        {
            var planes = GeometryUtility.CalculateFrustumPlanes(camera);
            var packedPlanes = new Vector4[planes.Length];
            for (var i = 0; i < planes.Length; i++)
            {
                var plane = planes[i];
                var normal = plane.normal;
                var magnitude = normal.magnitude;
                if (magnitude > 0.0001f)
                {
                    normal /= magnitude;
                    packedPlanes[i] = new Vector4(normal.x, normal.y, normal.z, plane.distance / magnitude);
                }
                else
                {
                    packedPlanes[i] = new Vector4(0f, 0f, 0f, 0f);
                }
            }

            return packedPlanes;
        }

        private static GraphicsBuffer CreateStructuredBuffer<T>(int count, int stride, GraphicsBuffer.Target target, T[] data) where T : struct
        {
            var safeCount = Math.Max(1, count);
            var buffer = new GraphicsBuffer(target, safeCount, stride);
            if (data != null && data.Length > 0)
            {
                buffer.SetData(data);
            }

            return buffer;
        }

        private static uint[] ConvertBytesToUInt32(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return Array.Empty<uint>();
            }

            var wordCount = (bytes.Length + sizeof(uint) - 1) / sizeof(uint);
            var words = new uint[wordCount];
            for (var i = 0; i < bytes.Length; i++)
            {
                var shift = (i & 3) * 8;
                words[i >> 2] |= (uint)bytes[i] << shift;
            }

            return words;
        }

        private static ClusterRecordGpu[] BuildClusterGpuData(NanoMeshClusterRecord[] clusters)
        {
            if (clusters == null || clusters.Length == 0)
            {
                return Array.Empty<ClusterRecordGpu>();
            }

            var result = new ClusterRecordGpu[clusters.Length];
            for (var i = 0; i < clusters.Length; i++)
            {
                var source = clusters[i];
                result[i] = new ClusterRecordGpu
                {
                    vertexDataOffsetBytes = source.vertexDataOffsetBytes,
                    vertexCount = source.vertexCount,
                    indexDataOffsetBytes = source.indexDataOffsetBytes,
                    indexCount = source.indexCount,
                    materialRangeIndex = source.materialRangeIndex,
                    hierarchyNodeIndex = source.hierarchyNodeIndex,
                    hierarchyLevel = source.hierarchyLevel,
                    geometricError = source.geometricError,
                    positionOrigin = source.positionOrigin,
                    positionExtent = source.positionExtent
                };
            }

            return result;
        }

        private static ClusterCullRecordGpu[] BuildClusterCullGpuData(NanoMeshClusterCullRecord[] clusterCullData)
        {
            if (clusterCullData == null || clusterCullData.Length == 0)
            {
                return Array.Empty<ClusterCullRecordGpu>();
            }

            var result = new ClusterCullRecordGpu[clusterCullData.Length];
            for (var i = 0; i < clusterCullData.Length; i++)
            {
                var source = clusterCullData[i];
                result[i] = new ClusterCullRecordGpu
                {
                    localBoundsCenter = source.localBounds.center,
                    localBoundsExtents = source.localBounds.extents,
                    geometricError = source.geometricError,
                    boundingSphere = source.boundingSphere,
                    normalCone = source.normalCone
                };
            }

            return result;
        }

        private static HierarchyNodeGpu[] BuildHierarchyNodeGpuData(NanoMeshHierarchyNode[] hierarchyNodes)
        {
            if (hierarchyNodes == null || hierarchyNodes.Length == 0)
            {
                return Array.Empty<HierarchyNodeGpu>();
            }

            var result = new HierarchyNodeGpu[hierarchyNodes.Length];
            for (var i = 0; i < hierarchyNodes.Length; i++)
            {
                var source = hierarchyNodes[i];
                var firstClusterIndex = source.firstClusterIndex;
                var renderClusterCount = source.renderClusterCount;
                if (renderClusterCount <= 0 && source.clusterIndex >= 0)
                {
                    firstClusterIndex = source.clusterIndex;
                    renderClusterCount = 1;
                }

                result[i] = new HierarchyNodeGpu
                {
                    clusterIndex = source.clusterIndex,
                    firstClusterIndex = firstClusterIndex,
                    renderClusterCount = renderClusterCount,
                    parentNodeIndex = source.parentNodeIndex,
                    firstChildNodeIndex = source.firstChildNodeIndex,
                    childCount = source.childCount,
                    hierarchyLevel = source.hierarchyLevel,
                    isLeaf = source.isLeaf ? 1 : 0,
                    localBoundsCenter = source.localBounds.center,
                    localBoundsExtents = source.localBounds.extents,
                    geometricError = source.geometricError,
                    parentError = !float.IsNaN(source.parentError) && !float.IsInfinity(source.parentError) ? source.parentError : float.MaxValue,
                    boundingSphere = source.boundingSphere,
                    normalCone = source.normalCone
                };
            }

            return result;
        }

        private static Bounds TransformBounds(Matrix4x4 matrix, Bounds bounds)
        {
            var center = matrix.MultiplyPoint3x4(bounds.center);
            var extents = bounds.extents;
            var axisX = matrix.MultiplyVector(new Vector3(extents.x, 0f, 0f));
            var axisY = matrix.MultiplyVector(new Vector3(0f, extents.y, 0f));
            var axisZ = matrix.MultiplyVector(new Vector3(0f, 0f, extents.z));
            extents.x = Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x);
            extents.y = Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y);
            extents.z = Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z);
            return new Bounds(center, extents * 2f);
        }

        private static long ComputeBufferBytes(GraphicsBuffer buffer)
        {
            if (buffer == null)
            {
                return 0L;
            }

            return (long)buffer.count * buffer.stride;
        }

        private static NanoMeshManager ResolveActiveManager()
        {
            if (s_activeManager != null)
            {
                return s_activeManager;
            }

            s_activeManager = FindFirstObjectByType<NanoMeshManager>(FindObjectsInactive.Exclude);
            return s_activeManager;
        }
    }
}
