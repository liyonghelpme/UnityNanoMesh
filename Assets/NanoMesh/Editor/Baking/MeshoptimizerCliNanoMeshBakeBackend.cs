using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;

namespace NanoMesh.Editor
{
    internal sealed class MeshoptimizerCliNanoMeshBakeBackend : INanoMeshBakeBackend
    {
        private const uint RequestMagic = 0x51424d4e;
        private const uint ResponseMagic = 0x53424d4e;
        private const uint FormatVersion = 2;

        private readonly NanoMeshSettings settings;

        public MeshoptimizerCliNanoMeshBakeBackend(NanoMeshSettings settings)
        {
            this.settings = settings;
        }

        public NanoMeshBakeResult Bake(Mesh mesh, NanoMeshBakeOptions options)
        {
            var result = new NanoMeshBakeResult
            {
                backendName = NanoMeshBakeBackendKind.MeshoptimizerCli.ToString()
            };

            if (settings == null)
            {
                result.success = false;
                result.message = "NanoMeshSettings asset is missing.";
                return result;
            }

            var executablePath = NanoMeshSettingsUtility.ResolvePathFromProjectRoot(settings.bakerExecutablePath);
            result.nativeExecutablePath = executablePath;
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                result.success = false;
                result.message = "NanoMesh baker executable was not found: " + executablePath;
                return result;
            }

            if (!TryCreateRequest(mesh, options, out var request, out var preparationWarnings, out var validationError))
            {
                result.success = false;
                result.message = validationError;
                return result;
            }

            if (preparationWarnings != null && preparationWarnings.Count > 0)
            {
                result.warnings.AddRange(preparationWarnings);
            }

            var tempRoot = NanoMeshSettingsUtility.ResolvePathFromProjectRoot(settings.tempBakeRoot);
            if (string.IsNullOrWhiteSpace(tempRoot))
            {
                result.success = false;
                result.message = "NanoMeshSettings.tempBakeRoot is required.";
                return result;
            }

            Directory.CreateDirectory(tempRoot);
            var token = Guid.NewGuid().ToString("N");
            var requestPath = Path.Combine(tempRoot, token + ".nmbreq");
            var responsePath = Path.Combine(tempRoot, token + ".nmbres");

            try
            {
                WriteRequest(requestPath, request);
                var run = RunProcess(executablePath, "--request " + QuoteArgument(requestPath) + " --response " + QuoteArgument(responsePath));
                if (run.exitCode != 0)
                {
                    result.success = false;
                    result.message = BuildFailureMessage("meshoptimizer CLI exited with code " + run.exitCode + ".", run.stdOut, run.stdErr);
                    return result;
                }

                if (!File.Exists(responsePath))
                {
                    result.success = false;
                    result.message = BuildFailureMessage("meshoptimizer CLI did not produce a response file.", run.stdOut, run.stdErr);
                    return result;
                }

                var response = ReadResponse(responsePath);
                foreach (var warning in response.warnings)
                {
                    result.warnings.Add(warning);
                }

                if (!response.success)
                {
                    result.success = false;
                    result.message = BuildFailureMessage(response.message, run.stdOut, run.stdErr);
                    return result;
                }

                result.success = true;
                result.asset = CreateAsset(mesh, response);
                if (preparationWarnings != null && preparationWarnings.Count > 0)
                {
                    var existingWarnings = result.asset.bakeWarnings ?? Array.Empty<NanoMeshBakeWarning>();
                    var mergedWarnings = new NanoMeshBakeWarning[preparationWarnings.Count + existingWarnings.Length];
                    for (var i = 0; i < preparationWarnings.Count; i++)
                    {
                        mergedWarnings[i] = preparationWarnings[i];
                    }

                    Array.Copy(existingWarnings, 0, mergedWarnings, preparationWarnings.Count, existingWarnings.Length);
                    result.asset.bakeWarnings = mergedWarnings;
                }
                result.message = response.message;
                return result;
            }
            catch (Exception ex)
            {
                result.success = false;
                result.message = "meshoptimizer CLI bake failed: " + ex.Message;
                return result;
            }
            finally
            {
                TryDelete(requestPath);
                TryDelete(responsePath);
            }
        }

