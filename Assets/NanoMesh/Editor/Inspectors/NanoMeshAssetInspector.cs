using UnityEditor;
using UnityEngine;

namespace NanoMesh.Editor
{
    [CustomEditor(typeof(NanoMeshAsset))]
    public sealed class NanoMeshAssetInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var asset = (NanoMeshAsset)target;
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("Source Mesh", asset.sourceMesh, typeof(Mesh), false);
                EditorGUILayout.TextField("Source Mesh Path", asset.sourceMeshAssetPath ?? string.Empty);
            }

            EditorGUILayout.Vector2Field("UV Min", asset.uvMin);
            EditorGUILayout.Vector2Field("UV Max", asset.uvMax);
            EditorGUILayout.BoundsField("Asset Bounds", asset.assetBounds);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Bake Summary", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Version", asset.version.ToString());
            EditorGUILayout.LabelField("Source Vertices", asset.sourceVertexCount.ToString());
            EditorGUILayout.LabelField("Source Triangles", asset.sourceTriangleCount.ToString());
            EditorGUILayout.LabelField("Clusters", asset.clusterCount.ToString());
            EditorGUILayout.LabelField("Hierarchy Nodes", asset.hierarchyNodeCount.ToString());
            EditorGUILayout.LabelField("Hierarchy Depth", asset.hierarchyDepth.ToString());
            EditorGUILayout.LabelField("Coarse Roots", asset.coarseRootCount.ToString());
            EditorGUILayout.LabelField("Material Ranges", asset.materialRanges != null ? asset.materialRanges.Length.ToString() : "0");
            EditorGUILayout.LabelField("Packed Vertex Bytes", asset.packedVertexDataSizeBytes.ToString());
            EditorGUILayout.LabelField("Packed Index Bytes", asset.packedIndexDataSizeBytes.ToString());
            EditorGUILayout.LabelField("Degenerate Triangles Dropped", asset.droppedDegenerateTriangleCount.ToString());

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Warnings", EditorStyles.boldLabel);
            if (asset.bakeWarnings == null || asset.bakeWarnings.Length == 0)
            {
                EditorGUILayout.HelpBox("No bake warnings.", MessageType.Info);
            }
            else
            {
                for (var i = 0; i < asset.bakeWarnings.Length; i++)
                {
                    EditorGUILayout.HelpBox(asset.bakeWarnings[i].message, MessageType.Warning);
                }
            }

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(asset.sourceMesh == null))
            {
                if (GUILayout.Button("Rebake Asset"))
                {
                    var result = NanoMeshBaker.RebakeAsset(asset);
                    if (result.success)
                    {
                        Debug.Log(result.message, asset);
                    }
                    else
                    {
                        Debug.LogError(result.message, asset);
                    }
                }
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
