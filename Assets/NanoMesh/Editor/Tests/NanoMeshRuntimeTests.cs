using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace NanoMesh.Editor.Tests
{
    public class NanoMeshRuntimeTests
    {
        private const string BunnyAssetPath = "Assets/NanoMesh/bunny_NanoMesh.asset";
        private readonly List<Object> createdObjects = new List<Object>();
        private string previousScenePath;

        [SetUp]
        public void SetUp()
        {
            previousScenePath = EditorSceneManager.GetActiveScene().path;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [TearDown]
        public void TearDown()
        {
            for (var i = createdObjects.Count - 1; i >= 0; i--)
            {
                if (createdObjects[i] != null)
                {
                    Object.DestroyImmediate(createdObjects[i]);
                }
            }

            createdObjects.Clear();

            if (!string.IsNullOrWhiteSpace(previousScenePath))
            {
                EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
            }
        }

        [Test]
        public void NanoMeshAsset_WithBunnySample_IsRuntimeValidAndReportsBytes()
        {
            var asset = AssetDatabase.LoadAssetAtPath<NanoMeshAsset>(BunnyAssetPath);
            Assert.That(asset, Is.Not.Null, "Expected baked bunny asset at " + BunnyAssetPath);
            Assert.That(asset.IsRuntimeDataValid, Is.True);
            Assert.That(asset.StaticGeometryBytes, Is.GreaterThan(0));
            Assert.That(asset.StaticMetadataBytes, Is.GreaterThan(0));
            Assert.That(asset.EstimatedStaticGpuBytes, Is.EqualTo(asset.StaticGeometryBytes + asset.StaticMetadataBytes));
        }

        [Test]
        public void NanoMeshAsset_WithMismatchedPayloads_FailsValidation()
        {
            var asset = ScriptableObject.CreateInstance<NanoMeshAsset>();
            createdObjects.Add(asset);
            asset.clusterCount = 1;
            asset.hierarchyNodeCount = 1;
            asset.coarseRootCount = 1;
            asset.clusters = new NanoMeshClusterRecord[0];
            asset.clusterCullData = new NanoMeshClusterCullRecord[0];
            asset.hierarchyNodes = new[] { new NanoMeshHierarchyNode() };
            asset.coarseRootNodeIndices = new[] { 0 };
            asset.packedVertexData = new byte[0];
            asset.packedIndexData = new byte[0];

            Assert.That(asset.IsRuntimeDataValid, Is.False);
            Assert.That(asset.TryGetRuntimeValidationError(out var error), Is.False);
            Assert.That(error, Does.Contain("cluster"));
        }

        [Test]
        public void NanoMeshRuntimeBuffers_AllocateResetAndRelease()
        {
            using var buffers = new NanoMeshRuntimeBuffers();
            buffers.EnsureFrameBuffers(2, 8, 8, 8);
            buffers.ResetPerFrameBuffers();

            Assert.That(buffers.IsAllocated, Is.True);
            Assert.That(buffers.InstanceCapacity, Is.GreaterThanOrEqualTo(2));
            Assert.That(buffers.VisibleClusterCapacity, Is.GreaterThanOrEqualTo(8));
            Assert.That(buffers.TraversalCapacity, Is.GreaterThanOrEqualTo(8));
            Assert.That(buffers.TransientGpuBytes, Is.GreaterThan(0));

            buffers.Release();

            Assert.That(buffers.IsAllocated, Is.False);
            Assert.That(buffers.InstanceBuffer, Is.Null);
            Assert.That(buffers.VisibleInstanceBuffer, Is.Null);
            Assert.That(buffers.VisibleClusterBuffer, Is.Null);
            Assert.That(buffers.TraversalFrontierBufferA, Is.Null);
            Assert.That(buffers.TraversalFrontierBufferB, Is.Null);
            Assert.That(buffers.IndirectArgsBuffer, Is.Null);
            Assert.That(buffers.DebugCountersBuffer, Is.Null);
        }

        [Test]
        public void NanoMeshRuntimeBuffers_GrowCapacities_ResetDoesNotThrow()
        {
            using var buffers = new NanoMeshRuntimeBuffers();
            buffers.EnsureFrameBuffers(1, 1, 1, 8);
            buffers.ResetPerFrameBuffers();

            Assert.DoesNotThrow(() =>
            {
                buffers.EnsureFrameBuffers(2, 8, 8, 8);
                buffers.ResetPerFrameBuffers();
            });

            Assert.That(buffers.InstanceCapacity, Is.GreaterThanOrEqualTo(2));
            Assert.That(buffers.VisibleClusterCapacity, Is.GreaterThanOrEqualTo(8));
            Assert.That(buffers.TraversalCapacity, Is.GreaterThanOrEqualTo(8));
        }

        [Test]
        public void NanoMeshManager_PrepareFrame_RegistersInstancesAndSharesUploadedAsset()
        {
            var asset = AssetDatabase.LoadAssetAtPath<NanoMeshAsset>(BunnyAssetPath);
            Assert.That(asset, Is.Not.Null, "Expected baked bunny asset at " + BunnyAssetPath);

            var managerObject = new GameObject("NanoMeshManager_Test");
            createdObjects.Add(managerObject);
            var manager = managerObject.AddComponent<NanoMeshManager>();

            var cameraObject = new GameObject("NanoMeshCamera_Test");
            createdObjects.Add(cameraObject);
            var camera = cameraObject.AddComponent<Camera>();

            var rendererA = CreateRenderer("RendererA", asset, new Vector3(0f, 0f, 0f));
            var rendererB = CreateRenderer("RendererB", asset, new Vector3(2f, 0f, 0f));

            manager.RefreshRegistrations();
            manager.PrepareFrame(camera);

            Assert.That(manager.RegisteredRendererCount, Is.EqualTo(2));
            Assert.That(manager.ValidInstanceCount, Is.EqualTo(2));
            Assert.That(manager.Stats.totalPreparedInstanceCount, Is.EqualTo(2));
            Assert.That(manager.Stats.visibleInstanceCount, Is.EqualTo(2));
            Assert.That(manager.UploadedAssetCount, Is.EqualTo(1));
            Assert.That(manager.PreparedMaterialRangeCount, Is.EqualTo(asset.materialRanges.Length * 2));
            Assert.That(manager.PreparedClusterCount, Is.EqualTo(asset.clusterCount * 2));
            Assert.That(manager.Stats.instanceBufferCapacity, Is.GreaterThanOrEqualTo(2));
            Assert.That(manager.Stats.visibleClusterCapacity, Is.GreaterThanOrEqualTo(asset.clusterCount * 2));
            Assert.That(manager.Stats.traversalCapacity, Is.GreaterThanOrEqualTo(asset.hierarchyNodeCount * 2));
            Assert.That(manager.Stats.staticGpuBytes, Is.GreaterThan(0));
            Assert.That(manager.Stats.transientGpuBytes, Is.GreaterThan(0));

            rendererB.gameObject.SetActive(false);
            manager.RefreshRegistrations();
            manager.PrepareFrame(camera);

            Assert.That(manager.RegisteredRendererCount, Is.EqualTo(1));
            Assert.That(manager.ValidInstanceCount, Is.EqualTo(1));
            Assert.That(manager.UploadedAssetCount, Is.EqualTo(1));
        }

        [Test]
        public void NanoMeshManager_PrepareFrame_GrowingRendererCount_DoesNotThrow()
        {
            var asset = AssetDatabase.LoadAssetAtPath<NanoMeshAsset>(BunnyAssetPath);
            Assert.That(asset, Is.Not.Null, "Expected baked bunny asset at " + BunnyAssetPath);

            var managerObject = new GameObject("NanoMeshManager_Test");
            createdObjects.Add(managerObject);
            var manager = managerObject.AddComponent<NanoMeshManager>();

            var cameraObject = new GameObject("NanoMeshCamera_Test");
            createdObjects.Add(cameraObject);
            var camera = cameraObject.AddComponent<Camera>();

            CreateRenderer("RendererA", asset, new Vector3(0f, 0f, 0f));
            manager.RefreshRegistrations();
            manager.PrepareFrame(camera);

            CreateRenderer("RendererB", asset, new Vector3(2f, 0f, 0f));
            manager.RefreshRegistrations();

            Assert.DoesNotThrow(() => manager.PrepareFrame(camera));
            Assert.That(manager.ValidInstanceCount, Is.EqualTo(2));
            Assert.That(manager.Stats.totalPreparedInstanceCount, Is.EqualTo(2));
            Assert.That(manager.Stats.instanceBufferCapacity, Is.GreaterThanOrEqualTo(2));
            Assert.That(manager.Stats.traversalCapacity, Is.GreaterThanOrEqualTo(asset.hierarchyNodeCount * 2));
        }

        [Test]
        public void NanoMeshManager_ExecuteInstanceCulling_CullsOffscreenInstances()
        {
            var asset = AssetDatabase.LoadAssetAtPath<NanoMeshAsset>(BunnyAssetPath);
            var computeShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/NanoMesh/Runtime/Shaders/NanoMeshPlaceholder.compute");
            Assert.That(asset, Is.Not.Null, "Expected baked bunny asset at " + BunnyAssetPath);
            Assert.That(computeShader, Is.Not.Null, "Expected instance culling compute shader.");

            var managerObject = new GameObject("NanoMeshManager_Test");
            createdObjects.Add(managerObject);
            var manager = managerObject.AddComponent<NanoMeshManager>();

            var cameraObject = new GameObject("NanoMeshCamera_Test");
            createdObjects.Add(cameraObject);
            var camera = cameraObject.AddComponent<Camera>();
            camera.transform.position = Vector3.zero;
            camera.transform.rotation = Quaternion.identity;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 100f;

            CreateRenderer("RendererVisible", asset, new Vector3(0f, 0f, 5f));
            CreateRenderer("RendererOffscreen", asset, new Vector3(500f, 0f, 5f));

            manager.RefreshRegistrations();
            manager.PrepareFrame(camera);

            var culled = manager.ExecuteInstanceCulling(camera, computeShader);

            Assert.That(culled, Is.True);
            Assert.That(manager.Stats.totalPreparedInstanceCount, Is.EqualTo(2));
            Assert.That(manager.Stats.visibleInstanceCount, Is.EqualTo(1));
            Assert.That(manager.CurrentRenderInstanceCount, Is.EqualTo(1));
        }

        [Test]
        public void NanoMeshComputeShader_ResolvesAllRequiredKernels()
        {
            var computeShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/NanoMesh/Runtime/Shaders/NanoMeshPlaceholder.compute");

            Assert.That(computeShader, Is.Not.Null, "Expected NanoMesh compute shader asset.");
            Assert.That(
                NanoMeshManager.TryResolveRequiredKernels(
                    computeShader,
                    "CSMain",
                    "ResetTraversalState",
                    "SeedCoarseRoots",
                    "TraverseFrontier",
                    "SwapFrontierCounts",
                    "BuildIndirectArgs"),
                Is.True);
        }

        [Test]
        public void NanoMeshManager_ExecuteClusterCullingAndLod_ReducesVisibleClustersWithDistance()
        {
            var asset = CreateGeneratedRuntimeAsset();
            var computeShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/NanoMesh/Runtime/Shaders/NanoMeshPlaceholder.compute");
            createdObjects.Add(asset);

            var managerObject = new GameObject("NanoMeshManager_Test");
            createdObjects.Add(managerObject);
            var manager = managerObject.AddComponent<NanoMeshManager>();

            var cameraObject = new GameObject("NanoMeshCamera_Test");
            createdObjects.Add(cameraObject);
            var camera = cameraObject.AddComponent<Camera>();
            camera.transform.rotation = Quaternion.identity;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 500f;

            CreateRenderer("RendererNearFar", asset, new Vector3(0f, 0f, 20f));

            manager.RefreshRegistrations();

            camera.transform.position = Vector3.zero;
            manager.PrepareFrame(camera);
            manager.ExecuteInstanceCulling(camera, computeShader);
            var nearSuccess = manager.ExecuteClusterCullingAndLod(camera, computeShader);
            var nearVisibleClusterCount = manager.CurrentVisibleClusterCount;

            camera.transform.position = new Vector3(0f, 0f, -120f);
            manager.PrepareFrame(camera);
            manager.ExecuteInstanceCulling(camera, computeShader);
            var farSuccess = manager.ExecuteClusterCullingAndLod(camera, computeShader);
            var farVisibleClusterCount = manager.CurrentVisibleClusterCount;

            Assert.That(nearSuccess, Is.True);
            Assert.That(farSuccess, Is.True);
            Assert.That(nearVisibleClusterCount, Is.GreaterThan(0));
            Assert.That(farVisibleClusterCount, Is.GreaterThan(0));
            Assert.That(nearVisibleClusterCount, Is.GreaterThanOrEqualTo(farVisibleClusterCount));
            Assert.That(manager.Stats.traversedNodeCount, Is.GreaterThan(0));
            Assert.That(manager.Stats.acceptedClusterCount, Is.EqualTo(manager.CurrentVisibleClusterCount));
        }

        [Test]
        public void NanoMeshManager_ExecuteClusterCullingAndLod_Disabled_UsesFallbackVisibleClusters()
        {
            var asset = CreateGeneratedRuntimeAsset();
            var computeShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/NanoMesh/Runtime/Shaders/NanoMeshPlaceholder.compute");
            createdObjects.Add(asset);

            var managerObject = new GameObject("NanoMeshManager_Test");
            createdObjects.Add(managerObject);
            var manager = managerObject.AddComponent<NanoMeshManager>();

            var serializedManager = new SerializedObject(manager);
            serializedManager.FindProperty("enableClusterCulling").boolValue = false;
            serializedManager.ApplyModifiedPropertiesWithoutUndo();

            var cameraObject = new GameObject("NanoMeshCamera_Test");
            createdObjects.Add(cameraObject);
            var camera = cameraObject.AddComponent<Camera>();
            camera.transform.position = Vector3.zero;
            camera.transform.rotation = Quaternion.identity;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 500f;

            CreateRenderer("RendererFallbackClusters", asset, new Vector3(0f, 0f, 10f));

            manager.RefreshRegistrations();
            manager.PrepareFrame(camera);
            manager.ExecuteInstanceCulling(camera, computeShader);
            var success = manager.ExecuteClusterCullingAndLod(camera, computeShader);

            Assert.That(manager.EnableClusterCulling, Is.False);
            Assert.That(success, Is.False);
            Assert.That(manager.CurrentVisibleClusterCount, Is.EqualTo(GetFallbackClusterCount(asset)));
            Assert.That(manager.Stats.visibleClusterCount, Is.EqualTo(manager.CurrentVisibleClusterCount));
            Assert.That(manager.Stats.acceptedClusterCount, Is.EqualTo(manager.CurrentVisibleClusterCount));
            Assert.That(manager.Stats.traversedNodeCount, Is.EqualTo(0));
        }

        [Test]
        public void NanoMeshManager_HierarchyLevelFilter_Disabled_AllowsAllClusterLevels()
        {
            var managerObject = new GameObject("NanoMeshManager_Test");
            createdObjects.Add(managerObject);
            var manager = managerObject.AddComponent<NanoMeshManager>();

            Assert.That(manager.EnableHierarchyLevelFilter, Is.False);
            Assert.That(manager.ShouldRenderClusterHierarchyLevel(0), Is.True);
            Assert.That(manager.ShouldRenderClusterHierarchyLevel(3), Is.True);
        }

        [Test]
        public void NanoMeshManager_HierarchyLevelFilter_ExactLevel_MatchesOnlySelectedLevel()
        {
            var asset = CreateGeneratedRuntimeAsset();
            var computeShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/NanoMesh/Runtime/Shaders/NanoMeshPlaceholder.compute");
            createdObjects.Add(asset);

            var selectedLevel = 0;

            var managerObject = new GameObject("NanoMeshManager_Test");
            createdObjects.Add(managerObject);
            var manager = managerObject.AddComponent<NanoMeshManager>();

            var serializedManager = new SerializedObject(manager);
            serializedManager.FindProperty("enableClusterCulling").boolValue = false;
            serializedManager.FindProperty("enableHierarchyLevelFilter").boolValue = true;
            serializedManager.FindProperty("hierarchyLevelFilter").intValue = selectedLevel;
            serializedManager.ApplyModifiedPropertiesWithoutUndo();

            var cameraObject = new GameObject("NanoMeshCamera_Test");
            createdObjects.Add(cameraObject);
            var camera = cameraObject.AddComponent<Camera>();
            camera.transform.position = Vector3.zero;
            camera.transform.rotation = Quaternion.identity;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 500f;

            CreateRenderer("RendererFilteredClusters", asset, new Vector3(0f, 0f, 10f));

            manager.RefreshRegistrations();
            manager.PrepareFrame(camera);
            manager.ExecuteInstanceCulling(camera, computeShader);
            var success = manager.ExecuteClusterCullingAndLod(camera, computeShader);

            var renderableClusterCount = 0;
            var matchingLevelCount = 0;
            var fallbackClusterCount = GetFallbackClusterCount(asset);
            for (var i = 0; i < fallbackClusterCount; i++)
            {
                var hierarchyLevel = asset.clusters[i].hierarchyLevel;
                if (manager.ShouldRenderClusterHierarchyLevel(hierarchyLevel))
                {
                    renderableClusterCount++;
                }

                if (hierarchyLevel == selectedLevel)
                {
                    matchingLevelCount++;
                }
            }

            Assert.That(success, Is.False);
            Assert.That(manager.EnableHierarchyLevelFilter, Is.True);
            Assert.That(manager.HierarchyLevelFilter, Is.EqualTo(selectedLevel));
            Assert.That(manager.CurrentVisibleClusterCount, Is.EqualTo(fallbackClusterCount));
            Assert.That(matchingLevelCount, Is.GreaterThan(0));
            Assert.That(renderableClusterCount, Is.EqualTo(matchingLevelCount));
        }

        [Test]
        public void NanoMeshManager_HierarchyLevelFilter_NoMatches_SkipsAllClusterLevels()
        {
            var asset = CreateGeneratedRuntimeAsset();
            createdObjects.Add(asset);

            var managerObject = new GameObject("NanoMeshManager_Test");
            createdObjects.Add(managerObject);
            var manager = managerObject.AddComponent<NanoMeshManager>();

            var serializedManager = new SerializedObject(manager);
            serializedManager.FindProperty("enableHierarchyLevelFilter").boolValue = true;
            serializedManager.FindProperty("hierarchyLevelFilter").intValue = FindHighestHierarchyLevel(asset) + 1;
            serializedManager.ApplyModifiedPropertiesWithoutUndo();

            for (var i = 0; i < asset.clusters.Length; i++)
            {
                Assert.That(manager.ShouldRenderClusterHierarchyLevel(asset.clusters[i].hierarchyLevel), Is.False);
            }
        }

        [Test]
        public void NanoMeshManager_ExecuteClusterCullingAndLod_HandlesMultipleCoarseRoots()
        {
            var asset = CreateGeneratedRuntimeAsset(targetRootCount: 3);
            var computeShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/NanoMesh/Runtime/Shaders/NanoMeshPlaceholder.compute");
            createdObjects.Add(asset);

            var managerObject = new GameObject("NanoMeshManager_Test");
            createdObjects.Add(managerObject);
            var manager = managerObject.AddComponent<NanoMeshManager>();

            var cameraObject = new GameObject("NanoMeshCamera_Test");
            createdObjects.Add(cameraObject);
            var camera = cameraObject.AddComponent<Camera>();
            camera.transform.position = Vector3.zero;
            camera.transform.rotation = Quaternion.identity;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 500f;

            CreateRenderer("RendererMultiRoot", asset, new Vector3(0f, 0f, 10f));

            manager.RefreshRegistrations();
            manager.PrepareFrame(camera);
            manager.ExecuteInstanceCulling(camera, computeShader);
            var success = manager.ExecuteClusterCullingAndLod(camera, computeShader);

            Assert.That(asset.coarseRootCount, Is.GreaterThan(1));
            Assert.That(asset.coarseRootCount, Is.LessThanOrEqualTo(3));
            Assert.That(success, Is.True);
            Assert.That(manager.CurrentVisibleClusterCount, Is.GreaterThan(0));
            Assert.That(manager.Stats.traversedNodeCount, Is.GreaterThan(0));
        }

        [Test]
        public void NanoMeshManager_ResolveMaterialForSlot_FallsBackToMeshRendererMaterials()
        {
            var asset = AssetDatabase.LoadAssetAtPath<NanoMeshAsset>(BunnyAssetPath);
            Assert.That(asset, Is.Not.Null, "Expected baked bunny asset at " + BunnyAssetPath);

            var managerObject = new GameObject("NanoMeshManager_Test");
            createdObjects.Add(managerObject);
            var manager = managerObject.AddComponent<NanoMeshManager>();

            var fallbackMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            fallbackMaterial.color = Color.green;
            createdObjects.Add(fallbackMaterial);

            var renderer = CreateRenderer("RendererWithFallback", asset, Vector3.zero, fallbackMaterial);
            manager.RefreshRegistrations();

            var resolvedMaterial = manager.ResolveMaterialForSlot(renderer, 0);
            Assert.That(resolvedMaterial, Is.SameAs(fallbackMaterial));
        }

        private NanoMeshRenderer CreateRenderer(string name, NanoMeshAsset asset, Vector3 position, Material fallbackMaterial = null)
        {
            var gameObject = new GameObject(name);
            createdObjects.Add(gameObject);
            gameObject.transform.position = position;
            var meshRenderer = gameObject.AddComponent<MeshRenderer>();
            createdObjects.Add(meshRenderer);
            var renderer = gameObject.AddComponent<NanoMeshRenderer>();

            if (fallbackMaterial != null)
            {
                meshRenderer.sharedMaterials = new[] { fallbackMaterial };
            }

            var serializedObject = new SerializedObject(renderer);
            serializedObject.FindProperty("asset").objectReferenceValue = asset;
            serializedObject.FindProperty("lodBias").floatValue = 1f;
            serializedObject.FindProperty("errorScale").floatValue = 1f;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();

            return renderer;
        }

        private static NanoMeshAsset CreateGeneratedRuntimeAsset(int targetRootCount = 2)
        {
            var mesh = CreateGridMesh(8, 8, "RuntimeGrid");
            try
            {
                var result = NanoMeshBaker.BakeMesh(mesh, new NanoMeshBakeOptions
                {
                    maxTrianglesPerCluster = 2,
                    maxChildrenPerParent = 2,
                    targetRootCount = targetRootCount
                });

                Assert.That(result.success, Is.True, result.message);
                return result.asset;
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        private static int FindHighestHierarchyLevel(NanoMeshAsset asset)
        {
            var highestLevel = 0;
            for (var i = 0; i < asset.clusters.Length; i++)
            {
                highestLevel = Mathf.Max(highestLevel, asset.clusters[i].hierarchyLevel);
            }

            return highestLevel;
        }

        private static int GetFallbackClusterCount(NanoMeshAsset asset)
        {
            return asset.leafClusterCount > 0 ? asset.leafClusterCount : asset.clusterCount;
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
    }
}
