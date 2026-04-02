using UnityEditor;
using UnityEditor.SceneManagement;

namespace NanoMesh.Editor
{
    internal static class NanoMeshValidationMenu
    {
        private const string ValidationScenePath = "Assets/NanoMesh/Samples/Scenes/NanoMeshValidation.unity";

        [MenuItem("NanoMesh/Open Validation Scene", false, 0)]
        private static void OpenValidationScene()
        {
            EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();
            EditorSceneManager.OpenScene(ValidationScenePath);
        }
    }
}
