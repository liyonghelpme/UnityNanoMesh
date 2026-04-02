using System;
using UnityEngine;

namespace NanoMesh
{
    [Serializable]
    public struct NanoMeshClusterRecord
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
        public Vector3 positionExtent;
    }

    [Serializable]
    public struct NanoMeshClusterCullRecord
    {
        public Bounds localBounds;
        public Vector4 boundingSphere;
        public Vector4 normalCone;
        public float geometricError;
    }

    [Serializable]
    public struct NanoMeshHierarchyNode
    {
        public int clusterIndex;
        public int firstClusterIndex;
        public int renderClusterCount;
        public int parentNodeIndex;
        public int firstChildNodeIndex;
        public int childCount;
        public int hierarchyLevel;
        public Bounds localBounds;
        public Vector4 boundingSphere;
        public Vector4 normalCone;
        public float geometricError;
        public float parentError;
        public bool isLeaf;
    }

    [Serializable]
    public struct NanoMeshSubmeshMaterialRange
    {
        public int submeshIndex;
        public int materialSlot;
        public int firstClusterIndex;
        public int clusterCount;
        public int firstIndexOffsetBytes;
        public int indexByteCount;
    }

    [Serializable]
    public struct NanoMeshBakeWarning
    {
        public string code;
        public string message;
    }

    [CreateAssetMenu(menuName = "NanoMesh/NanoMesh Asset", fileName = "NanoMeshAsset")]
    public sealed class NanoMeshAsset : ScriptableObject
    {
        public const int CurrentVersion = 2;

        public int version = CurrentVersion;
        public Mesh sourceMesh;
        public string sourceMeshAssetPath;
        public Bounds assetBounds;
        public Vector2 uvMin;
        public Vector2 uvMax;
        public int sourceVertexCount;
        public int sourceTriangleCount;
        public int leafClusterCount;
        public int clusterCount;
        public int hierarchyNodeCount;
        public int coarseRootCount;
        public int hierarchyDepth;
        public int packedVertexStrideBytes = 16;
        public int packedIndexStrideBytes = 2;
        public int packedVertexDataSizeBytes;
        public int packedIndexDataSizeBytes;
        public int droppedDegenerateTriangleCount;
        public NanoMeshClusterRecord[] clusters = Array.Empty<NanoMeshClusterRecord>();
        public NanoMeshClusterCullRecord[] clusterCullData = Array.Empty<NanoMeshClusterCullRecord>();
        public NanoMeshHierarchyNode[] hierarchyNodes = Array.Empty<NanoMeshHierarchyNode>();
        public int[] coarseRootNodeIndices = Array.Empty<int>();
        public NanoMeshSubmeshMaterialRange[] materialRanges = Array.Empty<NanoMeshSubmeshMaterialRange>();
        public byte[] packedVertexData = Array.Empty<byte>();
        public byte[] packedIndexData = Array.Empty<byte>();
        public NanoMeshBakeWarning[] bakeWarnings = Array.Empty<NanoMeshBakeWarning>();

        public bool IsRuntimeDataValid => TryGetRuntimeValidationError(out _);

        public long StaticGeometryBytes => Math.Max(0, packedVertexDataSizeBytes) + Math.Max(0, packedIndexDataSizeBytes);

        public long StaticMetadataBytes =>
            ComputeArrayBytes(clusters) +
            ComputeArrayBytes(clusterCullData) +
            ComputeArrayBytes(hierarchyNodes) +
            ComputeArrayBytes(coarseRootNodeIndices) +
            ComputeArrayBytes(materialRanges);

        public long EstimatedStaticGpuBytes => StaticGeometryBytes + StaticMetadataBytes;

        public bool TryGetRuntimeValidationError(out string error)
        {
            if (version != 1 && version != CurrentVersion)
            {
                error = "NanoMeshAsset version mismatch.";
                return false;
            }

            var effectiveLeafClusterCount = leafClusterCount > 0 ? leafClusterCount : clusterCount;
            if (effectiveLeafClusterCount <= 0 || effectiveLeafClusterCount > clusterCount)
            {
                error = "NanoMeshAsset leaf-cluster metadata is missing or mismatched.";
                return false;
            }

            if (clusterCount <= 0 || clusters == null || clusters.Length != clusterCount)
            {
                error = "NanoMeshAsset cluster metadata is missing or mismatched.";
                return false;
            }

            if (clusterCullData == null || clusterCullData.Length != clusterCount)
            {
                error = "NanoMeshAsset cluster cull metadata is missing or mismatched.";
                return false;
            }

            if (hierarchyNodeCount <= 0 || hierarchyNodes == null || hierarchyNodes.Length != hierarchyNodeCount)
            {
                error = "NanoMeshAsset hierarchy metadata is missing or mismatched.";
                return false;
            }

            if (coarseRootCount < 0 || coarseRootNodeIndices == null || coarseRootNodeIndices.Length != coarseRootCount)
            {
                error = "NanoMeshAsset coarse root metadata is missing or mismatched.";
                return false;
            }

            if (hierarchyNodes != null)
            {
                for (var i = 0; i < hierarchyNodes.Length; i++)
                {
                    var node = hierarchyNodes[i];
                    var firstClusterIndex = node.firstClusterIndex;
                    var renderClusterCount = node.renderClusterCount;
                    if (version == 1)
                    {
                        if (node.clusterIndex >= 0)
                        {
                            firstClusterIndex = node.clusterIndex;
                            renderClusterCount = 1;
                        }
                        else
                        {
                            firstClusterIndex = -1;
                            renderClusterCount = 0;
                        }
                    }

                    if (renderClusterCount < 0)
                    {
                        error = "NanoMeshAsset hierarchy metadata contains a negative render-cluster count.";
                        return false;
                    }

                    if (renderClusterCount == 0)
                    {
                        continue;
                    }

                    if (firstClusterIndex < 0 || firstClusterIndex + renderClusterCount > clusterCount)
                    {
                        error = "NanoMeshAsset hierarchy metadata references missing render clusters.";
                        return false;
                    }
                }
            }

            if (packedVertexStrideBytes <= 0 || packedIndexStrideBytes <= 0)
            {
                error = "NanoMeshAsset packed buffer stride is invalid.";
                return false;
            }

            if (packedVertexData == null || packedVertexData.Length != packedVertexDataSizeBytes)
            {
                error = "NanoMeshAsset packed vertex payload is missing or mismatched.";
                return false;
            }

            if (packedIndexData == null || packedIndexData.Length != packedIndexDataSizeBytes)
            {
                error = "NanoMeshAsset packed index payload is missing or mismatched.";
                return false;
            }

            error = null;
            return true;
        }

        private static long ComputeArrayBytes<T>(T[] array) where T : struct
        {
            if (array == null)
            {
                return 0L;
            }

            return (long)array.Length * System.Runtime.InteropServices.Marshal.SizeOf<T>();
        }
    }
}
