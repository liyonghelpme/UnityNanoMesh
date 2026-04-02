using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace NanoMesh.Editor
{
    public enum NanoMeshHierarchyPartitionMode
    {
        Contiguous = 0,
        MetisAdjacency = 1
    }

    public enum NanoMeshHierarchyAdjacencyMode
    {
        SharedEdge = 0,
        SharedVertex = 1
    }

    [Serializable]
    public struct NanoMeshBakeOptions
    {
        public int maxTrianglesPerCluster;
        public int maxChildrenPerParent;
        public int targetRootCount;
        public NanoMeshHierarchyPartitionMode hierarchyPartitionMode;
        public NanoMeshHierarchyAdjacencyMode hierarchyAdjacencyMode;

        public static NanoMeshBakeOptions Default => new NanoMeshBakeOptions
        {
            maxTrianglesPerCluster = 64,
            maxChildrenPerParent = 4,
            targetRootCount = 4,
            hierarchyPartitionMode = NanoMeshHierarchyPartitionMode.MetisAdjacency,
            hierarchyAdjacencyMode = NanoMeshHierarchyAdjacencyMode.SharedEdge
        };
    }

    public sealed class NanoMeshBakeResult
    {
        public bool success;
        public NanoMeshAsset asset;
        public string assetPath;
        public string message;
        public string backendName;
        public bool usedFallback;
        public string nativeExecutablePath;
        public readonly List<NanoMeshBakeWarning> warnings = new List<NanoMeshBakeWarning>();
    }

    public interface INanoMeshBakeBackend
    {
        NanoMeshBakeResult Bake(Mesh mesh, NanoMeshBakeOptions options);
    }

    public static class NanoMeshBaker
    {
        public static Func<NanoMeshSettings> SettingsProviderOverride;
        private static readonly INanoMeshBakeBackend ManagedBackend = new UnityNanoMeshBakeBackend();

        internal struct PreparedMeshData
        {
            public Mesh mesh;
            public Vector3[] positions;
            public Vector3[] normals;
            public Vector2[] uv0;
            public Bounds assetBounds;
            public Vector2 uvMin;
            public Vector2 uvMax;
            public List<PreparedSubmesh> submeshes;
            public List<NanoMeshBakeWarning> warnings;
        }

        internal struct PreparedSubmesh
        {
            public int submeshIndex;
            public int[] indices;
        }

        public static NanoMeshBakeResult BakeMesh(Mesh mesh, NanoMeshBakeOptions options = default)
        {
            if (options.maxTrianglesPerCluster <= 0)
            {
                options = NanoMeshBakeOptions.Default;
            }

            var settings = SettingsProviderOverride != null
                ? SettingsProviderOverride()
                : NanoMeshSettingsUtility.GetOrCreateSettings();
            var preferredBackend = settings != null ? settings.preferredBackend : NanoMeshBakeBackendKind.Managed;
            if (preferredBackend != NanoMeshBakeBackendKind.MeshoptimizerCli)
            {
                var managedResult = ManagedBackend.Bake(mesh, options);
                managedResult.backendName = NanoMeshBakeBackendKind.Managed.ToString();
                return managedResult;
            }

            var cliBackend = new MeshoptimizerCliNanoMeshBakeBackend(settings);
            var cliResult = cliBackend.Bake(mesh, options);
            if (cliResult.success || settings == null || !settings.fallbackToManagedBackend)
            {
                if (string.IsNullOrEmpty(cliResult.backendName))
                {
                    cliResult.backendName = NanoMeshBakeBackendKind.MeshoptimizerCli.ToString();
                }

                return cliResult;
            }

            var fallbackResult = ManagedBackend.Bake(mesh, options);
            if (!fallbackResult.success)
            {
                return cliResult;
            }

            fallbackResult.backendName = NanoMeshBakeBackendKind.Managed.ToString();
            fallbackResult.usedFallback = true;
            fallbackResult.nativeExecutablePath = cliResult.nativeExecutablePath;
            var fallbackWarning = new NanoMeshBakeWarning
            {
                code = "MeshoptimizerCliFallback",
                message = "meshoptimizer CLI bake failed and the baker fell back to the managed backend. " + cliResult.message
            };
            fallbackResult.warnings.Insert(0, fallbackWarning);
            if (fallbackResult.asset != null)
            {
                var existingWarnings = fallbackResult.asset.bakeWarnings ?? Array.Empty<NanoMeshBakeWarning>();
                var mergedWarnings = new NanoMeshBakeWarning[existingWarnings.Length + 1];
                mergedWarnings[0] = fallbackWarning;
                Array.Copy(existingWarnings, 0, mergedWarnings, 1, existingWarnings.Length);
                fallbackResult.asset.bakeWarnings = mergedWarnings;
            }

            fallbackResult.message = "Fell back to managed backend after meshoptimizer CLI failure. " + fallbackResult.message;
            return fallbackResult;
        }

        public static NanoMeshBakeResult BakeMeshToAsset(Mesh mesh, string assetPath, NanoMeshBakeOptions options = default)
        {
            var result = BakeMesh(mesh, options);
            if (!result.success)
            {
                return result;
            }

            if (string.IsNullOrWhiteSpace(assetPath))
            {
                CleanupTransientAsset(result.asset);
                result.success = false;
                result.message = "Choose a valid asset path.";
                return result;
            }

            var existingAsset = AssetDatabase.LoadAssetAtPath<NanoMeshAsset>(assetPath);
            if (existingAsset == null)
            {
                AssetDatabase.CreateAsset(result.asset, assetPath);
                existingAsset = result.asset;
            }
            else
            {
                CopyAssetData(result.asset, existingAsset);
                CleanupTransientAsset(result.asset);
            }

            result.asset = existingAsset;
            result.assetPath = assetPath;
            EditorUtility.SetDirty(existingAsset);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(assetPath);
            return result;
        }

        public static NanoMeshBakeResult RebakeAsset(NanoMeshAsset asset, NanoMeshBakeOptions options = default)
        {
            if (asset == null || asset.sourceMesh == null)
            {
                return new NanoMeshBakeResult
                {
                    success = false,
                    message = "Assign a source mesh before re-baking."
                };
            }

            var assetPath = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return new NanoMeshBakeResult
                {
                    success = false,
                    message = "The selected NanoMeshAsset is not saved on disk."
                };
            }

            return BakeMeshToAsset(asset.sourceMesh, assetPath, options);
        }

        internal static bool TryPrepareMeshForBake(Mesh mesh, out PreparedMeshData prepared, out string error)
        {
            prepared = default;
            error = null;
            if (mesh == null)
            {
                error = "Select a Mesh asset or a GameObject with a MeshFilter.";
                return false;
            }

            if (!mesh.isReadable)
            {
                error = "Mesh " + mesh.name + " is not readable.";
                return false;
            }

            if (mesh.vertexCount <= 0)
            {
                error = "Mesh " + mesh.name + " has no vertices.";
                return false;
            }

            var positions = mesh.vertices;
            var normals = mesh.normals;
            if (positions == null || positions.Length == 0)
            {
                error = "Mesh " + mesh.name + " is missing positions.";
                return false;
            }

            if (normals == null || normals.Length != positions.Length)
            {
                error = "Mesh " + mesh.name + " must contain normals for NanoMesh v1.";
                return false;
            }

            var uv0 = mesh.uv;
            var warnings = new List<NanoMeshBakeWarning>();
            if (uv0 == null || uv0.Length != positions.Length)
            {
                uv0 = new Vector2[positions.Length];
                warnings.Add(new NanoMeshBakeWarning
                {
                    code = "GeneratedZeroUv0",
                    message = "Mesh " + mesh.name + " was missing UV0. NanoMesh baked it with zero UV coordinates."
                });
            }

            var submeshCount = mesh.subMeshCount;
            if (submeshCount <= 0)
            {
                error = "Mesh " + mesh.name + " has no submeshes.";
                return false;
            }

            var submeshes = new List<PreparedSubmesh>(submeshCount);
            for (var submeshIndex = 0; submeshIndex < submeshCount; submeshIndex++)
            {
                if (mesh.GetTopology(submeshIndex) != MeshTopology.Triangles)
                {
                    error = "Mesh " + mesh.name + " uses unsupported topology in submesh " + submeshIndex + ". NanoMesh v1 only supports triangles.";
                    return false;
                }

                var indices = mesh.GetIndices(submeshIndex);
                if (indices == null || indices.Length == 0)
                {
                    continue;
                }

                submeshes.Add(new PreparedSubmesh
                {
                    submeshIndex = submeshIndex,
                    indices = indices
                });
            }

            if (submeshes.Count == 0)
            {
                error = "Mesh " + mesh.name + " has no triangle indices to bake.";
                return false;
            }

            var assetBounds = new Bounds(positions[0], Vector3.zero);
            var uvMin = uv0[0];
            var uvMax = uv0[0];
            for (var i = 0; i < positions.Length; i++)
            {
                assetBounds.Encapsulate(positions[i]);
                uvMin = Vector2.Min(uvMin, uv0[i]);
                uvMax = Vector2.Max(uvMax, uv0[i]);
            }

            prepared = new PreparedMeshData
            {
                mesh = mesh,
                positions = positions,
                normals = normals,
                uv0 = uv0,
                assetBounds = assetBounds,
                uvMin = uvMin,
                uvMax = uvMax,
                submeshes = submeshes,
                warnings = warnings
            };
            return true;
        }

        private static void CopyAssetData(NanoMeshAsset source, NanoMeshAsset destination)
        {
            destination.version = source.version;
            destination.sourceMesh = source.sourceMesh;
            destination.sourceMeshAssetPath = source.sourceMeshAssetPath;
            destination.assetBounds = source.assetBounds;
            destination.uvMin = source.uvMin;
            destination.uvMax = source.uvMax;
            destination.sourceVertexCount = source.sourceVertexCount;
            destination.sourceTriangleCount = source.sourceTriangleCount;
            destination.clusterCount = source.clusterCount;
            destination.hierarchyNodeCount = source.hierarchyNodeCount;
            destination.coarseRootCount = source.coarseRootCount;
            destination.hierarchyDepth = source.hierarchyDepth;
            destination.packedVertexStrideBytes = source.packedVertexStrideBytes;
            destination.packedIndexStrideBytes = source.packedIndexStrideBytes;
            destination.packedVertexDataSizeBytes = source.packedVertexDataSizeBytes;
            destination.packedIndexDataSizeBytes = source.packedIndexDataSizeBytes;
            destination.droppedDegenerateTriangleCount = source.droppedDegenerateTriangleCount;
            destination.clusters = source.clusters;
            destination.clusterCullData = source.clusterCullData;
            destination.hierarchyNodes = source.hierarchyNodes;
            destination.coarseRootNodeIndices = source.coarseRootNodeIndices;
            destination.materialRanges = source.materialRanges;
            destination.packedVertexData = source.packedVertexData;
            destination.packedIndexData = source.packedIndexData;
            destination.bakeWarnings = source.bakeWarnings;
        }

        private static void CleanupTransientAsset(NanoMeshAsset asset)
        {
            if (asset != null)
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        private sealed class UnityNanoMeshBakeBackend : INanoMeshBakeBackend
        {
            private const float DegenerateAreaEpsilon = 1e-12f;

            public NanoMeshBakeResult Bake(Mesh mesh, NanoMeshBakeOptions options)
            {
                var result = new NanoMeshBakeResult();
                if (!TryNormalizeMesh(mesh, result, out var normalized))
                {
                    return result;
                }

                if (normalized.warnings != null && normalized.warnings.Count > 0)
                {
                    result.warnings.AddRange(normalized.warnings);
                }

                options.maxTrianglesPerCluster = Mathf.Max(1, options.maxTrianglesPerCluster);
                options.maxChildrenPerParent = Mathf.Max(2, options.maxChildrenPerParent);
                options.targetRootCount = Mathf.Max(1, options.targetRootCount);

                var leafClusters = BuildLeafClusters(normalized, options, result.warnings, out var droppedDegenerateTriangleCount);
                if (leafClusters.Count == 0)
                {
                    result.success = false;
                    result.message = droppedDegenerateTriangleCount > 0
                        ? "No valid triangles remained after removing degenerate triangles."
                        : "The mesh did not produce any bakeable clusters.";
                    return result;
                }

                var hierarchyNodes = BuildHierarchy(
                    leafClusters,
                    options,
                    result.warnings,
                    out var renderClusters,
                    out var coarseRoots,
                    out var hierarchyDepth);
                var asset = ScriptableObject.CreateInstance<NanoMeshAsset>();
                PopulateAsset(asset, normalized, leafClusters, renderClusters, hierarchyNodes, coarseRoots, hierarchyDepth, droppedDegenerateTriangleCount, result.warnings);

                result.success = true;
                result.asset = asset;
                result.message = "Baked " + mesh.name + " into " + asset.clusterCount + " clusters.";
                return result;
            }

            private static bool TryNormalizeMesh(Mesh mesh, NanoMeshBakeResult result, out NormalizedMesh normalized)
            {
                normalized = default;
                if (!TryPrepareMeshForBake(mesh, out var prepared, out var error))
                {
                    result.success = false;
                    result.message = error;
                    return false;
                }

                var submeshes = new List<NormalizedSubmesh>(prepared.submeshes.Count);
                for (var i = 0; i < prepared.submeshes.Count; i++)
                {
                    submeshes.Add(new NormalizedSubmesh
                    {
                        submeshIndex = prepared.submeshes[i].submeshIndex,
                        indices = prepared.submeshes[i].indices
                    });
                }

                normalized = new NormalizedMesh
                {
                    mesh = prepared.mesh,
                    positions = prepared.positions,
                    normals = prepared.normals,
                    uv0 = prepared.uv0,
                    assetBounds = prepared.assetBounds,
                    uvMin = prepared.uvMin,
                    uvMax = prepared.uvMax,
                    submeshes = submeshes,
                    warnings = prepared.warnings
                };
                return true;
            }

            private static List<LeafClusterBuildData> BuildLeafClusters(
                NormalizedMesh normalized,
                NanoMeshBakeOptions options,
                List<NanoMeshBakeWarning> warnings,
                out int droppedDegenerateTriangleCount)
            {
                droppedDegenerateTriangleCount = 0;
                var leafClusters = new List<LeafClusterBuildData>();

                foreach (var submesh in normalized.submeshes)
                {
                    var indices = submesh.indices;
                    var triangleStart = 0;
                    while (triangleStart < indices.Length)
                    {
                        var triangleCount = Mathf.Min(options.maxTrianglesPerCluster, (indices.Length - triangleStart) / 3);
                        var end = triangleStart + triangleCount * 3;
                        var vertexLookup = new Dictionary<int, ushort>();
                        var localVertices = new List<ClusterVertex>();
                        var localIndices = new List<ushort>(triangleCount * 3);

                        for (var indexCursor = triangleStart; indexCursor < end; indexCursor += 3)
                        {
                            var a = indices[indexCursor];
                            var b = indices[indexCursor + 1];
                            var c = indices[indexCursor + 2];

                            if (a == b || b == c || a == c)
                            {
                                droppedDegenerateTriangleCount++;
                                continue;
                            }

                            var pa = normalized.positions[a];
                            var pb = normalized.positions[b];
                            var pc = normalized.positions[c];
                            var area = Vector3.Cross(pb - pa, pc - pa).sqrMagnitude;
                            if (area <= DegenerateAreaEpsilon)
                            {
                                droppedDegenerateTriangleCount++;
                                continue;
                            }

                            localIndices.Add(GetOrAddVertex(a, normalized, vertexLookup, localVertices));
                            localIndices.Add(GetOrAddVertex(b, normalized, vertexLookup, localVertices));
                            localIndices.Add(GetOrAddVertex(c, normalized, vertexLookup, localVertices));
                        }

                        if (localIndices.Count > 0)
                        {
                            var bounds = new Bounds(localVertices[0].position, Vector3.zero);
                            var averageNormal = Vector3.zero;
                            for (var i = 0; i < localVertices.Count; i++)
                            {
                                bounds.Encapsulate(localVertices[i].position);
                                averageNormal += localVertices[i].normal;
                            }

                            averageNormal = averageNormal.sqrMagnitude > 0f ? averageNormal.normalized : Vector3.forward;
                            var sphere = new Vector4(bounds.center.x, bounds.center.y, bounds.center.z, bounds.extents.magnitude);
                            var cone = ComputeNormalCone(localVertices, averageNormal);
                            var triangleCountInCluster = localIndices.Count / 3;
                            var error = bounds.extents.magnitude / Mathf.Max(1f, Mathf.Sqrt(triangleCountInCluster));
                            leafClusters.Add(new LeafClusterBuildData
                            {
                                submeshIndex = submesh.submeshIndex,
                                materialRangeIndex = -1,
                                vertices = localVertices,
                                indices = localIndices,
                                bounds = bounds,
                                boundingSphere = sphere,
                                normalCone = cone,
                                geometricError = error
                            });
                        }

                        triangleStart = end;
                    }
                }

                if (droppedDegenerateTriangleCount > 0)
                {
                    warnings.Add(new NanoMeshBakeWarning
                    {
                        code = "DegenerateTrianglesDropped",
                        message = "Dropped " + droppedDegenerateTriangleCount + " degenerate source triangles during bake."
                    });
                }

                return leafClusters;
            }

            private static ushort GetOrAddVertex(
                int sourceVertexIndex,
                NormalizedMesh normalized,
                Dictionary<int, ushort> lookup,
                List<ClusterVertex> localVertices)
            {
                if (lookup.TryGetValue(sourceVertexIndex, out var localIndex))
                {
                    return localIndex;
                }

                localIndex = checked((ushort)localVertices.Count);
                lookup.Add(sourceVertexIndex, localIndex);
                localVertices.Add(new ClusterVertex
                {
                    position = normalized.positions[sourceVertexIndex],
                    normal = normalized.normals[sourceVertexIndex].normalized,
                    uv = normalized.uv0[sourceVertexIndex]
                });
                return localIndex;
            }

            private static Vector4 ComputeNormalCone(List<ClusterVertex> vertices, Vector3 averageNormal)
            {
                var minDot = 1f;
                for (var i = 0; i < vertices.Count; i++)
                {
                    minDot = Mathf.Min(minDot, Vector3.Dot(averageNormal, vertices[i].normal.normalized));
                }

                return new Vector4(averageNormal.x, averageNormal.y, averageNormal.z, Mathf.Clamp(minDot, -1f, 1f));
            }

            private static List<NanoMeshHierarchyNode> BuildHierarchy(
                List<LeafClusterBuildData> leafClusters,
                NanoMeshBakeOptions options,
                List<NanoMeshBakeWarning> warnings,
                out List<LeafClusterBuildData> renderClusters,
                out int[] coarseRoots,
                out int hierarchyDepth)
            {
                var buildNodes = new List<HierarchyBuildNode>(leafClusters.Count * 2);
                var currentLevel = new List<int>(leafClusters.Count);
                for (var i = 0; i < leafClusters.Count; i++)
                {
                    buildNodes.Add(new HierarchyBuildNode
                    {
                        parentNodeIndex = -1,
                        firstChildNodeIndex = -1,
                        childCount = 0,
                        hierarchyLevel = 0,
                        localBounds = leafClusters[i].bounds,
                        boundingSphere = leafClusters[i].boundingSphere,
                        normalCone = leafClusters[i].normalCone,
                        geometricError = leafClusters[i].geometricError,
                        parentError = float.PositiveInfinity,
                        descendantLeafClusterIndices = new List<int> { i },
                        isLeaf = true
                    });
                    currentLevel.Add(i);
                }

                hierarchyDepth = 1;
                while (currentLevel.Count > options.targetRootCount)
                {
                    var nextLevel = new List<int>(Mathf.CeilToInt(currentLevel.Count / (float)options.maxChildrenPerParent));
                    for (var groupStart = 0; groupStart < currentLevel.Count; groupStart += options.maxChildrenPerParent)
                    {
                        var childCount = Mathf.Min(options.maxChildrenPerParent, currentLevel.Count - groupStart);
                        var firstChildNodeIndex = currentLevel[groupStart];
                        var parentBounds = buildNodes[firstChildNodeIndex].localBounds;
                        var parentError = buildNodes[firstChildNodeIndex].geometricError;
                        var accumulatedNormal = Vector3.zero;
                        var descendantLeafClusterIndices = new List<int>();

                        for (var offset = 0; offset < childCount; offset++)
                        {
                            var childNodeIndex = currentLevel[groupStart + offset];
                            var childNode = buildNodes[childNodeIndex];
                            if (offset > 0)
                            {
                                parentBounds.Encapsulate(childNode.localBounds.min);
                                parentBounds.Encapsulate(childNode.localBounds.max);
                            }

                            parentError = Mathf.Max(parentError, childNode.geometricError);
                            accumulatedNormal += new Vector3(childNode.normalCone.x, childNode.normalCone.y, childNode.normalCone.z);
                            descendantLeafClusterIndices.AddRange(childNode.descendantLeafClusterIndices);
                        }

                        var hierarchyLevel = buildNodes[firstChildNodeIndex].hierarchyLevel + 1;
                        hierarchyDepth = Mathf.Max(hierarchyDepth, hierarchyLevel + 1);
                        var averageNormal = accumulatedNormal.sqrMagnitude > 0f ? accumulatedNormal.normalized : Vector3.forward;
                        var normalCone = new Vector4(averageNormal.x, averageNormal.y, averageNormal.z, -1f);
                        parentError += parentBounds.extents.magnitude * 0.5f * hierarchyLevel;
                        var parentNodeIndex = buildNodes.Count;
                        buildNodes.Add(new HierarchyBuildNode
                        {
                            parentNodeIndex = -1,
                            firstChildNodeIndex = firstChildNodeIndex,
                            childCount = childCount,
                            hierarchyLevel = hierarchyLevel,
                            localBounds = parentBounds,
                            boundingSphere = new Vector4(parentBounds.center.x, parentBounds.center.y, parentBounds.center.z, parentBounds.extents.magnitude),
                            normalCone = normalCone,
                            geometricError = parentError,
                            parentError = float.PositiveInfinity,
                            descendantLeafClusterIndices = descendantLeafClusterIndices,
                            isLeaf = false
                        });

                        for (var offset = 0; offset < childCount; offset++)
                        {
                            var childNodeIndex = currentLevel[groupStart + offset];
                            var childNode = buildNodes[childNodeIndex];
                            childNode.parentNodeIndex = parentNodeIndex;
                            childNode.parentError = parentError;
                            buildNodes[childNodeIndex] = childNode;
                        }

                        nextLevel.Add(parentNodeIndex);
                    }

                    if (nextLevel.Count >= currentLevel.Count)
                    {
                        warnings.Add(new NanoMeshBakeWarning
                        {
                            code = "HierarchyReductionStalled",
                            message = "Hierarchy simplification stalled before reaching the target root count."
                        });
                        break;
                    }

                    currentLevel = nextLevel;
                }

                coarseRoots = currentLevel.ToArray();
                if (coarseRoots.Length > 1)
                {
                    warnings.Add(new NanoMeshBakeWarning
                    {
                        code = "MultipleCoarseRoots",
                        message = "Bake finished with " + coarseRoots.Length + " coarse roots."
                    });
                }

                if (coarseRoots.Length == leafClusters.Count && leafClusters.Count > 1)
                {
                    warnings.Add(new NanoMeshBakeWarning
                    {
                        code = "LeafOnlyHierarchy",
                        message = "Hierarchy stayed at the leaf level. Coarse traversal cannot render connected parent geometry for this asset."
                    });
                    warnings.Add(new NanoMeshBakeWarning
                    {
                        code = "PoorHierarchyReduction",
                        message = "Hierarchy reduction did not merge any leaf clusters for this asset."
                    });
                }

                renderClusters = new List<LeafClusterBuildData>(leafClusters.Count * Mathf.Max(2, hierarchyDepth));
                var hierarchyNodes = new List<NanoMeshHierarchyNode>(buildNodes.Count);
                for (var nodeIndex = 0; nodeIndex < buildNodes.Count; nodeIndex++)
                {
                    hierarchyNodes.Add(new NanoMeshHierarchyNode
                    {
                        clusterIndex = -1,
                        firstClusterIndex = -1,
                        renderClusterCount = 0,
                        parentNodeIndex = buildNodes[nodeIndex].parentNodeIndex,
                        firstChildNodeIndex = buildNodes[nodeIndex].firstChildNodeIndex,
                        childCount = buildNodes[nodeIndex].childCount,
                        hierarchyLevel = buildNodes[nodeIndex].hierarchyLevel,
                        localBounds = buildNodes[nodeIndex].localBounds,
                        boundingSphere = buildNodes[nodeIndex].boundingSphere,
                        normalCone = buildNodes[nodeIndex].normalCone,
                        geometricError = buildNodes[nodeIndex].geometricError,
                        parentError = buildNodes[nodeIndex].parentError,
                        isLeaf = buildNodes[nodeIndex].isLeaf
                    });
                }

                for (var nodeIndex = 0; nodeIndex < buildNodes.Count; nodeIndex++)
                {
                    var node = hierarchyNodes[nodeIndex];
                    node.firstClusterIndex = renderClusters.Count;
                    if (buildNodes[nodeIndex].isLeaf)
                    {
                        var renderCluster = leafClusters[nodeIndex];
                        renderCluster.hierarchyNodeIndex = nodeIndex;
                        renderCluster.hierarchyLevel = node.hierarchyLevel;
                        renderClusters.Add(renderCluster);
                    }
                    else
                    {
                        AppendParentRenderClusters(
                            buildNodes[nodeIndex],
                            leafClusters,
                            nodeIndex,
                            node.hierarchyLevel,
                            node.geometricError,
                            renderClusters);
                    }

                    node.renderClusterCount = renderClusters.Count - node.firstClusterIndex;
                    node.clusterIndex = node.renderClusterCount > 0 ? node.firstClusterIndex : -1;
                    hierarchyNodes[nodeIndex] = node;
                }

                return hierarchyNodes;
            }

            private static void PopulateAsset(
                NanoMeshAsset asset,
                NormalizedMesh normalized,
                List<LeafClusterBuildData> leafClusters,
                List<LeafClusterBuildData> renderClusters,
                List<NanoMeshHierarchyNode> hierarchyNodes,
                int[] coarseRoots,
                int hierarchyDepth,
                int droppedDegenerateTriangleCount,
                List<NanoMeshBakeWarning> warnings)
            {
                var materialRanges = BuildMaterialRanges(leafClusters);
                using (var vertexStream = new MemoryStream())
                using (var indexStream = new MemoryStream())
                using (var vertexWriter = new BinaryWriter(vertexStream))
                using (var indexWriter = new BinaryWriter(indexStream))
                {
                    for (var i = 0; i < renderClusters.Count; i++)
                    {
                        var cluster = renderClusters[i];
                        cluster.materialRangeIndex = FindMaterialRangeIndex(materialRanges, cluster.submeshIndex);
                        cluster.vertexDataOffsetBytes = checked((int)vertexStream.Position);
                        cluster.indexDataOffsetBytes = checked((int)indexStream.Position);

                        WriteVertices(cluster, normalized.uvMin, normalized.uvMax, vertexWriter);
                        WriteIndices(cluster, indexWriter);

                        renderClusters[i] = cluster;
                    }

                    for (var rangeIndex = 0; rangeIndex < materialRanges.Count; rangeIndex++)
                    {
                        var range = materialRanges[rangeIndex];
                        if (range.firstClusterIndex >= 0 && range.firstClusterIndex < renderClusters.Count && range.clusterCount > 0)
                        {
                            range.firstIndexOffsetBytes = renderClusters[range.firstClusterIndex].indexDataOffsetBytes;
                            var lastCluster = renderClusters[Mathf.Min(renderClusters.Count - 1, range.firstClusterIndex + range.clusterCount - 1)];
                            range.indexByteCount = (lastCluster.indexDataOffsetBytes + lastCluster.indices.Count * sizeof(ushort)) - range.firstIndexOffsetBytes;
                        }
                        materialRanges[rangeIndex] = range;
                    }

                    asset.version = NanoMeshAsset.CurrentVersion;
                    asset.sourceMesh = normalized.mesh;
                    asset.sourceMeshAssetPath = AssetDatabase.GetAssetPath(normalized.mesh);
                    asset.assetBounds = normalized.assetBounds;
                    asset.uvMin = normalized.uvMin;
                    asset.uvMax = normalized.uvMax;
                    asset.sourceVertexCount = normalized.positions.Length;
                    asset.sourceTriangleCount = CountSourceTriangles(normalized.submeshes);
                    asset.leafClusterCount = leafClusters.Count;
                    asset.clusterCount = renderClusters.Count;
                    asset.hierarchyNodeCount = hierarchyNodes.Count;
                    asset.coarseRootCount = coarseRoots.Length;
                    asset.hierarchyDepth = hierarchyDepth;
                    asset.packedVertexStrideBytes = 16;
                    asset.packedIndexStrideBytes = 2;
                    asset.packedVertexData = vertexStream.ToArray();
                    asset.packedIndexData = indexStream.ToArray();
                    asset.packedVertexDataSizeBytes = asset.packedVertexData.Length;
                    asset.packedIndexDataSizeBytes = asset.packedIndexData.Length;
                    asset.droppedDegenerateTriangleCount = droppedDegenerateTriangleCount;
                    asset.clusters = new NanoMeshClusterRecord[renderClusters.Count];
                    asset.clusterCullData = new NanoMeshClusterCullRecord[renderClusters.Count];
                    asset.hierarchyNodes = hierarchyNodes.ToArray();
                    asset.coarseRootNodeIndices = coarseRoots;
                    asset.materialRanges = materialRanges.ToArray();
                    asset.bakeWarnings = warnings.ToArray();

                    for (var i = 0; i < renderClusters.Count; i++)
                    {
                        var cluster = renderClusters[i];
                        asset.clusters[i] = new NanoMeshClusterRecord
                        {
                            vertexDataOffsetBytes = cluster.vertexDataOffsetBytes,
                            vertexCount = cluster.vertices.Count,
                            indexDataOffsetBytes = cluster.indexDataOffsetBytes,
                            indexCount = cluster.indices.Count,
                            materialRangeIndex = cluster.materialRangeIndex,
                            hierarchyNodeIndex = cluster.hierarchyNodeIndex,
                            hierarchyLevel = cluster.hierarchyLevel,
                            geometricError = cluster.geometricError,
                            positionOrigin = cluster.bounds.min,
                            positionExtent = cluster.bounds.size
                        };
                        asset.clusterCullData[i] = new NanoMeshClusterCullRecord
                        {
                            localBounds = cluster.bounds,
                            boundingSphere = cluster.boundingSphere,
                            normalCone = cluster.normalCone,
                            geometricError = cluster.geometricError
                        };
                    }
                }
            }

            private static List<NanoMeshSubmeshMaterialRange> BuildMaterialRanges(List<LeafClusterBuildData> leafClusters)
            {
                var ranges = new List<NanoMeshSubmeshMaterialRange>();
                var submeshCounts = new Dictionary<int, int>();
                var submeshOrder = new List<int>();
                for (var i = 0; i < leafClusters.Count; i++)
                {
                    if (!submeshCounts.TryGetValue(leafClusters[i].submeshIndex, out var count))
                    {
                        submeshCounts.Add(leafClusters[i].submeshIndex, 1);
                        submeshOrder.Add(leafClusters[i].submeshIndex);
                    }
                    else
                    {
                        submeshCounts[leafClusters[i].submeshIndex] = count + 1;
                    }
                }

                if (submeshCounts.Count == 0)
                {
                    return ranges;
                }

                for (var orderIndex = 0; orderIndex < submeshOrder.Count; orderIndex++)
                {
                    var submeshIndex = submeshOrder[orderIndex];
                    ranges.Add(new NanoMeshSubmeshMaterialRange
                    {
                        submeshIndex = submeshIndex,
                        materialSlot = submeshIndex,
                        firstClusterIndex = FindFirstClusterIndex(leafClusters, submeshIndex),
                        clusterCount = submeshCounts[submeshIndex],
                        firstIndexOffsetBytes = 0,
                        indexByteCount = 0
                    });
                }

                return ranges;
            }

            private static int FindMaterialRangeIndex(List<NanoMeshSubmeshMaterialRange> ranges, int submeshIndex)
            {
                for (var i = 0; i < ranges.Count; i++)
                {
                    if (ranges[i].submeshIndex == submeshIndex)
                    {
                        return i;
                    }
                }

                return -1;
            }

            private static int FindFirstClusterIndex(List<LeafClusterBuildData> clusters, int submeshIndex)
            {
                for (var i = 0; i < clusters.Count; i++)
                {
                    if (clusters[i].submeshIndex == submeshIndex)
                    {
                        return i;
                    }
                }

                return -1;
            }

            private static void AppendParentRenderClusters(
                HierarchyBuildNode buildNode,
                List<LeafClusterBuildData> leafClusters,
                int hierarchyNodeIndex,
                int hierarchyLevel,
                float geometricError,
                List<LeafClusterBuildData> renderClusters)
            {
                var groupedLeafClusters = new Dictionary<int, List<LeafClusterBuildData>>();
                var submeshOrder = new List<int>();
                for (var i = 0; i < buildNode.descendantLeafClusterIndices.Count; i++)
                {
                    var leafCluster = leafClusters[buildNode.descendantLeafClusterIndices[i]];
                    if (!groupedLeafClusters.TryGetValue(leafCluster.submeshIndex, out var group))
                    {
                        group = new List<LeafClusterBuildData>();
                        groupedLeafClusters.Add(leafCluster.submeshIndex, group);
                        submeshOrder.Add(leafCluster.submeshIndex);
                    }

                    group.Add(leafCluster);
                }

                for (var submeshOrderIndex = 0; submeshOrderIndex < submeshOrder.Count; submeshOrderIndex++)
                {
                    var group = groupedLeafClusters[submeshOrder[submeshOrderIndex]];
                    var chunk = new List<LeafClusterBuildData>();
                    var vertexCount = 0;
                    for (var clusterIndex = 0; clusterIndex < group.Count; clusterIndex++)
                    {
                        var nextCluster = group[clusterIndex];
                        var wouldOverflow = chunk.Count > 0 && vertexCount + nextCluster.vertices.Count > ushort.MaxValue;
                        if (wouldOverflow)
                        {
                            renderClusters.Add(MergeClusters(chunk, hierarchyNodeIndex, hierarchyLevel, geometricError));
                            chunk.Clear();
                            vertexCount = 0;
                        }

                        chunk.Add(nextCluster);
                        vertexCount += nextCluster.vertices.Count;
                    }

                    if (chunk.Count > 0)
                    {
                        renderClusters.Add(MergeClusters(chunk, hierarchyNodeIndex, hierarchyLevel, geometricError));
                    }
                }
            }

            private static LeafClusterBuildData MergeClusters(
                List<LeafClusterBuildData> sourceClusters,
                int hierarchyNodeIndex,
                int hierarchyLevel,
                float geometricError)
            {
                var mergedVertices = new List<ClusterVertex>();
                var mergedIndices = new List<ushort>();
                var bounds = new Bounds();
                var boundsInitialized = false;
                var averageNormal = Vector3.zero;
                var submeshIndex = sourceClusters[0].submeshIndex;

                for (var i = 0; i < sourceClusters.Count; i++)
                {
                    var sourceCluster = sourceClusters[i];
                    var vertexOffset = mergedVertices.Count;
                    for (var vertexIndex = 0; vertexIndex < sourceCluster.vertices.Count; vertexIndex++)
                    {
                        var vertex = sourceCluster.vertices[vertexIndex];
                        mergedVertices.Add(vertex);
                        averageNormal += vertex.normal;
                        if (!boundsInitialized)
                        {
                            bounds = new Bounds(vertex.position, Vector3.zero);
                            boundsInitialized = true;
                        }
                        else
                        {
                            bounds.Encapsulate(vertex.position);
                        }
                    }

                    for (var indexIndex = 0; indexIndex < sourceCluster.indices.Count; indexIndex++)
                    {
                        mergedIndices.Add((ushort)(vertexOffset + sourceCluster.indices[indexIndex]));
                    }
                }

                averageNormal = averageNormal.sqrMagnitude > 0f ? averageNormal.normalized : Vector3.forward;
                return new LeafClusterBuildData
                {
                    submeshIndex = submeshIndex,
                    materialRangeIndex = -1,
                    vertices = mergedVertices,
                    indices = mergedIndices,
                    bounds = bounds,
                    boundingSphere = new Vector4(bounds.center.x, bounds.center.y, bounds.center.z, bounds.extents.magnitude),
                    normalCone = ComputeNormalCone(mergedVertices, averageNormal),
                    geometricError = geometricError,
                    hierarchyNodeIndex = hierarchyNodeIndex,
                    hierarchyLevel = hierarchyLevel
                };
            }

            private static void WriteVertices(LeafClusterBuildData cluster, Vector2 uvMin, Vector2 uvMax, BinaryWriter writer)
            {
                var positionOrigin = cluster.bounds.min;
                var positionExtent = cluster.bounds.size;
                var uvExtent = uvMax - uvMin;
                uvExtent.x = Mathf.Abs(uvExtent.x) < 1e-8f ? 1f : uvExtent.x;
                uvExtent.y = Mathf.Abs(uvExtent.y) < 1e-8f ? 1f : uvExtent.y;

                for (var i = 0; i < cluster.vertices.Count; i++)
                {
                    var vertex = cluster.vertices[i];
                    writer.Write(QuantizeUnorm16(vertex.position.x, positionOrigin.x, positionExtent.x));
                    writer.Write(QuantizeUnorm16(vertex.position.y, positionOrigin.y, positionExtent.y));
                    writer.Write(QuantizeUnorm16(vertex.position.z, positionOrigin.z, positionExtent.z));
                    writer.Write(QuantizeSnorm16(vertex.normal.x));
                    writer.Write(QuantizeSnorm16(vertex.normal.y));
                    writer.Write(QuantizeSnorm16(vertex.normal.z));
                    writer.Write(QuantizeUnorm16(vertex.uv.x, uvMin.x, uvExtent.x));
                    writer.Write(QuantizeUnorm16(vertex.uv.y, uvMin.y, uvExtent.y));
                }
            }

            private static void WriteIndices(LeafClusterBuildData cluster, BinaryWriter writer)
            {
                for (var i = 0; i < cluster.indices.Count; i++)
                {
                    writer.Write(cluster.indices[i]);
                }
            }

            private static ushort QuantizeUnorm16(float value, float min, float extent)
            {
                if (Mathf.Abs(extent) < 1e-8f)
                {
                    return 0;
                }

                var normalized = Mathf.Clamp01((value - min) / extent);
                return (ushort)Mathf.RoundToInt(normalized * ushort.MaxValue);
            }

            private static short QuantizeSnorm16(float value)
            {
                return (short)Mathf.RoundToInt(Mathf.Clamp(value, -1f, 1f) * short.MaxValue);
            }

            private static int CountSourceTriangles(List<NormalizedSubmesh> submeshes)
            {
                var triangleCount = 0;
                for (var i = 0; i < submeshes.Count; i++)
                {
                    triangleCount += submeshes[i].indices.Length / 3;
                }

                return triangleCount;
            }

            private struct NormalizedMesh
            {
                public Mesh mesh;
                public Vector3[] positions;
                public Vector3[] normals;
                public Vector2[] uv0;
                public Bounds assetBounds;
                public Vector2 uvMin;
                public Vector2 uvMax;
                public List<NormalizedSubmesh> submeshes;
                public List<NanoMeshBakeWarning> warnings;
            }

            private struct NormalizedSubmesh
            {
                public int submeshIndex;
                public int[] indices;
            }

            private struct ClusterVertex
            {
                public Vector3 position;
                public Vector3 normal;
                public Vector2 uv;
            }

            private struct HierarchyBuildNode
            {
                public int parentNodeIndex;
                public int firstChildNodeIndex;
                public int childCount;
                public int hierarchyLevel;
                public Bounds localBounds;
                public Vector4 boundingSphere;
                public Vector4 normalCone;
                public float geometricError;
                public float parentError;
                public List<int> descendantLeafClusterIndices;
                public bool isLeaf;
            }

            private struct LeafClusterBuildData
            {
                public int submeshIndex;
                public int materialRangeIndex;
                public List<ClusterVertex> vertices;
                public List<ushort> indices;
                public Bounds bounds;
                public Vector4 boundingSphere;
                public Vector4 normalCone;
                public float geometricError;
                public int hierarchyNodeIndex;
                public int hierarchyLevel;
                public int vertexDataOffsetBytes;
                public int indexDataOffsetBytes;
            }
        }
    }
}