        private static bool TryCreateRequest(
            Mesh mesh,
            NanoMeshBakeOptions options,
            out BakeRequest request,
            out List<NanoMeshBakeWarning> warnings,
            out string error)
        {
            request = default;
            warnings = null;
            error = null;
            if (!NanoMeshBaker.TryPrepareMeshForBake(mesh, out var prepared, out error))
            {
                return false;
            }

            request.meshName = mesh.name;
            request.options = options;
            request.positions = prepared.positions;
            request.normals = prepared.normals;
            request.uv0 = prepared.uv0;
            request.submeshes = new List<SubmeshRequest>();
            warnings = prepared.warnings;

            for (var submeshIndex = 0; submeshIndex < prepared.submeshes.Count; submeshIndex++)
            {
                request.submeshes.Add(new SubmeshRequest
                {
                    submeshIndex = prepared.submeshes[submeshIndex].submeshIndex,
                    indices = prepared.submeshes[submeshIndex].indices
                });
            }

            return true;
        }

        private static void WriteRequest(string path, BakeRequest request)
        {
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, false);
            writer.Write(RequestMagic);
            writer.Write(FormatVersion);
            WriteString(writer, request.meshName ?? string.Empty);
            writer.Write(request.options.maxTrianglesPerCluster);
            writer.Write(request.options.maxChildrenPerParent);
            writer.Write(request.options.targetRootCount);
            writer.Write((int)request.options.hierarchyPartitionMode);
            writer.Write((int)request.options.hierarchyAdjacencyMode);
            writer.Write(request.positions.Length);
            writer.Write(request.submeshes.Count);

            for (var i = 0; i < request.positions.Length; i++)
            {
                WriteVector3(writer, request.positions[i]);
                WriteVector3(writer, request.normals[i]);
                WriteVector2(writer, request.uv0[i]);
            }

