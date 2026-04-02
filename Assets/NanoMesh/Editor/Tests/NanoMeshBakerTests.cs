using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace NanoMesh.Editor.Tests
{
    public class NanoMeshBakerTests
    {
        private const string TempRoot = "Assets/NanoMesh/Editor/Tests/Generated";
        private const string BunnyMeshPath = "Assets/NanoMesh/bunny.obj";
        private const string ArmadilloMeshPath = "Assets/NanoMesh/armadillo.obj";
        private const string NativeExecutablePath = "Native/NanoMeshBaker/build/Debug/NanoMeshBakerCli.exe";

        [TearDown]
        public void TearDown()
        {
            NanoMeshBaker.SettingsProviderOverride = null;
            if (AssetDatabase.IsValidFolder(TempRoot))
            {
                AssetDatabase.DeleteAsset(TempRoot);
            }

            AssetDatabase.Refresh();
        }

        [Test]
        public void BakeMesh_WithValidMesh_PopulatesClusterHierarchyAndPayloads()
        {
            var mesh = CreateGridMesh(4, 4, "ValidGrid");
            NanoMeshAsset asset = null;
            try
            {
                var result = NanoMeshBaker.BakeMesh(mesh, new NanoMeshBakeOptions
                {
                    maxTrianglesPerCluster = 2,
                    maxChildrenPerParent = 2,
                    targetRootCount = 2
                });

                asset = result.asset;
                Assert.That(result.success, Is.True, result.message);
                Assert.That(asset, Is.Not.Null);
                Assert.That(asset.leafClusterCount, Is.GreaterThan(0));
                Assert.That(asset.clusterCount, Is.GreaterThan(0));
                Assert.That(asset.clusterCount, Is.GreaterThan(asset.leafClusterCount));
                Assert.That(asset.hierarchyNodeCount, Is.GreaterThan(asset.leafClusterCount));
                Assert.That(asset.packedVertexData.Length, Is.GreaterThan(0));
                Assert.That(asset.packedIndexData.Length, Is.GreaterThan(0));
                Assert.That(asset.coarseRootCount, Is.EqualTo(2));
            }
            finally
            {
                Object.DestroyImmediate(mesh);
                if (asset != null)
                {
                    Object.DestroyImmediate(asset);
                }
            }
        }

        [Test]
        public void BakeMesh_WithTwoSubmeshes_CreatesMaterialRanges()
        {
            var mesh = CreateTwoSubmeshMesh();
            NanoMeshAsset asset = null;
            try
            {
                var result = NanoMeshBaker.BakeMesh(mesh, new NanoMeshBakeOptions
                {
                    maxTrianglesPerCluster = 1,
                    maxChildrenPerParent = 2,
                    targetRootCount = 2
                });

                asset = result.asset;
                Assert.That(result.success, Is.True, result.message);
                Assert.That(asset.materialRanges.Length, Is.EqualTo(2));
                Assert.That(asset.materialRanges[0].submeshIndex, Is.EqualTo(0));
                Assert.That(asset.materialRanges[1].submeshIndex, Is.EqualTo(1));
                Assert.That(asset.materialRanges[0].clusterCount, Is.GreaterThan(0));
                Assert.That(asset.materialRanges[1].clusterCount, Is.GreaterThan(0));
            }
            finally
            {
                Object.DestroyImmediate(mesh);
                if (asset != null)
                {
                    Object.DestroyImmediate(asset);
                }
            }
        }

        [Test]
        public void BakeMesh_WhenReductionStopsWithMultipleRoots_RecordsWarning()
        {
            var mesh = CreateGridMesh(6, 2, "MultiRootGrid");
            NanoMeshAsset asset = null;
            try
            {
                var result = NanoMeshBaker.BakeMesh(mesh, new NanoMeshBakeOptions
                {
                    maxTrianglesPerCluster = 1,
                    maxChildrenPerParent = 2,
                    targetRootCount = 3
                });

                asset = result.asset;
                Assert.That(result.success, Is.True, result.message);
                Assert.That(asset.coarseRootCount, Is.EqualTo(3));
                Assert.That(asset.bakeWarnings, Has.Length.GreaterThan(0));
                Assert.That(ContainsWarning(asset, "MultipleCoarseRoots"), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(mesh);
                if (asset != null)
                {
                    Object.DestroyImmediate(asset);
                }
            }
        }

        [Test]
        public void BakeMesh_WithTargetRootCountAboveLeafCount_RecordsLeafOnlyHierarchyWarning()
        {
            var mesh = CreateGridMesh(2, 2, "LeafOnlyGrid");
            NanoMeshAsset asset = null;
            try
            {
                var result = NanoMeshBaker.BakeMesh(mesh, new NanoMeshBakeOptions
                {
                    maxTrianglesPerCluster = 1,
                    maxChildrenPerParent = 2,
                    targetRootCount = 999
                });

                asset = result.asset;
                Assert.That(result.success, Is.True, result.message);
                Assert.That(asset, Is.Not.Null);
                Assert.That(asset.leafClusterCount, Is.EqualTo(asset.hierarchyNodeCount));
                Assert.That(ContainsWarning(asset, "LeafOnlyHierarchy"), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(mesh);
                if (asset != null)
                {
                    Object.DestroyImmediate(asset);
                }
            }
        }

        [Test]
        public void BakeMesh_WithMissingNormals_FailsClearly()
        {
            var mesh = new Mesh
            {
                name = "MissingNormals"
            };
            mesh.vertices = new[]
            {
                Vector3.zero,
                Vector3.right,
                Vector3.up
            };
            mesh.triangles = new[] { 0, 1, 2 };
            try
            {
                var result = NanoMeshBaker.BakeMesh(mesh);
                Assert.That(result.success, Is.False);
                Assert.That(result.message, Does.Contain("normals"));
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void BakeMesh_WithMissingUv0_UsesZeroUvFallback()
        {
            var mesh = new Mesh
            {
                name = "MissingUv0"
            };
            mesh.vertices = new[]
            {
                Vector3.zero,
                Vector3.right,
                Vector3.up
            };
            mesh.normals = new[]
            {
                Vector3.forward,
                Vector3.forward,
                Vector3.forward
            };
            mesh.triangles = new[] { 0, 1, 2 };

            NanoMeshAsset asset = null;
            try
            {
                var result = NanoMeshBaker.BakeMesh(mesh);
                asset = result.asset;

                Assert.That(result.success, Is.True, result.message);
                Assert.That(asset, Is.Not.Null);
                Assert.That(asset.packedVertexData.Length, Is.GreaterThan(0));
                Assert.That(asset.packedIndexData.Length, Is.GreaterThan(0));
                Assert.That(asset.uvMin, Is.EqualTo(Vector2.zero));
                Assert.That(asset.uvMax, Is.EqualTo(Vector2.zero));
                Assert.That(ContainsWarning(asset, "GeneratedZeroUv0"), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(mesh);
                if (asset != null)
                {
                    Object.DestroyImmediate(asset);
                }
            }
        }

        [Test]
        public void BakeMesh_WithNonTriangleTopology_FailsClearly()
        {
            var mesh = new Mesh
            {
                name = "LineMesh"
            };
            mesh.vertices = new[]
            {
                Vector3.zero,
                Vector3.right,
                Vector3.up,
                Vector3.one
            };
            mesh.normals = new[]
            {
                Vector3.forward,
                Vector3.forward,
                Vector3.forward,
                Vector3.forward
            };
            mesh.uv = new[]
            {
                Vector2.zero,
                Vector2.right,
                Vector2.up,
                Vector2.one
            };
            mesh.SetIndices(new[] { 0, 1, 2, 3 }, MeshTopology.Lines, 0);
            try
            {
                var result = NanoMeshBaker.BakeMesh(mesh);
                Assert.That(result.success, Is.False);
                Assert.That(result.message, Does.Contain("triangles"));
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void BakeMeshToAsset_PersistsSerializedDataDeterministically()
        {
            EnsureFolder("Assets/NanoMesh");
            EnsureFolder("Assets/NanoMesh/Editor");
            EnsureFolder("Assets/NanoMesh/Editor/Tests");
            EnsureFolder(TempRoot);

            var mesh = CreateGridMesh(3, 3, "PersistedGrid");
            var assetPath = TempRoot + "/PersistedGrid_NanoMesh.asset";
            NanoMeshAsset firstAsset = null;
            NanoMeshAsset secondAsset = null;
            try
            {
                var first = NanoMeshBaker.BakeMesh(mesh);
                var second = NanoMeshBaker.BakeMesh(mesh);
                var persisted = NanoMeshBaker.BakeMeshToAsset(mesh, assetPath);
                firstAsset = first.asset;
                secondAsset = second.asset;

                Assert.That(first.success, Is.True, first.message);
                Assert.That(second.success, Is.True, second.message);
                Assert.That(persisted.success, Is.True, persisted.message);

                var reloaded = AssetDatabase.LoadAssetAtPath<NanoMeshAsset>(assetPath);
                Assert.That(reloaded, Is.Not.Null);
                Assert.That(reloaded.clusterCount, Is.EqualTo(first.asset.clusterCount));
                Assert.That(reloaded.hierarchyDepth, Is.EqualTo(first.asset.hierarchyDepth));
                Assert.That(reloaded.packedVertexData, Is.EqualTo(first.asset.packedVertexData));
                Assert.That(second.asset.packedIndexData, Is.EqualTo(first.asset.packedIndexData));
            }
            finally
            {
                Object.DestroyImmediate(mesh);
                if (firstAsset != null)
                {
                    Object.DestroyImmediate(firstAsset);
                }

                if (secondAsset != null)
                {
                    Object.DestroyImmediate(secondAsset);
                }
            }
        }

        [Test]
        public void BakeMesh_WithMissingCli_UsesManagedFallbackWhenEnabled()
        {
            NanoMeshBaker.SettingsProviderOverride = () =>
            {
                var settings = ScriptableObject.CreateInstance<NanoMeshSettings>();
                settings.preferredBackend = NanoMeshBakeBackendKind.MeshoptimizerCli;
                settings.fallbackToManagedBackend = true;
                settings.bakerExecutablePath = "Native/NanoMeshBaker/build/Debug/DoesNotExist.exe";
                settings.tempBakeRoot = "Library/NanoMesh/TestBake";
                return settings;
            };

            var mesh = CreateGridMesh(2, 2, "FallbackGrid");
            NanoMeshAsset asset = null;
            try
            {
                var result = NanoMeshBaker.BakeMesh(mesh);
                asset = result.asset;
                Assert.That(result.success, Is.True, result.message);
                Assert.That(result.usedFallback, Is.True);
                Assert.That(result.backendName, Is.EqualTo(NanoMeshBakeBackendKind.Managed.ToString()));
                Assert.That(ContainsWarning(asset, "MeshoptimizerCliFallback"), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(mesh);
                if (asset != null)
                {
                    Object.DestroyImmediate(asset);
                }
            }
        }

        [Test]
        public void BakeMesh_WithMissingCli_FailsWhenFallbackDisabled()
        {
            NanoMeshBaker.SettingsProviderOverride = () =>
            {
                var settings = ScriptableObject.CreateInstance<NanoMeshSettings>();
                settings.preferredBackend = NanoMeshBakeBackendKind.MeshoptimizerCli;
                settings.fallbackToManagedBackend = false;
                settings.bakerExecutablePath = "Native/NanoMeshBaker/build/Debug/DoesNotExist.exe";
                settings.tempBakeRoot = "Library/NanoMesh/TestBake";
                return settings;
            };

            var mesh = CreateGridMesh(2, 2, "FailGrid");
            try
            {
                var result = NanoMeshBaker.BakeMesh(mesh);
                Assert.That(result.success, Is.False);
                Assert.That(result.backendName, Is.EqualTo(NanoMeshBakeBackendKind.MeshoptimizerCli.ToString()));
                Assert.That(result.message, Does.Contain("executable"));
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void BakeMesh_WithNativeBackend_SucceedsWithoutFallback()
        {
            NanoMeshBaker.SettingsProviderOverride = () =>
            {
                var settings = ScriptableObject.CreateInstance<NanoMeshSettings>();
                settings.preferredBackend = NanoMeshBakeBackendKind.MeshoptimizerCli;
                settings.fallbackToManagedBackend = false;
                settings.bakerExecutablePath = NativeExecutablePath;
                settings.tempBakeRoot = "Library/NanoMesh/TestBake";
                return settings;
            };

            var mesh = CreateGridMesh(2, 2, "NativeGrid");
            NanoMeshAsset asset = null;
            try
            {
                var result = NanoMeshBaker.BakeMesh(mesh);
                asset = result.asset;

                Assert.That(result.success, Is.True, result.message);
                Assert.That(asset, Is.Not.Null);
                Assert.That(result.backendName, Is.EqualTo(NanoMeshBakeBackendKind.MeshoptimizerCli.ToString()));
                Assert.That(result.usedFallback, Is.False);
                Assert.That(asset.clusterCount, Is.GreaterThan(0));
                Assert.That(asset.hierarchyNodeCount, Is.GreaterThan(0));
            }
            finally
            {
                Object.DestroyImmediate(mesh);
                if (asset != null)
                {
                    Object.DestroyImmediate(asset);
                }
            }
        }

        [Test]
        public void BakeMesh_WithManagedBackend_BakesRenderableParentNodes()
        {
            var mesh = CreateGridMesh(8, 8, "NativeHierarchyGrid");
            NanoMeshAsset asset = null;
            try
            {
                var result = NanoMeshBaker.BakeMesh(mesh, new NanoMeshBakeOptions
                {
                    maxTrianglesPerCluster = 2,
                    maxChildrenPerParent = 2,
                    targetRootCount = 2,
                    hierarchyPartitionMode = NanoMeshHierarchyPartitionMode.MetisAdjacency,
                    hierarchyAdjacencyMode = NanoMeshHierarchyAdjacencyMode.SharedEdge
                });
                asset = result.asset;

                Assert.That(result.success, Is.True, result.message);
                Assert.That(asset, Is.Not.Null);
                Assert.That(result.backendName, Is.EqualTo(NanoMeshBakeBackendKind.Managed.ToString()));
                Assert.That(FindHighestClusterLevel(asset), Is.GreaterThan(0));
                Assert.That(asset.clusterCount, Is.GreaterThan(asset.leafClusterCount));
                Assert.That(AllHierarchyNodesHaveRenderableCluster(asset), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(mesh);
                if (asset != null)
                {
                    Object.DestroyImmediate(asset);
                }
            }
        }

        [Test]
        public void BakeMesh_WithNativeBackendAndMissingUv0_Succeeds()
        {
            NanoMeshBaker.SettingsProviderOverride = () =>
            {
                var settings = ScriptableObject.CreateInstance<NanoMeshSettings>();
                settings.preferredBackend = NanoMeshBakeBackendKind.MeshoptimizerCli;
                settings.fallbackToManagedBackend = false;
                settings.bakerExecutablePath = NativeExecutablePath;
                settings.tempBakeRoot = "Library/NanoMesh/TestBake";
                return settings;
            };

            var mesh = new Mesh
            {
                name = "NativeMissingUv0"
            };
            mesh.vertices = new[]
            {
                Vector3.zero,
                Vector3.right,
                Vector3.up
            };
            mesh.normals = new[]
            {
                Vector3.forward,
                Vector3.forward,
                Vector3.forward
            };
            mesh.triangles = new[] { 0, 1, 2 };

            NanoMeshAsset asset = null;
            try
            {
                var result = NanoMeshBaker.BakeMesh(mesh);
                asset = result.asset;

                Assert.That(result.success, Is.True, result.message);
                Assert.That(asset, Is.Not.Null);
                Assert.That(result.backendName, Is.EqualTo(NanoMeshBakeBackendKind.MeshoptimizerCli.ToString()));
                Assert.That(ContainsWarning(asset, "GeneratedZeroUv0"), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(mesh);
                if (asset != null)
                {
                    Object.DestroyImmediate(asset);
                }
            }
        }

        [Test]
        public void BakeMeshToAsset_WithBunnySample_PersistsBakedAsset()
        {
            EnsureFolder(TempRoot);

            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(BunnyMeshPath);
            Assert.That(mesh, Is.Not.Null, "Expected bunny mesh at " + BunnyMeshPath);

            var assetPath = TempRoot + "/BunnySample_NanoMesh.asset";
            var result = NanoMeshBaker.BakeMeshToAsset(mesh, assetPath);

            Assert.That(result.success, Is.True, result.message);
            Assert.That(result.asset, Is.Not.Null);
            Assert.That(result.asset.sourceMeshAssetPath, Is.EqualTo(BunnyMeshPath));
            Assert.That(result.asset.clusterCount, Is.GreaterThan(0));
            Assert.That(result.asset.hierarchyNodeCount, Is.GreaterThan(0));
            Assert.That(result.asset.packedVertexData.Length, Is.GreaterThan(0));
            Assert.That(result.asset.packedIndexData.Length, Is.GreaterThan(0));

            var reloaded = AssetDatabase.LoadAssetAtPath<NanoMeshAsset>(assetPath);
            Assert.That(reloaded, Is.Not.Null);
            Assert.That(reloaded.sourceMeshAssetPath, Is.EqualTo(BunnyMeshPath));
            Assert.That(reloaded.clusterCount, Is.EqualTo(result.asset.clusterCount));
            Assert.That(reloaded.hierarchyDepth, Is.EqualTo(result.asset.hierarchyDepth));
        }

        [Test]
        public void BakeMeshToAsset_WithArmadilloSample_PersistsBakedAsset()
        {
            EnsureFolder(TempRoot);

            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(ArmadilloMeshPath);
            Assert.That(mesh, Is.Not.Null, "Expected armadillo mesh at " + ArmadilloMeshPath);

            var assetPath = TempRoot + "/ArmadilloSample_NanoMesh.asset";
            var result = NanoMeshBaker.BakeMeshToAsset(mesh, assetPath);

            Assert.That(result.success, Is.True, result.message);
            Assert.That(result.asset, Is.Not.Null);
            Assert.That(result.asset.sourceMeshAssetPath, Is.EqualTo(ArmadilloMeshPath));
            Assert.That(result.asset.packedVertexData.Length, Is.GreaterThan(0));
            Assert.That(result.asset.packedIndexData.Length, Is.GreaterThan(0));
            Assert.That(ContainsWarning(result.asset, "GeneratedZeroUv0"), Is.True);

            var reloaded = AssetDatabase.LoadAssetAtPath<NanoMeshAsset>(assetPath);
            Assert.That(reloaded, Is.Not.Null);
            Assert.That(reloaded.sourceMeshAssetPath, Is.EqualTo(ArmadilloMeshPath));
            Assert.That(ContainsWarning(reloaded, "GeneratedZeroUv0"), Is.True);
        }

        private static bool ContainsWarning(NanoMeshAsset asset, string code)
        {
            for (var i = 0; i < asset.bakeWarnings.Length; i++)
            {
                if (asset.bakeWarnings[i].code == code)
                {
                    return true;
                }
            }

            return false;
        }

        private static int FindHighestClusterLevel(NanoMeshAsset asset)
        {
            var highestLevel = 0;
            for (var i = 0; i < asset.clusters.Length; i++)
            {
                highestLevel = Mathf.Max(highestLevel, asset.clusters[i].hierarchyLevel);
            }

            return highestLevel;
        }

        private static bool AllHierarchyNodesHaveRenderableCluster(NanoMeshAsset asset)
        {
            for (var i = 0; i < asset.hierarchyNodes.Length; i++)
            {
                var node = asset.hierarchyNodes[i];
                if (node.renderClusterCount <= 0)
                {
                    return false;
                }

                if (node.firstClusterIndex < 0 || node.firstClusterIndex + node.renderClusterCount > asset.clusters.Length)
                {
                    return false;
                }
            }

            return true;
        }

        private static Mesh CreateGridMesh(int columns, int rows, string name)
        {
            var vertices = new Vector3[(columns + 1) * (rows + 1)];
            var normals = new Vector3[vertices.Length];
            var uv = new Vector2[vertices.Length];
            var cursor = 0;
            for (var y = 0; y <= rows; y++)
            {
                for (var x = 0; x <= columns; x++)
                {
                    vertices[cursor] = new Vector3(x, y, 0f);
                    normals[cursor] = Vector3.forward;
                    uv[cursor] = new Vector2(columns == 0 ? 0f : x / (float)columns, rows == 0 ? 0f : y / (float)rows);
                    cursor++;
                }
            }

            var triangles = new int[columns * rows * 6];
            var triangleCursor = 0;
            for (var y = 0; y < rows; y++)
            {
                for (var x = 0; x < columns; x++)
                {
                    var start = y * (columns + 1) + x;
                    triangles[triangleCursor++] = start;
                    triangles[triangleCursor++] = start + columns + 1;
                    triangles[triangleCursor++] = start + 1;
                    triangles[triangleCursor++] = start + 1;
                    triangles[triangleCursor++] = start + columns + 1;
                    triangles[triangleCursor++] = start + columns + 2;
                }
            }

            var mesh = new Mesh
            {
                name = name,
                vertices = vertices,
                normals = normals,
                uv = uv
            };
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh CreateTwoSubmeshMesh()
        {
            var mesh = new Mesh
            {
                name = "TwoSubmesh"
            };
            mesh.vertices = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, 0f),
                new Vector3(2f, 0f, 0f),
                new Vector3(3f, 0f, 0f),
                new Vector3(2f, 1f, 0f)
            };
            mesh.normals = new[]
            {
                Vector3.forward,
                Vector3.forward,
                Vector3.forward,
                Vector3.forward,
                Vector3.forward,
                Vector3.forward
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(0.5f, 0f),
                new Vector2(0f, 1f),
                new Vector2(0.5f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, 1f)
            };
            mesh.subMeshCount = 2;
            mesh.SetIndices(new[] { 0, 1, 2 }, MeshTopology.Triangles, 0);
            mesh.SetIndices(new[] { 3, 4, 5 }, MeshTopology.Triangles, 1);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void EnsureFolder(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath))
            {
                return;
            }

            var parent = Path.GetDirectoryName(folderPath)?.Replace('\\', '/');
            var folderName = Path.GetFileName(folderPath);
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            {
                EnsureFolder(parent);
            }

            AssetDatabase.CreateFolder(parent, folderName);
        }
    }
}
