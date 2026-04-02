using UnityEditor;
using UnityEngine;

namespace NanoMesh.Editor
{
    internal static class NanoMeshBakeMenu
    {
        private const string BunnySourceMeshPath = "Assets/NanoMesh/bunny.obj";
        private const string BunnyBakedAssetPath = "Assets/NanoMesh/bunny_NanoMesh.asset";
        private const string ArmadilloSourceMeshPath = "Assets/NanoMesh/armadillo.obj";
        private const string ArmadilloBakedAssetPath = "Assets/NanoMesh/armadillo_NanoMesh.asset";

        [MenuItem("NanoMesh/Bake Selected Mesh", false, 10)]
        [MenuItem("Assets/NanoMesh/Bake Selected Mesh", false, 2000)]
        private static void BakeSelectedMesh()
        {
            if (!TryGetSelectedMesh(out var mesh))
            {
                EditorUtility.DisplayDialog("NanoMesh Bake", "Select a Mesh asset or a GameObject with a MeshFilter.", "OK");
                return;
            }

            var suggestedName = mesh.name + "_NanoMesh";
            var meshPath = AssetDatabase.GetAssetPath(mesh);
            var directory = string.IsNullOrWhiteSpace(meshPath) ? "Assets/NanoMesh" : System.IO.Path.GetDirectoryName(meshPath)?.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = "Assets/NanoMesh";
            }

            var assetPath = EditorUtility.SaveFilePanelInProject(
                "Bake NanoMesh Asset",
                suggestedName,
                "asset",
                "Choose where to save the baked NanoMesh asset.",
                directory);

            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return;
            }

            var result = NanoMeshBaker.BakeMeshToAsset(mesh, assetPath);
            if (result.success)
            {
                Selection.activeObject = result.asset;
                EditorGUIUtility.PingObject(result.asset);
                Debug.Log(result.message, result.asset);
                for (var i = 0; i < result.warnings.Count; i++)
                {
                    Debug.LogWarning(result.warnings[i].message, result.asset);
                }

                return;
            }

            EditorUtility.DisplayDialog("NanoMesh Bake Failed", result.message, "OK");
        }

        [MenuItem("NanoMesh/Validate/Bake Bunny Sample", false, 20)]
        private static void BakeBunnySample()
        {
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(BunnySourceMeshPath);
            if (mesh == null)
            {
                EditorUtility.DisplayDialog("NanoMesh Bake Failed", "Could not load bunny mesh at " + BunnySourceMeshPath + ".", "OK");
                return;
            }

            var result = BakeSampleWithManagedBackend(mesh, BunnyBakedAssetPath);
            if (!result.success)
            {
                EditorUtility.DisplayDialog("NanoMesh Bake Failed", result.message, "OK");
                return;
            }

            Selection.activeObject = result.asset;
            EditorGUIUtility.PingObject(result.asset);

            Debug.Log(
                "NanoMesh bunny bake succeeded. backend=" + result.backendName +
                ", fallback=" + result.usedFallback +
                ", clusters=" + result.asset.clusterCount +
                ", coarseRoots=" + result.asset.coarseRootCount +
                ", hierarchyDepth=" + result.asset.hierarchyDepth,
                result.asset);

            for (var i = 0; i < result.warnings.Count; i++)
            {
                Debug.LogWarning(result.warnings[i].message, result.asset);
            }
        }

        [MenuItem("NanoMesh/Validate/Bake Armadillo Sample", false, 21)]
        private static void BakeArmadilloSample()
        {
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(ArmadilloSourceMeshPath);
            if (mesh == null)
            {
                EditorUtility.DisplayDialog("NanoMesh Bake Failed", "Could not load armadillo mesh at " + ArmadilloSourceMeshPath + ".", "OK");
                return;
            }

            var result = BakeSampleWithManagedBackend(mesh, ArmadilloBakedAssetPath);
            if (!result.success)
            {
                EditorUtility.DisplayDialog("NanoMesh Bake Failed", result.message, "OK");
                return;
            }

            Selection.activeObject = result.asset;
            EditorGUIUtility.PingObject(result.asset);

            Debug.Log(
                "NanoMesh armadillo bake succeeded. backend=" + result.backendName +
                ", fallback=" + result.usedFallback +
                ", clusters=" + result.asset.clusterCount +
                ", coarseRoots=" + result.asset.coarseRootCount +
                ", hierarchyDepth=" + result.asset.hierarchyDepth,
                result.asset);

            for (var i = 0; i < result.warnings.Count; i++)
            {
                Debug.LogWarning(result.warnings[i].message, result.asset);
            }
        }

        [MenuItem("NanoMesh/Bake Selected Mesh", true)]
        [MenuItem("Assets/NanoMesh/Bake Selected Mesh", true)]
        private static bool ValidateBakeSelectedMesh()
        {
            return TryGetSelectedMesh(out _);
        }

        [MenuItem("NanoMesh/Validate/Bake Bunny Sample", true)]
        private static bool ValidateBakeBunnySample()
        {
            return AssetDatabase.LoadAssetAtPath<Mesh>(BunnySourceMeshPath) != null;
        }

        [MenuItem("NanoMesh/Validate/Bake Armadillo Sample", true)]
        private static bool ValidateBakeArmadilloSample()
        {
            return AssetDatabase.LoadAssetAtPath<Mesh>(ArmadilloSourceMeshPath) != null;
        }

        internal static bool TryGetSelectedMesh(out Mesh mesh)
        {
            mesh = Selection.activeObject as Mesh;
            if (mesh != null)
            {
                return true;
            }

            var gameObject = Selection.activeGameObject;
            if (gameObject == null)
            {
                return false;
            }

            var meshFilter = gameObject.GetComponent<MeshFilter>();
            mesh = meshFilter != null ? meshFilter.sharedMesh : null;
            return mesh != null;
        }

        private static NanoMeshBakeResult BakeSampleWithManagedBackend(Mesh mesh, string assetPath)
        {
            var previousOverride = NanoMeshBaker.SettingsProviderOverride;
            try
            {
                NanoMeshBaker.SettingsProviderOverride = () =>
                {
                    var settings = ScriptableObject.CreateInstance<NanoMeshSettings>();
                    settings.preferredBackend = NanoMeshBakeBackendKind.Managed;
                    settings.fallbackToManagedBackend = true;
                    return settings;
                };

                return NanoMeshBaker.BakeMeshToAsset(mesh, assetPath);
            }
            finally
            {
                NanoMeshBaker.SettingsProviderOverride = previousOverride;
            }
        }
    }
}