            for (var i = 0; i < request.submeshes.Count; i++)
            {
                writer.Write(request.submeshes[i].submeshIndex);
                writer.Write(request.submeshes[i].indices.Length);
                for (var indexCursor = 0; indexCursor < request.submeshes[i].indices.Length; indexCursor++)
                {
                    writer.Write(request.submeshes[i].indices[indexCursor]);
                }
            }
        }

        private static BakeResponsePayload ReadResponse(string path)
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8, false);

            var magic = reader.ReadUInt32();
            var version = reader.ReadUInt32();
            if (magic != ResponseMagic)
            {
                throw new InvalidOperationException("NanoMesh bake response magic mismatch.");
            }

            if (version != FormatVersion)
            {
                throw new InvalidOperationException("Unsupported NanoMesh bake response version: " + version);
            }

            var response = new BakeResponsePayload
            {
                success = reader.ReadUInt32() != 0u,
                message = ReadString(reader)
            };

            var warningCount = reader.ReadUInt32();
            response.warnings = new NanoMeshBakeWarning[(int)warningCount];
            for (var i = 0; i < warningCount; i++)
            {
                response.warnings[i] = new NanoMeshBakeWarning
                {
                    code = ReadString(reader),
                    message = ReadString(reader)
                };
            }

            response.assetBoundsCenter = ReadVector3(reader);
            response.assetBoundsExtents = ReadVector3(reader);
            response.uvMin = ReadVector2(reader);
            response.uvMax = ReadVector2(reader);
            response.sourceVertexCount = reader.ReadInt32();
            response.sourceTriangleCount = reader.ReadInt32();
            response.clusterCount = reader.ReadInt32();
            response.hierarchyNodeCount = reader.ReadInt32();
            response.coarseRootCount = reader.ReadInt32();
            response.hierarchyDepth = reader.ReadInt32();
            response.packedVertexStrideBytes = reader.ReadInt32();
            response.packedIndexStrideBytes = reader.ReadInt32();
            response.packedVertexDataSizeBytes = reader.ReadInt32();
            response.packedIndexDataSizeBytes = reader.ReadInt32();
            response.droppedDegenerateTriangleCount = reader.ReadInt32();

            var clusterPayloadCount = reader.ReadUInt32();
            var cullPayloadCount = reader.ReadUInt32();
            var hierarchyPayloadCount = reader.ReadUInt32();
            var coarseRootCount = reader.ReadUInt32();
            var materialRangeCount = reader.ReadUInt32();

            response.clusters = new NanoMeshClusterRecord[(int)clusterPayloadCount];
            for (var i = 0; i < clusterPayloadCount; i++)
            {
                response.clusters[i] = new NanoMeshClusterRecord
                {
                    vertexDataOffsetBytes = reader.ReadInt32(),
                    vertexCount = reader.ReadInt32(),
                    indexDataOffsetBytes = reader.ReadInt32(),
                    indexCount = reader.ReadInt32(),
                    materialRangeIndex = reader.ReadInt32(),
                    hierarchyNodeIndex = reader.ReadInt32(),
                    hierarchyLevel = reader.ReadInt32(),
                    geometricError = reader.ReadSingle(),
                    positionOrigin = ReadVector3(reader),
                    positionExtent = ReadVector3(reader)
                };
            }

            response.clusterCullData = new NanoMeshClusterCullRecord[(int)cullPayloadCount];
            for (var i = 0; i < cullPayloadCount; i++)
            {
                response.clusterCullData[i] = new NanoMeshClusterCullRecord
                {
                    localBounds = new Bounds(ReadVector3(reader), ReadVector3(reader) * 2f),
                    boundingSphere = ReadVector4(reader),
                    normalCone = ReadVector4(reader),
                    geometricError = reader.ReadSingle()
                };
            }

            response.hierarchyNodes = new NanoMeshHierarchyNode[(int)hierarchyPayloadCount];
            for (var i = 0; i < hierarchyPayloadCount; i++)
            {
                response.hierarchyNodes[i] = new NanoMeshHierarchyNode
                {
                    clusterIndex = reader.ReadInt32(),
                    parentNodeIndex = reader.ReadInt32(),
                    firstChildNodeIndex = reader.ReadInt32(),
                    childCount = reader.ReadInt32(),
                    hierarchyLevel = reader.ReadInt32(),
                    localBounds = new Bounds(ReadVector3(reader), ReadVector3(reader) * 2f),
                    boundingSphere = ReadVector4(reader),
                    normalCone = ReadVector4(reader),
                    geometricError = reader.ReadSingle(),
                    isLeaf = reader.ReadUInt32() != 0u
                };
            }

            response.coarseRootNodeIndices = new int[(int)coarseRootCount];
            for (var i = 0; i < coarseRootCount; i++)
            {
                response.coarseRootNodeIndices[i] = reader.ReadInt32();
            }

            response.materialRanges = new NanoMeshSubmeshMaterialRange[(int)materialRangeCount];
            for (var i = 0; i < materialRangeCount; i++)
            {
                response.materialRanges[i] = new NanoMeshSubmeshMaterialRange
                {
                    submeshIndex = reader.ReadInt32(),
                    materialSlot = reader.ReadInt32(),
                    firstClusterIndex = reader.ReadInt32(),
                    clusterCount = reader.ReadInt32(),
                    firstIndexOffsetBytes = reader.ReadInt32(),
                    indexByteCount = reader.ReadInt32()
                };
            }

            response.packedVertexData = reader.ReadBytes(response.packedVertexDataSizeBytes);
            response.packedIndexData = reader.ReadBytes(response.packedIndexDataSizeBytes);
            return response;
        }

        private static NanoMeshAsset CreateAsset(Mesh sourceMesh, BakeResponsePayload response)
        {
            var asset = ScriptableObject.CreateInstance<NanoMeshAsset>();
            asset.version = NanoMeshAsset.CurrentVersion;
            asset.sourceMesh = sourceMesh;
            asset.sourceMeshAssetPath = UnityEditor.AssetDatabase.GetAssetPath(sourceMesh);
            asset.assetBounds = new Bounds(response.assetBoundsCenter, response.assetBoundsExtents * 2f);
            asset.uvMin = response.uvMin;
            asset.uvMax = response.uvMax;
            asset.sourceVertexCount = response.sourceVertexCount;
            asset.sourceTriangleCount = response.sourceTriangleCount;
            asset.leafClusterCount = response.clusterCount;
            asset.clusterCount = response.clusterCount;
            asset.hierarchyNodeCount = response.hierarchyNodeCount;
            asset.coarseRootCount = response.coarseRootCount;
            asset.hierarchyDepth = response.hierarchyDepth;
            asset.packedVertexStrideBytes = response.packedVertexStrideBytes;
            asset.packedIndexStrideBytes = response.packedIndexStrideBytes;
            asset.packedVertexDataSizeBytes = response.packedVertexDataSizeBytes;
            asset.packedIndexDataSizeBytes = response.packedIndexDataSizeBytes;
            asset.droppedDegenerateTriangleCount = response.droppedDegenerateTriangleCount;
            asset.clusters = response.clusters ?? Array.Empty<NanoMeshClusterRecord>();
            asset.clusterCullData = response.clusterCullData ?? Array.Empty<NanoMeshClusterCullRecord>();
            asset.hierarchyNodes = response.hierarchyNodes ?? Array.Empty<NanoMeshHierarchyNode>();
            asset.coarseRootNodeIndices = response.coarseRootNodeIndices ?? Array.Empty<int>();
            asset.materialRanges = response.materialRanges ?? Array.Empty<NanoMeshSubmeshMaterialRange>();
            asset.packedVertexData = response.packedVertexData ?? Array.Empty<byte>();
            asset.packedIndexData = response.packedIndexData ?? Array.Empty<byte>();
            asset.bakeWarnings = response.warnings ?? Array.Empty<NanoMeshBakeWarning>();
            return asset;
        }

        private static (int exitCode, string stdOut, string stdErr) RunProcess(string executablePath, string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? NanoMeshSettingsUtility.GetProjectRoot(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to launch NanoMesh baker process.");
            }

            var stdOut = process.StandardOutput.ReadToEnd();
            var stdErr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, stdOut, stdErr);
        }

        private static string BuildFailureMessage(string prefix, string stdOut, string stdErr)
        {
            if (!string.IsNullOrWhiteSpace(stdErr))
            {
                return prefix + " stderr: " + stdErr.Trim();
            }

            if (!string.IsNullOrWhiteSpace(stdOut))
            {
                return prefix + " stdout: " + stdOut.Trim();
            }

            return prefix;
        }

        private static string QuoteArgument(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static void TryDelete(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private static void WriteVector2(BinaryWriter writer, Vector2 value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            writer.Write((uint)bytes.Length);
            writer.Write(bytes);
        }

        private static void WriteVector3(BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
        }

        private static Vector2 ReadVector2(BinaryReader reader)
        {
            return new Vector2(reader.ReadSingle(), reader.ReadSingle());
        }

        private static string ReadString(BinaryReader reader)
        {
            var length = reader.ReadUInt32();
            var bytes = reader.ReadBytes((int)length);
            return Encoding.UTF8.GetString(bytes);
        }

        private static Vector3 ReadVector3(BinaryReader reader)
        {
            return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        private static Vector4 ReadVector4(BinaryReader reader)
        {
            return new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        private struct SubmeshRequest
        {
            public int submeshIndex;
            public int[] indices;
        }

        private struct BakeRequest
        {
            public string meshName;
            public NanoMeshBakeOptions options;
            public Vector3[] positions;
            public Vector3[] normals;
            public Vector2[] uv0;
            public List<SubmeshRequest> submeshes;
        }

        private struct BakeResponsePayload
        {
            public bool success;
            public string message;
            public NanoMeshBakeWarning[] warnings;
            public Vector3 assetBoundsCenter;
            public Vector3 assetBoundsExtents;
            public Vector2 uvMin;
            public Vector2 uvMax;
            public int sourceVertexCount;
            public int sourceTriangleCount;
            public int clusterCount;
            public int hierarchyNodeCount;
            public int coarseRootCount;
            public int hierarchyDepth;
            public int packedVertexStrideBytes;
            public int packedIndexStrideBytes;
            public int packedVertexDataSizeBytes;
            public int packedIndexDataSizeBytes;
            public int droppedDegenerateTriangleCount;
            public NanoMeshClusterRecord[] clusters;
            public NanoMeshClusterCullRecord[] clusterCullData;
            public NanoMeshHierarchyNode[] hierarchyNodes;
            public int[] coarseRootNodeIndices;
            public NanoMeshSubmeshMaterialRange[] materialRanges;
            public byte[] packedVertexData;
            public byte[] packedIndexData;
        }
    }
}
