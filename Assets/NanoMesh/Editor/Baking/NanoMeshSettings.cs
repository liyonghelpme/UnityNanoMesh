using System.IO;
using UnityEditor;
using UnityEngine;

namespace NanoMesh.Editor
{
    public enum NanoMeshBakeBackendKind
    {
        Managed = 0,
        MeshoptimizerCli = 1
    }

    [CreateAssetMenu(menuName = "NanoMesh/Settings", fileName = "NanoMeshSettings")]
    public sealed class NanoMeshSettings : ScriptableObject
    {
        public const string DefaultAssetPath = "Assets/NanoMesh/Editor/NanoMeshSettings.asset";

        [Tooltip("Path to NanoMeshBakerCli.exe. Relative paths are resolved from the project root.")]
        public string bakerExecutablePath = "Native/NanoMeshBaker/build/Debug/NanoMeshBakerCli.exe";

        [Tooltip("Temporary folder for request and response files. Relative paths are resolved from the project root.")]
        public string tempBakeRoot = "Library/NanoMesh/Bake";

        public NanoMeshBakeBackendKind preferredBackend = NanoMeshBakeBackendKind.MeshoptimizerCli;
        public bool fallbackToManagedBackend = true;
    }

    internal static class NanoMeshSettingsUtility
    {
        public static NanoMeshSettings GetOrCreateSettings()
        {
            var settings = AssetDatabase.LoadAssetAtPath<NanoMeshSettings>(NanoMeshSettings.DefaultAssetPath);
            if (settings != null)
            {
                return settings;
            }

            EnsureFolder("Assets/NanoMesh");
            EnsureFolder("Assets/NanoMesh/Editor");

            settings = ScriptableObject.CreateInstance<NanoMeshSettings>();
            AssetDatabase.CreateAsset(settings, NanoMeshSettings.DefaultAssetPath);
            AssetDatabase.SaveAssets();
            return settings;
        }

        public static string ResolvePathFromProjectRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            return Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(GetProjectRoot(), path));
        }

        public static string GetProjectRoot()
        {
            return Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
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
