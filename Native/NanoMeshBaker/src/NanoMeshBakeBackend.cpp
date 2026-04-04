#include "NanoMeshBakeBackend.h"

#include "meshoptimizer.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <fstream>
#include <limits>
#include <map>
#include <set>
#include <string>
#include <sstream>
#include <utility>
#include <vector>

namespace
{
    constexpr uint32_t kRequestMagic = 0x51424d4e;  // NMBQ
    constexpr uint32_t kResponseMagic = 0x53424d4e; // NMBS
    constexpr uint32_t kFormatVersion = 2;
    constexpr float kDegenerateAreaEpsilon = 1e-12f;
    constexpr uint32_t kPackedVertexStrideBytes = 16;
    constexpr uint32_t kPackedIndexStrideBytes = 2;
    constexpr size_t kMeshletMaxVertices = 64;
    constexpr float kMeshletConeWeight = 0.25f;
    constexpr float kSimplificationTargetRatio = 0.5f;
    constexpr float kSimplificationPoorRatio = 0.9f;
    constexpr float kParentErrorScale = 0.25f;
    constexpr uint32_t kHierarchyPartitionContiguous = 0;
    constexpr uint32_t kHierarchyPartitionMetisAdjacency = 1;
    constexpr uint32_t kHierarchyAdjacencySharedEdge = 0;
    constexpr uint32_t kHierarchyAdjacencySharedVertex = 1;

    struct BinaryWriter
    {
        explicit BinaryWriter(const std::string& path)
            : stream(path, std::ios::binary)
        {
        }

        template <typename T>
        void write(const T& value)
        {
            stream.write(reinterpret_cast<const char*>(&value), sizeof(T));
        }

        void writeBytes(const void* data, size_t size)
        {
            stream.write(reinterpret_cast<const char*>(data), static_cast<std::streamsize>(size));
        }

        void writeString(const std::string& value)
        {
            uint32_t size = static_cast<uint32_t>(value.size());
            write(size);
            if (size > 0)
            {
                writeBytes(value.data(), size);
            }
        }

        bool good() const
        {
            return stream.good();
        }

        std::ofstream stream;
    };

    struct BinaryReader
    {
        explicit BinaryReader(const std::string& path)
            : stream(path, std::ios::binary)
        {
        }

        template <typename T>
        bool read(T& value)
        {
            stream.read(reinterpret_cast<char*>(&value), sizeof(T));
            return stream.good();
        }

        bool readBytes(void* data, size_t size)
        {
            stream.read(reinterpret_cast<char*>(data), static_cast<std::streamsize>(size));
            return stream.good();
        }

        bool readString(std::string& outValue)
        {
            uint32_t size = 0;
            if (!read(size))
            {
                return false;
            }

            outValue.resize(size);
            if (size == 0)
            {
                return true;
            }

            return readBytes(outValue.data(), size);
        }

        std::ifstream stream;
    };

    struct SourceVertex
    {
        nanomesh::Float3 position{};
        nanomesh::Float3 normal{};
        nanomesh::Float2 uv{};
    };

    struct LeafCluster
    {
        int submeshIndex = 0;
        int materialRangeIndex = -1;
        int hierarchyNodeIndex = -1;
        int hierarchyLevel = 0;
        int vertexDataOffsetBytes = 0;
        int indexDataOffsetBytes = 0;
        float geometricError = 0.0f;
        nanomesh::Bounds bounds{};
        nanomesh::Float4 boundingSphere{};
        nanomesh::Float4 normalCone{};
        std::vector<uint32_t> sourceVertexRefs;
        std::vector<SourceVertex> vertices;
        std::vector<uint16_t> microIndices;
        std::vector<uint32_t> sourceTriangleIndices;
    };

    struct NodeBuildData
    {
        int clusterIndex = -1;
        int submeshIndex = -1;
        int parentNodeIndex = -1;
        int firstChildNodeIndex = -1;
        int childCount = 0;
        int hierarchyLevel = 0;
        nanomesh::Bounds bounds{};
        nanomesh::Float4 boundingSphere{};
        nanomesh::Float4 normalCone{};
        float geometricError = 0.0f;
        bool isLeaf = false;
        std::vector<int> leafClusterIndices;
    };

    struct SimplificationResult
    {
        std::vector<uint32_t> indices;
        float error = 0.0f;
        bool reduced = false;
        bool meaningful = false;
    };

    struct LeafAdjacencyGraph
    {
        std::vector<std::vector<int>> neighbors;
    };

    nanomesh::Float3 makeFloat3(float x, float y, float z)
    {
        nanomesh::Float3 value{};
        value.x = x;
        value.y = y;
        value.z = z;
        return value;
    }

    nanomesh::Float4 makeFloat4(float x, float y, float z, float w)
    {
        nanomesh::Float4 value{};
        value.x = x;
        value.y = y;
        value.z = z;
        value.w = w;
        return value;
    }

    nanomesh::Bounds makeBounds(const nanomesh::Float3& minValue, const nanomesh::Float3& maxValue)
    {
        nanomesh::Bounds bounds{};
        bounds.center = makeFloat3(
            (minValue.x + maxValue.x) * 0.5f,
            (minValue.y + maxValue.y) * 0.5f,
            (minValue.z + maxValue.z) * 0.5f);
        bounds.extents = makeFloat3(
            (maxValue.x - minValue.x) * 0.5f,
            (maxValue.y - minValue.y) * 0.5f,
            (maxValue.z - minValue.z) * 0.5f);
        return bounds;
    }

    nanomesh::Float3 boundsMin(const nanomesh::Bounds& bounds)
    {
        return makeFloat3(
            bounds.center.x - bounds.extents.x,
            bounds.center.y - bounds.extents.y,
            bounds.center.z - bounds.extents.z);
    }

    nanomesh::Float3 boundsMax(const nanomesh::Bounds& bounds)
    {
        return makeFloat3(
            bounds.center.x + bounds.extents.x,
            bounds.center.y + bounds.extents.y,
            bounds.center.z + bounds.extents.z);
    }

    float length3(const nanomesh::Float3& value)
    {
        return std::sqrt(value.x * value.x + value.y * value.y + value.z * value.z);
    }

    nanomesh::Float3 normalize3(const nanomesh::Float3& value, const nanomesh::Float3& fallback = makeFloat3(0.0f, 0.0f, 1.0f))
    {
        const float len = length3(value);
        if (len <= 1e-8f)
        {
            return fallback;
        }

        return makeFloat3(value.x / len, value.y / len, value.z / len);
    }

    float dot3(const nanomesh::Float3& a, const nanomesh::Float3& b)
    {
        return a.x * b.x + a.y * b.y + a.z * b.z;
    }

    nanomesh::Float3 add3(const nanomesh::Float3& a, const nanomesh::Float3& b)
    {
        return makeFloat3(a.x + b.x, a.y + b.y, a.z + b.z);
    }

    nanomesh::Float3 sub3(const nanomesh::Float3& a, const nanomesh::Float3& b)
    {
        return makeFloat3(a.x - b.x, a.y - b.y, a.z - b.z);
    }

    nanomesh::Float3 cross3(const nanomesh::Float3& a, const nanomesh::Float3& b)
    {
        return makeFloat3(
            a.y * b.z - a.z * b.y,
            a.z * b.x - a.x * b.z,
            a.x * b.y - a.y * b.x);
    }

    void appendWarning(std::vector<nanomesh::BakeWarning>& warnings, const std::string& code, const std::string& message)
    {
        warnings.push_back({code, message});
    }

    uint64_t makeUndirectedEdgeKey(uint32_t a, uint32_t b)
    {
        const uint32_t lo = std::min(a, b);
        const uint32_t hi = std::max(a, b);
        return (static_cast<uint64_t>(lo) << 32ull) | static_cast<uint64_t>(hi);
    }

    LeafAdjacencyGraph buildLeafAdjacencyGraph(
        const std::vector<LeafCluster>& leafClusters,
        uint32_t adjacencyMode)
    {
        LeafAdjacencyGraph graph{};
        graph.neighbors.resize(leafClusters.size());
        std::vector<std::set<int>> neighborSets(leafClusters.size());
        std::map<int, std::vector<int>> leavesBySubmesh;

        for (size_t leafIndex = 0; leafIndex < leafClusters.size(); ++leafIndex)
        {
            leavesBySubmesh[leafClusters[leafIndex].submeshIndex].push_back(static_cast<int>(leafIndex));
        }

        for (const auto& submeshPair : leavesBySubmesh)
        {
            if (adjacencyMode == kHierarchyAdjacencySharedVertex)
            {
                std::map<uint32_t, std::vector<int>> vertexToLeaves;
                for (int leafIndex : submeshPair.second)
                {
                    for (uint32_t vertexIndex : leafClusters[static_cast<size_t>(leafIndex)].sourceVertexRefs)
                    {
                        vertexToLeaves[vertexIndex].push_back(leafIndex);
                    }
                }

                for (const auto& vertexPair : vertexToLeaves)
                {
                    const std::vector<int>& sharingLeaves = vertexPair.second;
                    for (size_t i = 0; i < sharingLeaves.size(); ++i)
                    {
                        for (size_t j = i + 1; j < sharingLeaves.size(); ++j)
                        {
                            neighborSets[static_cast<size_t>(sharingLeaves[i])].insert(sharingLeaves[j]);
                            neighborSets[static_cast<size_t>(sharingLeaves[j])].insert(sharingLeaves[i]);
                        }
                    }
                }
            }
            else
            {
                std::map<uint64_t, std::vector<int>> edgeToLeaves;
                for (int leafIndex : submeshPair.second)
                {
                    const std::vector<uint32_t>& indices = leafClusters[static_cast<size_t>(leafIndex)].sourceTriangleIndices;
                    for (size_t i = 0; i + 2 < indices.size(); i += 3)
                    {
                        edgeToLeaves[makeUndirectedEdgeKey(indices[i + 0], indices[i + 1])].push_back(leafIndex);
                        edgeToLeaves[makeUndirectedEdgeKey(indices[i + 1], indices[i + 2])].push_back(leafIndex);
                        edgeToLeaves[makeUndirectedEdgeKey(indices[i + 2], indices[i + 0])].push_back(leafIndex);
                    }
                }

                for (const auto& edgePair : edgeToLeaves)
                {
                    std::set<int> uniqueLeaves(edgePair.second.begin(), edgePair.second.end());
                    std::vector<int> sharingLeaves(uniqueLeaves.begin(), uniqueLeaves.end());
                    for (size_t i = 0; i < sharingLeaves.size(); ++i)
                    {
                        for (size_t j = i + 1; j < sharingLeaves.size(); ++j)
                        {
                            neighborSets[static_cast<size_t>(sharingLeaves[i])].insert(sharingLeaves[j]);
                            neighborSets[static_cast<size_t>(sharingLeaves[j])].insert(sharingLeaves[i]);
                        }
                    }
                }
            }
        }

        for (size_t leafIndex = 0; leafIndex < neighborSets.size(); ++leafIndex)
        {
            graph.neighbors[leafIndex].assign(neighborSets[leafIndex].begin(), neighborSets[leafIndex].end());
        }

        return graph;
    }

    int countRestrictedConnectedComponents(
        const LeafAdjacencyGraph& graph,
        const std::vector<int>& leafIndices)
    {
        if (leafIndices.empty())
        {
            return 0;
        }

        std::set<int> allowed(leafIndices.begin(), leafIndices.end());
        std::set<int> visited;
        int componentCount = 0;

        for (int startLeaf : leafIndices)
        {
            if (visited.find(startLeaf) != visited.end())
            {
                continue;
            }

            ++componentCount;
            std::vector<int> frontier = { startLeaf };
            visited.insert(startLeaf);
            while (!frontier.empty())
            {
                const int current = frontier.back();
                frontier.pop_back();

                for (int neighbor : graph.neighbors[static_cast<size_t>(current)])
                {
                    if (allowed.find(neighbor) == allowed.end() || visited.find(neighbor) != visited.end())
                    {
                        continue;
                    }

                    visited.insert(neighbor);
                    frontier.push_back(neighbor);
                }
            }
        }

        return componentCount;
    }

    bool validateRequest(const nanomesh::BakeRequest& request, std::string& outError)
    {
        if (request.positions.empty())
        {
            outError = "Request has no positions.";
            return false;
        }

        if (request.positions.size() != request.normals.size() || request.positions.size() != request.uv0.size())
        {
            outError = "Request attribute array sizes do not match.";
            return false;
        }

        if (request.submeshes.empty())
        {
            outError = "Request has no submeshes.";
            return false;
        }

        if (request.options.hierarchyPartitionMode > kHierarchyPartitionMetisAdjacency)
        {
            outError = "Request hierarchy partition mode is invalid.";
            return false;
        }

        if (request.options.hierarchyAdjacencyMode > kHierarchyAdjacencySharedVertex)
        {
            outError = "Request hierarchy adjacency mode is invalid.";
            return false;
        }

        return true;
    }

    nanomesh::Bounds computeBoundsFromVertices(const std::vector<SourceVertex>& vertices)
    {
        nanomesh::Float3 minValue = vertices.front().position;
        nanomesh::Float3 maxValue = vertices.front().position;
        for (const SourceVertex& vertex : vertices)
        {
            minValue.x = std::min(minValue.x, vertex.position.x);
            minValue.y = std::min(minValue.y, vertex.position.y);
            minValue.z = std::min(minValue.z, vertex.position.z);
            maxValue.x = std::max(maxValue.x, vertex.position.x);
            maxValue.y = std::max(maxValue.y, vertex.position.y);
            maxValue.z = std::max(maxValue.z, vertex.position.z);
        }

        return makeBounds(minValue, maxValue);
    }

    float computeTriangleAreaSquared(const nanomesh::Float3& a, const nanomesh::Float3& b, const nanomesh::Float3& c)
    {
        const nanomesh::Float3 ab = sub3(b, a);
        const nanomesh::Float3 ac = sub3(c, a);
        const nanomesh::Float3 cross = cross3(ab, ac);
        return dot3(cross, cross);
    }

    nanomesh::Float4 computeFallbackCone(const std::vector<SourceVertex>& vertices)
    {
        nanomesh::Float3 accumulated = {};
        for (const SourceVertex& vertex : vertices)
        {
            accumulated = add3(accumulated, normalize3(vertex.normal));
        }

        const nanomesh::Float3 axis = normalize3(accumulated);
        float minDot = 1.0f;
        for (const SourceVertex& vertex : vertices)
        {
            minDot = std::min(minDot, dot3(axis, normalize3(vertex.normal)));
        }

        return makeFloat4(axis.x, axis.y, axis.z, std::clamp(minDot, -1.0f, 1.0f));
    }

    nanomesh::Float4 convertMeshoptCone(const meshopt_Bounds& bounds, const std::vector<SourceVertex>& vertices)
    {
        if (std::isfinite(bounds.cone_cutoff) && bounds.cone_cutoff >= -1.0f && bounds.cone_cutoff <= 1.0f)
        {
            return makeFloat4(bounds.cone_axis[0], bounds.cone_axis[1], bounds.cone_axis[2], bounds.cone_cutoff);
        }

        return computeFallbackCone(vertices);
    }

    void buildLeafClusters(
        const nanomesh::BakeRequest& request,
        std::vector<LeafCluster>& outLeafClusters,
        std::vector<nanomesh::BakeWarning>& warnings,
        int32_t& outDroppedDegenerateTriangles,
        nanomesh::Float2& outUvMin,
        nanomesh::Float2& outUvMax,
        nanomesh::Bounds& outAssetBounds,
        int32_t& outSourceTriangleCount)
    {
        outDroppedDegenerateTriangles = 0;
        outLeafClusters.clear();

        nanomesh::Float3 assetMin = request.positions.front();
        nanomesh::Float3 assetMax = request.positions.front();
        outUvMin = request.uv0.front();
        outUvMax = request.uv0.front();

        for (size_t i = 0; i < request.positions.size(); ++i)
        {
            const nanomesh::Float3& position = request.positions[i];
            assetMin.x = std::min(assetMin.x, position.x);
            assetMin.y = std::min(assetMin.y, position.y);
            assetMin.z = std::min(assetMin.z, position.z);
            assetMax.x = std::max(assetMax.x, position.x);
            assetMax.y = std::max(assetMax.y, position.y);
            assetMax.z = std::max(assetMax.z, position.z);

            outUvMin.x = std::min(outUvMin.x, request.uv0[i].x);
            outUvMin.y = std::min(outUvMin.y, request.uv0[i].y);
            outUvMax.x = std::max(outUvMax.x, request.uv0[i].x);
            outUvMax.y = std::max(outUvMax.y, request.uv0[i].y);
        }

        outAssetBounds = makeBounds(assetMin, assetMax);
        outSourceTriangleCount = 0;

        for (const nanomesh::SubmeshInput& submesh : request.submeshes)
        {
            std::vector<uint32_t> cleanedIndices;
            cleanedIndices.reserve(submesh.indices.size());

            for (size_t i = 0; i + 2 < submesh.indices.size(); i += 3)
            {
                const uint32_t ia = submesh.indices[i + 0];
                const uint32_t ib = submesh.indices[i + 1];
                const uint32_t ic = submesh.indices[i + 2];

                if (ia >= request.positions.size() || ib >= request.positions.size() || ic >= request.positions.size())
                {
                    ++outDroppedDegenerateTriangles;
                    continue;
                }

                if (ia == ib || ib == ic || ia == ic)
                {
                    ++outDroppedDegenerateTriangles;
                    continue;
                }

                const float areaSq = computeTriangleAreaSquared(request.positions[ia], request.positions[ib], request.positions[ic]);
                if (areaSq <= kDegenerateAreaEpsilon)
                {
                    ++outDroppedDegenerateTriangles;
                    continue;
                }

                cleanedIndices.push_back(ia);
                cleanedIndices.push_back(ib);
                cleanedIndices.push_back(ic);
            }

            outSourceTriangleCount += static_cast<int32_t>(cleanedIndices.size() / 3);
            if (cleanedIndices.empty())
            {
                continue;
            }

            const size_t maxMeshlets = meshopt_buildMeshletsBound(
                cleanedIndices.size(),
                kMeshletMaxVertices,
                std::max<size_t>(1, request.options.maxTrianglesPerCluster));
            std::vector<meshopt_Meshlet> meshlets(maxMeshlets);
            std::vector<unsigned int> meshletVertices(cleanedIndices.size());
            std::vector<unsigned char> meshletTriangles(cleanedIndices.size());

            const size_t meshletCount = meshopt_buildMeshlets(
                meshlets.data(),
                meshletVertices.data(),
                meshletTriangles.data(),
                cleanedIndices.data(),
                cleanedIndices.size(),
                &request.positions[0].x,
                request.positions.size(),
                sizeof(nanomesh::Float3),
                kMeshletMaxVertices,
                std::max<size_t>(1, request.options.maxTrianglesPerCluster),
                kMeshletConeWeight);

            for (size_t meshletIndex = 0; meshletIndex < meshletCount; ++meshletIndex)
            {
                const meshopt_Meshlet& meshlet = meshlets[meshletIndex];
                if (meshlet.triangle_count == 0 || meshlet.vertex_count == 0)
                {
                    continue;
                }

                meshopt_optimizeMeshlet(
                    &meshletVertices[meshlet.vertex_offset],
                    &meshletTriangles[meshlet.triangle_offset],
                    meshlet.triangle_count,
                    meshlet.vertex_count);

                LeafCluster cluster{};
                cluster.submeshIndex = static_cast<int>(submesh.submeshIndex);
                cluster.sourceVertexRefs.reserve(meshlet.vertex_count);
                cluster.vertices.reserve(meshlet.vertex_count);
                cluster.microIndices.reserve(meshlet.triangle_count * 3);
                cluster.sourceTriangleIndices.reserve(meshlet.triangle_count * 3);

                for (unsigned int vertexCursor = 0; vertexCursor < meshlet.vertex_count; ++vertexCursor)
                {
                    const uint32_t sourceVertexIndex = meshletVertices[meshlet.vertex_offset + vertexCursor];
                    cluster.sourceVertexRefs.push_back(sourceVertexIndex);
                    cluster.vertices.push_back(SourceVertex
                    {
                        request.positions[sourceVertexIndex],
                        normalize3(request.normals[sourceVertexIndex]),
                        request.uv0[sourceVertexIndex]
                    });
                }

                for (unsigned int triCursor = 0; triCursor < meshlet.triangle_count * 3; ++triCursor)
                {
                    const uint8_t localIndex = meshletTriangles[meshlet.triangle_offset + triCursor];
                    cluster.microIndices.push_back(localIndex);
                    cluster.sourceTriangleIndices.push_back(cluster.sourceVertexRefs[localIndex]);
                }

                cluster.bounds = computeBoundsFromVertices(cluster.vertices);
                const meshopt_Bounds meshletBounds = meshopt_computeMeshletBounds(
                    cluster.sourceVertexRefs.data(),
                    &meshletTriangles[meshlet.triangle_offset],
                    meshlet.triangle_count,
                    &request.positions[0].x,
                    request.positions.size(),
                    sizeof(nanomesh::Float3));
                cluster.boundingSphere = makeFloat4(
                    meshletBounds.center[0],
                    meshletBounds.center[1],
                    meshletBounds.center[2],
                    meshletBounds.radius);
                cluster.normalCone = convertMeshoptCone(meshletBounds, cluster.vertices);
                cluster.geometricError = cluster.bounds.extents.x + cluster.bounds.extents.y + cluster.bounds.extents.z;
                cluster.geometricError = std::max(1e-5f, cluster.geometricError / std::max(1u, meshlet.triangle_count));

                outLeafClusters.push_back(std::move(cluster));
            }
        }

        if (outDroppedDegenerateTriangles > 0)
        {
            appendWarning(
                warnings,
                "DegenerateTrianglesDropped",
                "Dropped " + std::to_string(outDroppedDegenerateTriangles) + " degenerate source triangles during bake.");
        }
    }

    void buildClusterFromSourceIndices(
        const nanomesh::BakeRequest& request,
        int submeshIndex,
        const std::vector<uint32_t>& sourceTriangleIndices,
        LeafCluster& outCluster)
    {
        outCluster = LeafCluster{};
        outCluster.submeshIndex = submeshIndex;
        outCluster.sourceTriangleIndices = sourceTriangleIndices;

        std::map<uint32_t, uint16_t> localVertexLookup;
        outCluster.sourceVertexRefs.reserve(sourceTriangleIndices.size());
        outCluster.vertices.reserve(sourceTriangleIndices.size());
        outCluster.microIndices.reserve(sourceTriangleIndices.size());

        for (uint32_t sourceVertexIndex : sourceTriangleIndices)
        {
            auto existing = localVertexLookup.find(sourceVertexIndex);
            uint16_t localIndex = 0;
            if (existing == localVertexLookup.end())
            {
                localIndex = static_cast<uint16_t>(outCluster.vertices.size());
                localVertexLookup.emplace(sourceVertexIndex, localIndex);
                outCluster.sourceVertexRefs.push_back(sourceVertexIndex);
                outCluster.vertices.push_back(SourceVertex
                {
                    request.positions[sourceVertexIndex],
                    normalize3(request.normals[sourceVertexIndex]),
                    request.uv0[sourceVertexIndex]
                });
            }
            else
            {
                localIndex = existing->second;
            }

            outCluster.microIndices.push_back(localIndex);
        }

        outCluster.bounds = computeBoundsFromVertices(outCluster.vertices);
        outCluster.boundingSphere = makeFloat4(
            outCluster.bounds.center.x,
            outCluster.bounds.center.y,
            outCluster.bounds.center.z,
            length3(outCluster.bounds.extents));
        outCluster.normalCone = computeFallbackCone(outCluster.vertices);
        const uint32_t triangleCount = static_cast<uint32_t>(outCluster.microIndices.size() / 3);
        outCluster.geometricError = outCluster.bounds.extents.x + outCluster.bounds.extents.y + outCluster.bounds.extents.z;
        outCluster.geometricError = std::max(1e-5f, outCluster.geometricError / std::max(1u, triangleCount));
    }

    nanomesh::Float4 computeNodeCone(const std::vector<NodeBuildData>& nodes, const std::vector<int>& childNodeIndices)
    {
        nanomesh::Float3 accumulated = {};
        float minCutoff = 1.0f;
        for (int childNodeIndex : childNodeIndices)
        {
            const NodeBuildData& child = nodes[childNodeIndex];
            accumulated = add3(accumulated, makeFloat3(child.normalCone.x, child.normalCone.y, child.normalCone.z));
            minCutoff = std::min(minCutoff, child.normalCone.w);
        }

        const nanomesh::Float3 axis = normalize3(accumulated);
        return makeFloat4(axis.x, axis.y, axis.z, minCutoff);
    }

    std::vector<int> collectFlattenedClusterIndices(const std::vector<int>& nodeIndices, const std::vector<NodeBuildData>& nodes)
    {
        std::vector<int> result;
        for (int nodeIndex : nodeIndices)
        {
            const std::vector<int>& leaves = nodes[nodeIndex].leafClusterIndices;
            result.insert(result.end(), leaves.begin(), leaves.end());
        }

        std::sort(result.begin(), result.end());
        result.erase(std::unique(result.begin(), result.end()), result.end());
        return result;
    }

    SimplificationResult attemptParentSimplification(
        const nanomesh::BakeRequest& request,
        const std::vector<LeafCluster>& leafClusters,
        const std::vector<int>& leafClusterIndices)
    {
        SimplificationResult result{};
        std::vector<uint32_t> indices;
        for (int leafClusterIndex : leafClusterIndices)
        {
            const LeafCluster& cluster = leafClusters[leafClusterIndex];
            indices.insert(indices.end(), cluster.sourceTriangleIndices.begin(), cluster.sourceTriangleIndices.end());
        }

        result.indices = indices;
        if (indices.size() < 6)
        {
            return result;
        }

        std::vector<float> attributes(request.positions.size() * 5, 0.0f);
        for (size_t vertexIndex = 0; vertexIndex < request.positions.size(); ++vertexIndex)
        {
            const size_t base = vertexIndex * 5;
            attributes[base + 0] = request.normals[vertexIndex].x;
            attributes[base + 1] = request.normals[vertexIndex].y;
            attributes[base + 2] = request.normals[vertexIndex].z;
            attributes[base + 3] = request.uv0[vertexIndex].x;
            attributes[base + 4] = request.uv0[vertexIndex].y;
        }

        const std::array<float, 5> attributeWeights = {0.7f, 0.7f, 0.7f, 0.3f, 0.3f};
        const size_t targetIndexCount = std::max<size_t>(3, static_cast<size_t>(indices.size() * kSimplificationTargetRatio));
        std::vector<uint32_t> simplified(indices.size());
        float resultError = 0.0f;
        const size_t simplifiedIndexCount = meshopt_simplifyWithAttributes(
            simplified.data(),
            indices.data(),
            indices.size(),
            &request.positions[0].x,
            request.positions.size(),
            sizeof(nanomesh::Float3),
            attributes.data(),
            sizeof(float) * 5,
            attributeWeights.data(),
            attributeWeights.size(),
            nullptr,
            targetIndexCount,
            std::numeric_limits<float>::max(),
            meshopt_SimplifyErrorAbsolute,
            &resultError);

        result.error = resultError;
        result.reduced = simplifiedIndexCount > 0 && simplifiedIndexCount < indices.size();
        result.meaningful = result.reduced && static_cast<double>(simplifiedIndexCount) <= static_cast<double>(indices.size()) * kSimplificationPoorRatio;
        if (simplifiedIndexCount >= 3)
        {
            simplified.resize(simplifiedIndexCount);
            result.indices = std::move(simplified);
        }

        return result;
    }

    void buildHierarchy(
        const nanomesh::BakeRequest& request,
        const std::vector<LeafCluster>& leafClusters,
        std::vector<NodeBuildData>& outNodes,
        std::vector<int32_t>& outCoarseRoots,
        int32_t& outHierarchyDepth,
        std::vector<nanomesh::BakeWarning>& warnings)
    {
        outNodes.clear();
        outNodes.reserve(leafClusters.size() * 2);
        const LeafAdjacencyGraph leafAdjacency = buildLeafAdjacencyGraph(leafClusters, request.options.hierarchyAdjacencyMode);

        std::vector<int> currentLevel;
        currentLevel.reserve(leafClusters.size());
        for (size_t i = 0; i < leafClusters.size(); ++i)
        {
            NodeBuildData node{};
            node.clusterIndex = static_cast<int>(i);
            node.submeshIndex = leafClusters[i].submeshIndex;
            node.hierarchyLevel = 0;
            node.bounds = leafClusters[i].bounds;
            node.boundingSphere = leafClusters[i].boundingSphere;
            node.normalCone = leafClusters[i].normalCone;
            node.geometricError = leafClusters[i].geometricError;
            node.isLeaf = true;
            node.leafClusterIndices.push_back(static_cast<int>(i));
            outNodes.push_back(node);
            currentLevel.push_back(static_cast<int>(i));
        }

        outHierarchyDepth = leafClusters.empty() ? 0 : 1;
        const uint32_t targetRootCount = std::max<uint32_t>(1, request.options.targetRootCount);
        const uint32_t partitionSize = std::max<uint32_t>(2, request.options.maxChildrenPerParent);

        while (currentLevel.size() > targetRootCount)
        {
            std::vector<int> nextLevel;
            bool anyMeaningfulReduction = false;
            bool anyStructuralReduction = false;
            std::map<int, std::vector<int>> currentLevelBySubmesh;
            for (int nodeIndex : currentLevel)
            {
                currentLevelBySubmesh[outNodes[nodeIndex].submeshIndex].push_back(nodeIndex);
            }

            for (const auto& submeshPair : currentLevelBySubmesh)
            {
                std::vector<int> sortedNodeIndices = submeshPair.second;
                std::sort(sortedNodeIndices.begin(), sortedNodeIndices.end(), [&outNodes](int left, int right)
                {
                    return outNodes[left].leafClusterIndices.front() < outNodes[right].leafClusterIndices.front();
                });

                if (sortedNodeIndices.size() <= targetRootCount)
                {
                    nextLevel.insert(nextLevel.end(), sortedNodeIndices.begin(), sortedNodeIndices.end());
                    continue;
                }

                std::map<unsigned int, std::vector<int>> partitionMap;
                if (request.options.hierarchyPartitionMode == kHierarchyPartitionContiguous)
                {
                    for (size_t i = 0; i < sortedNodeIndices.size(); ++i)
                    {
                        partitionMap[static_cast<unsigned int>(i / partitionSize)].push_back(sortedNodeIndices[i]);
                    }
                }
                else
                {
                    std::vector<uint32_t> flattenedClusterIndices;
                    std::vector<uint32_t> clusterIndexCounts;
                    clusterIndexCounts.reserve(sortedNodeIndices.size());

                    for (int nodeIndex : sortedNodeIndices)
                    {
                        const std::vector<int>& leaves = outNodes[nodeIndex].leafClusterIndices;
                        uint32_t countForNode = 0;
                        for (int leafIndex : leaves)
                        {
                            const std::vector<uint32_t>& sourceIndices = leafClusters[static_cast<size_t>(leafIndex)].sourceTriangleIndices;
                            flattenedClusterIndices.insert(flattenedClusterIndices.end(), sourceIndices.begin(), sourceIndices.end());
                            countForNode += static_cast<uint32_t>(sourceIndices.size());
                        }

                        clusterIndexCounts.push_back(countForNode);
                    }

                    std::vector<unsigned int> partitions(sortedNodeIndices.size(), 0);
                    const size_t partitionCount = meshopt_partitionClusters(
                        partitions.data(),
                        flattenedClusterIndices.data(),
                        flattenedClusterIndices.size(),
                        clusterIndexCounts.data(),
                        sortedNodeIndices.size(),
                        &request.positions[0].x,
                        request.positions.size(),
                        sizeof(nanomesh::Float3),
                        partitionSize);

                    if (partitionCount >= sortedNodeIndices.size())
                    {
                        nextLevel.insert(nextLevel.end(), sortedNodeIndices.begin(), sortedNodeIndices.end());
                        continue;
                    }

                    for (size_t i = 0; i < sortedNodeIndices.size(); ++i)
                    {
                        partitionMap[partitions[i]].push_back(sortedNodeIndices[i]);
                    }
                }

                for (const auto& pair : partitionMap)
                {
                    const std::vector<int>& childNodeIndices = pair.second;
                    if (childNodeIndices.empty())
                    {
                        continue;
                    }

                    if (childNodeIndices.size() == 1)
                    {
                        nextLevel.push_back(childNodeIndices.front());
                        continue;
                    }

                    nanomesh::Float3 minValue = boundsMin(outNodes[childNodeIndices.front()].bounds);
                    nanomesh::Float3 maxValue = boundsMax(outNodes[childNodeIndices.front()].bounds);
                    std::vector<int> leafClusterIndices = collectFlattenedClusterIndices(childNodeIndices, outNodes);
                    float geometricError = outNodes[childNodeIndices.front()].geometricError;

                    for (size_t i = 1; i < childNodeIndices.size(); ++i)
                    {
                        const NodeBuildData& child = outNodes[childNodeIndices[i]];
                        const nanomesh::Float3 childMin = boundsMin(child.bounds);
                        const nanomesh::Float3 childMax = boundsMax(child.bounds);
                        minValue.x = std::min(minValue.x, childMin.x);
                        minValue.y = std::min(minValue.y, childMin.y);
                        minValue.z = std::min(minValue.z, childMin.z);
                        maxValue.x = std::max(maxValue.x, childMax.x);
                        maxValue.y = std::max(maxValue.y, childMax.y);
                        maxValue.z = std::max(maxValue.z, childMax.z);
                        geometricError = std::max(geometricError, child.geometricError);
                    }

                    const SimplificationResult simplification = attemptParentSimplification(request, leafClusters, leafClusterIndices);
                    anyMeaningfulReduction = anyMeaningfulReduction || simplification.meaningful;

                    const nanomesh::Bounds parentBounds = makeBounds(minValue, maxValue);
                    const int parentHierarchyLevel = outNodes[childNodeIndices.front()].hierarchyLevel + 1;
                    outHierarchyDepth = std::max(outHierarchyDepth, parentHierarchyLevel + 1);

                    NodeBuildData parent{};
                    parent.clusterIndex = -1;
                    parent.submeshIndex = submeshPair.first;
                    parent.hierarchyLevel = parentHierarchyLevel;
                    parent.firstChildNodeIndex = childNodeIndices.front();
                    parent.childCount = static_cast<int>(childNodeIndices.size());
                    parent.bounds = parentBounds;
                    parent.boundingSphere = makeFloat4(
                        parentBounds.center.x,
                        parentBounds.center.y,
                        parentBounds.center.z,
                        length3(parentBounds.extents));
                    parent.normalCone = computeNodeCone(outNodes, childNodeIndices);
                    parent.geometricError = std::max(geometricError, geometricError + simplification.error + length3(parentBounds.extents) * kParentErrorScale);
                    parent.isLeaf = false;
                    parent.leafClusterIndices = std::move(leafClusterIndices);

                    const int parentIndex = static_cast<int>(outNodes.size());
                    outNodes.push_back(parent);
                    nextLevel.push_back(parentIndex);
                    anyStructuralReduction = true;

                    for (int childNodeIndex : childNodeIndices)
                    {
                        outNodes[childNodeIndex].parentNodeIndex = parentIndex;
                    }
                }
            }

            if (!anyMeaningfulReduction)
            {
                appendWarning(warnings, "PoorHierarchyReduction", "Hierarchy reduction did not simplify partitions meaningfully.");
            }

            if (!anyStructuralReduction &&
                request.options.hierarchyPartitionMode == kHierarchyPartitionMetisAdjacency &&
                currentLevel.size() > targetRootCount)
            {
                appendWarning(warnings, "LowAdjacencyHierarchyQuality", "Adjacency partitioning could not merge the current hierarchy level into graph-coherent groups.");
            }

            if (!anyStructuralReduction || nextLevel.size() >= currentLevel.size())
            {
                appendWarning(warnings, "HierarchyReductionStalled", "Hierarchy simplification stalled before reaching the target root count.");
                break;
            }

            currentLevel = std::move(nextLevel);
        }

        outCoarseRoots.assign(currentLevel.begin(), currentLevel.end());
        if (outCoarseRoots.size() == leafClusters.size() && leafClusters.size() > 1)
        {
            appendWarning(warnings, "LeafOnlyHierarchy", "Hierarchy stayed at the leaf level. Coarse traversal cannot render connected parent geometry for this asset.");
        }

        if (outCoarseRoots.size() > 1)
        {
            appendWarning(warnings, "MultipleCoarseRoots", "Bake finished with " + std::to_string(outCoarseRoots.size()) + " coarse roots.");
        }

        for (size_t rootIndex = 0; rootIndex < outCoarseRoots.size(); ++rootIndex)
        {
            const std::vector<int>& rootLeaves = outNodes[static_cast<size_t>(outCoarseRoots[rootIndex])].leafClusterIndices;
            const int componentCount = countRestrictedConnectedComponents(leafAdjacency, rootLeaves);
            if (componentCount > 1)
            {
                std::ostringstream builder;
                builder << "Coarse root " << rootIndex << " spans " << componentCount
                        << " disconnected leaf-cluster components. Partitioning quality is likely poor for this asset.";
                appendWarning(warnings, "DisconnectedCoarseRoot", builder.str());
            }
        }
    }

    void buildRenderableClusters(
        const nanomesh::BakeRequest& request,
        const std::vector<LeafCluster>& leafClusters,
        std::vector<NodeBuildData>& hierarchyNodes,
        std::vector<LeafCluster>& outClusters)
    {
        outClusters = leafClusters;

        for (size_t nodeIndex = 0; nodeIndex < hierarchyNodes.size(); ++nodeIndex)
        {
            NodeBuildData& node = hierarchyNodes[nodeIndex];
            if (node.isLeaf)
            {
                if (node.clusterIndex >= 0)
                {
                    LeafCluster& cluster = outClusters[static_cast<size_t>(node.clusterIndex)];
                    cluster.hierarchyNodeIndex = static_cast<int>(nodeIndex);
                    cluster.hierarchyLevel = node.hierarchyLevel;
                }

                continue;
            }

            const SimplificationResult simplification = attemptParentSimplification(request, leafClusters, node.leafClusterIndices);
            LeafCluster cluster{};
            buildClusterFromSourceIndices(request, node.submeshIndex, simplification.indices, cluster);
            cluster.hierarchyNodeIndex = static_cast<int>(nodeIndex);
            cluster.hierarchyLevel = node.hierarchyLevel;
            cluster.geometricError = std::max(node.geometricError, cluster.geometricError);

            node.clusterIndex = static_cast<int>(outClusters.size());
            outClusters.push_back(std::move(cluster));
        }
    }

    std::vector<nanomesh::SubmeshMaterialRange> buildMaterialRanges(std::vector<LeafCluster>& renderClusters)
    {
        std::vector<nanomesh::SubmeshMaterialRange> ranges;
        if (renderClusters.empty())
        {
            return ranges;
        }

        std::stable_sort(renderClusters.begin(), renderClusters.end(), [](const LeafCluster& left, const LeafCluster& right)
        {
            if (left.submeshIndex != right.submeshIndex)
            {
                return left.submeshIndex < right.submeshIndex;
            }

            return left.hierarchyLevel < right.hierarchyLevel;
        });

        int currentSubmesh = renderClusters.front().submeshIndex;
        int firstClusterIndex = 0;
        for (size_t i = 1; i <= renderClusters.size(); ++i)
        {
            const bool reachedEnd = i == renderClusters.size();
            const int nextSubmesh = reachedEnd ? -1 : renderClusters[i].submeshIndex;
            if (!reachedEnd && nextSubmesh == currentSubmesh)
            {
                continue;
            }

            ranges.push_back(nanomesh::SubmeshMaterialRange
            {
                currentSubmesh,
                currentSubmesh,
                firstClusterIndex,
                static_cast<int32_t>(i - firstClusterIndex),
                0,
                0
            });

            if (!reachedEnd)
            {
                currentSubmesh = nextSubmesh;
                firstClusterIndex = static_cast<int>(i);
            }
        }

        return ranges;
    }

    uint16_t quantizeUnorm16(float value, float minValue, float extent)
    {
        if (std::abs(extent) < 1e-8f)
        {
            return 0;
        }

        const float normalized = std::clamp((value - minValue) / extent, 0.0f, 1.0f);
        return static_cast<uint16_t>(meshopt_quantizeUnorm(normalized, 16));
    }

    int16_t quantizeSnorm16(float value)
    {
        return static_cast<int16_t>(meshopt_quantizeSnorm(value, 16));
    }

    void writeU16(std::vector<uint8_t>& bytes, uint16_t value)
    {
        bytes.push_back(static_cast<uint8_t>(value & 0xff));
        bytes.push_back(static_cast<uint8_t>((value >> 8) & 0xff));
    }

    void writeS16(std::vector<uint8_t>& bytes, int16_t value)
    {
        writeU16(bytes, static_cast<uint16_t>(value));
    }

    void remapHierarchyClusterIndices(const std::vector<LeafCluster>& renderClusters, std::vector<NodeBuildData>& hierarchyNodes)
    {
        for (NodeBuildData& node : hierarchyNodes)
        {
            node.clusterIndex = -1;
        }

        for (size_t clusterIndex = 0; clusterIndex < renderClusters.size(); ++clusterIndex)
        {
            const LeafCluster& cluster = renderClusters[clusterIndex];
            if (cluster.hierarchyNodeIndex >= 0 &&
                static_cast<size_t>(cluster.hierarchyNodeIndex) < hierarchyNodes.size())
            {
                hierarchyNodes[static_cast<size_t>(cluster.hierarchyNodeIndex)].clusterIndex = static_cast<int>(clusterIndex);
            }
        }
    }

    void buildPackedPayloads(
        const nanomesh::Float2& uvMin,
        const nanomesh::Float2& uvMax,
        std::vector<LeafCluster>& renderClusters,
        std::vector<nanomesh::SubmeshMaterialRange>& materialRanges,
        std::vector<uint8_t>& outVertexData,
        std::vector<uint8_t>& outIndexData)
    {
        outVertexData.clear();
        outIndexData.clear();

        for (size_t clusterIndex = 0; clusterIndex < renderClusters.size(); ++clusterIndex)
        {
            LeafCluster& cluster = renderClusters[clusterIndex];
            for (size_t rangeIndex = 0; rangeIndex < materialRanges.size(); ++rangeIndex)
            {
                const nanomesh::SubmeshMaterialRange& range = materialRanges[rangeIndex];
                if (range.submeshIndex == cluster.submeshIndex &&
                    static_cast<int>(clusterIndex) >= range.firstClusterIndex &&
                    static_cast<int>(clusterIndex) < range.firstClusterIndex + range.clusterCount)
                {
                    cluster.materialRangeIndex = static_cast<int>(rangeIndex);
                    break;
                }
            }

            cluster.vertexDataOffsetBytes = static_cast<int>(outVertexData.size());
            cluster.indexDataOffsetBytes = static_cast<int>(outIndexData.size());

            const nanomesh::Float3 minValue = boundsMin(cluster.bounds);
            const nanomesh::Float3 maxValue = boundsMax(cluster.bounds);
            const nanomesh::Float3 extent = makeFloat3(
                maxValue.x - minValue.x,
                maxValue.y - minValue.y,
                maxValue.z - minValue.z);
            const nanomesh::Float2 uvExtent = {
                std::abs(uvMax.x - uvMin.x) < 1e-8f ? 1.0f : uvMax.x - uvMin.x,
                std::abs(uvMax.y - uvMin.y) < 1e-8f ? 1.0f : uvMax.y - uvMin.y
            };

            for (const SourceVertex& vertex : cluster.vertices)
            {
                writeU16(outVertexData, quantizeUnorm16(vertex.position.x, minValue.x, extent.x));
                writeU16(outVertexData, quantizeUnorm16(vertex.position.y, minValue.y, extent.y));
                writeU16(outVertexData, quantizeUnorm16(vertex.position.z, minValue.z, extent.z));
                writeS16(outVertexData, quantizeSnorm16(vertex.normal.x));
                writeS16(outVertexData, quantizeSnorm16(vertex.normal.y));
                writeS16(outVertexData, quantizeSnorm16(vertex.normal.z));
                writeU16(outVertexData, quantizeUnorm16(vertex.uv.x, uvMin.x, uvExtent.x));
                writeU16(outVertexData, quantizeUnorm16(vertex.uv.y, uvMin.y, uvExtent.y));
            }

            for (uint16_t microIndex : cluster.microIndices)
            {
                writeU16(outIndexData, microIndex);
            }
        }

        for (nanomesh::SubmeshMaterialRange& range : materialRanges)
        {
            if (range.clusterCount <= 0)
            {
                continue;
            }

            range.firstIndexOffsetBytes = renderClusters[range.firstClusterIndex].indexDataOffsetBytes;
            const LeafCluster& lastCluster = renderClusters[range.firstClusterIndex + range.clusterCount - 1];
            range.indexByteCount = (lastCluster.indexDataOffsetBytes + static_cast<int>(lastCluster.microIndices.size()) * static_cast<int>(kPackedIndexStrideBytes)) - range.firstIndexOffsetBytes;
        }
    }

    void fillResponse(
        const nanomesh::BakeRequest& request,
        const nanomesh::Float2& uvMin,
        const nanomesh::Float2& uvMax,
        const nanomesh::Bounds& assetBounds,
        const std::vector<LeafCluster>& renderClusters,
        const std::vector<NodeBuildData>& hierarchyNodes,
        const std::vector<int32_t>& coarseRoots,
        const std::vector<nanomesh::SubmeshMaterialRange>& materialRanges,
        const std::vector<nanomesh::BakeWarning>& warnings,
        int32_t droppedDegenerateTriangleCount,
        int32_t sourceTriangleCount,
        const std::vector<uint8_t>& packedVertexData,
        const std::vector<uint8_t>& packedIndexData,
        nanomesh::BakeResponse& outResponse)
    {
        outResponse.success = true;
        outResponse.message = "Baked " + request.meshName + " into " + std::to_string(renderClusters.size()) + " clusters.";
        outResponse.warnings = warnings;
        outResponse.assetBoundsCenter = assetBounds.center;
        outResponse.assetBoundsExtents = assetBounds.extents;
        outResponse.uvMin = uvMin;
        outResponse.uvMax = uvMax;
        outResponse.sourceVertexCount = static_cast<int32_t>(request.positions.size());
        outResponse.sourceTriangleCount = sourceTriangleCount;
        outResponse.clusterCount = static_cast<int32_t>(renderClusters.size());
        outResponse.hierarchyNodeCount = static_cast<int32_t>(hierarchyNodes.size());
        outResponse.coarseRootCount = static_cast<int32_t>(coarseRoots.size());
        outResponse.hierarchyDepth = 0;
        for (const NodeBuildData& node : hierarchyNodes)
        {
            outResponse.hierarchyDepth = std::max(outResponse.hierarchyDepth, node.hierarchyLevel + 1);
        }

        outResponse.packedVertexStrideBytes = kPackedVertexStrideBytes;
        outResponse.packedIndexStrideBytes = kPackedIndexStrideBytes;
        outResponse.packedVertexDataSizeBytes = static_cast<int32_t>(packedVertexData.size());
        outResponse.packedIndexDataSizeBytes = static_cast<int32_t>(packedIndexData.size());
        outResponse.droppedDegenerateTriangleCount = droppedDegenerateTriangleCount;
        outResponse.packedVertexData = packedVertexData;
        outResponse.packedIndexData = packedIndexData;
        outResponse.coarseRootNodeIndices = coarseRoots;
        outResponse.materialRanges = materialRanges;

        outResponse.clusters.reserve(renderClusters.size());
        outResponse.clusterCullData.reserve(renderClusters.size());
        for (const LeafCluster& cluster : renderClusters)
        {
            outResponse.clusters.push_back(nanomesh::ClusterRecord
            {
                cluster.vertexDataOffsetBytes,
                static_cast<int32_t>(cluster.vertices.size()),
                cluster.indexDataOffsetBytes,
                static_cast<int32_t>(cluster.microIndices.size()),
                cluster.materialRangeIndex,
                cluster.hierarchyNodeIndex,
                cluster.hierarchyLevel,
                cluster.geometricError,
                boundsMin(cluster.bounds),
                makeFloat3(cluster.bounds.extents.x * 2.0f, cluster.bounds.extents.y * 2.0f, cluster.bounds.extents.z * 2.0f)
            });

            outResponse.clusterCullData.push_back(nanomesh::ClusterCullRecord
            {
                cluster.bounds,
                cluster.boundingSphere,
                cluster.normalCone,
                cluster.geometricError
            });
        }

        outResponse.hierarchyNodes.reserve(hierarchyNodes.size());
        for (const NodeBuildData& node : hierarchyNodes)
        {
            outResponse.hierarchyNodes.push_back(nanomesh::HierarchyNode
            {
                node.clusterIndex,
                node.parentNodeIndex,
                node.firstChildNodeIndex,
                node.childCount,
                node.hierarchyLevel,
                node.bounds,
                node.boundingSphere,
                node.normalCone,
                node.geometricError,
                node.isLeaf
            });
        }
    }
}

namespace nanomesh
{
    bool readBakeRequest(const std::string& path, BakeRequest& outRequest, std::string& outError)
    {
        BinaryReader reader(path);
        if (!reader.stream.is_open())
        {
            outError = "Failed to open request file: " + path;
            return false;
        }

        uint32_t magic = 0;
        uint32_t version = 0;
        if (!reader.read(magic) || !reader.read(version))
        {
            outError = "Request header is truncated.";
            return false;
        }

        if (magic != kRequestMagic)
        {
            outError = "Request magic does not match NanoMesh bake contract.";
            return false;
        }

        if (version != kFormatVersion)
        {
            outError = "Unsupported request version: " + std::to_string(version);
            return false;
        }

        if (!reader.readString(outRequest.meshName))
        {
            outError = "Failed to read request mesh name.";
            return false;
        }

        uint32_t vertexCount = 0;
        uint32_t submeshCount = 0;
        if (!reader.read(outRequest.options.maxTrianglesPerCluster) ||
            !reader.read(outRequest.options.maxChildrenPerParent) ||
            !reader.read(outRequest.options.targetRootCount) ||
            !reader.read(outRequest.options.hierarchyPartitionMode) ||
            !reader.read(outRequest.options.hierarchyAdjacencyMode) ||
            !reader.read(vertexCount) ||
            !reader.read(submeshCount))
        {
            outError = "Failed to read request counts.";
            return false;
        }

        outRequest.positions.resize(vertexCount);
        outRequest.normals.resize(vertexCount);
        outRequest.uv0.resize(vertexCount);
        for (uint32_t i = 0; i < vertexCount; ++i)
        {
            if (!reader.read(outRequest.positions[i]) ||
                !reader.read(outRequest.normals[i]) ||
                !reader.read(outRequest.uv0[i]))
            {
                outError = "Failed to read request vertex payload.";
                return false;
            }
        }

        outRequest.submeshes.resize(submeshCount);
        for (uint32_t submeshIndex = 0; submeshIndex < submeshCount; ++submeshIndex)
        {
            uint32_t serializedSubmeshIndex = 0;
            uint32_t indexCount = 0;
            if (!reader.read(serializedSubmeshIndex) || !reader.read(indexCount))
            {
                outError = "Failed to read submesh header.";
                return false;
            }

            outRequest.submeshes[submeshIndex].submeshIndex = serializedSubmeshIndex;
            outRequest.submeshes[submeshIndex].indices.resize(indexCount);
            if (indexCount > 0 && !reader.readBytes(outRequest.submeshes[submeshIndex].indices.data(), sizeof(uint32_t) * indexCount))
            {
                outError = "Failed to read submesh indices.";
                return false;
            }
        }

        return validateRequest(outRequest, outError);
    }

    bool writeBakeResponse(const std::string& path, const BakeResponse& response, std::string& outError)
    {
        BinaryWriter writer(path);
        if (!writer.stream.is_open())
        {
            outError = "Failed to open response file for writing: " + path;
            return false;
        }

        writer.write(kResponseMagic);
        writer.write(kFormatVersion);
        writer.write(static_cast<uint32_t>(response.success ? 1 : 0));
        writer.writeString(response.message);

        uint32_t warningCount = static_cast<uint32_t>(response.warnings.size());
        writer.write(warningCount);
        for (const BakeWarning& warning : response.warnings)
        {
            writer.writeString(warning.code);
            writer.writeString(warning.message);
        }

        writer.write(response.assetBoundsCenter);
        writer.write(response.assetBoundsExtents);
        writer.write(response.uvMin);
        writer.write(response.uvMax);
        writer.write(response.sourceVertexCount);
        writer.write(response.sourceTriangleCount);
        writer.write(response.clusterCount);
        writer.write(response.hierarchyNodeCount);
        writer.write(response.coarseRootCount);
        writer.write(response.hierarchyDepth);
        writer.write(response.packedVertexStrideBytes);
        writer.write(response.packedIndexStrideBytes);
        writer.write(response.packedVertexDataSizeBytes);
        writer.write(response.packedIndexDataSizeBytes);
        writer.write(response.droppedDegenerateTriangleCount);

        const uint32_t clusterCount = static_cast<uint32_t>(response.clusters.size());
        const uint32_t cullCount = static_cast<uint32_t>(response.clusterCullData.size());
        const uint32_t hierarchyCount = static_cast<uint32_t>(response.hierarchyNodes.size());
        const uint32_t rootCount = static_cast<uint32_t>(response.coarseRootNodeIndices.size());
        const uint32_t materialRangeCount = static_cast<uint32_t>(response.materialRanges.size());
        writer.write(clusterCount);
        writer.write(cullCount);
        writer.write(hierarchyCount);
        writer.write(rootCount);
        writer.write(materialRangeCount);

        for (const ClusterRecord& cluster : response.clusters)
        {
            writer.write(cluster.vertexDataOffsetBytes);
            writer.write(cluster.vertexCount);
            writer.write(cluster.indexDataOffsetBytes);
            writer.write(cluster.indexCount);
            writer.write(cluster.materialRangeIndex);
            writer.write(cluster.hierarchyNodeIndex);
            writer.write(cluster.hierarchyLevel);
            writer.write(cluster.geometricError);
            writer.write(cluster.positionOrigin);
            writer.write(cluster.positionExtent);
        }

        for (const ClusterCullRecord& cull : response.clusterCullData)
        {
            writer.write(cull.localBounds.center);
            writer.write(cull.localBounds.extents);
            writer.write(cull.boundingSphere);
            writer.write(cull.normalCone);
            writer.write(cull.geometricError);
        }

        for (const HierarchyNode& node : response.hierarchyNodes)
        {
            uint32_t isLeaf = node.isLeaf ? 1u : 0u;
            writer.write(node.clusterIndex);
            writer.write(node.parentNodeIndex);
            writer.write(node.firstChildNodeIndex);
            writer.write(node.childCount);
            writer.write(node.hierarchyLevel);
            writer.write(node.localBounds);
            writer.write(node.boundingSphere);
            writer.write(node.normalCone);
            writer.write(node.geometricError);
            writer.write(isLeaf);
        }

        for (int32_t rootIndex : response.coarseRootNodeIndices)
        {
            writer.write(rootIndex);
        }

        for (const SubmeshMaterialRange& range : response.materialRanges)
        {
            writer.write(range.submeshIndex);
            writer.write(range.materialSlot);
            writer.write(range.firstClusterIndex);
            writer.write(range.clusterCount);
            writer.write(range.firstIndexOffsetBytes);
            writer.write(range.indexByteCount);
        }

        if (!response.packedVertexData.empty())
        {
            writer.writeBytes(response.packedVertexData.data(), response.packedVertexData.size());
        }

        if (!response.packedIndexData.empty())
        {
            writer.writeBytes(response.packedIndexData.data(), response.packedIndexData.size());
        }

        if (!writer.good())
        {
            outError = "Failed while writing NanoMesh bake response.";
            return false;
        }

        return true;
    }

    bool runBake(const BakeRequest& request, BakeResponse& outResponse, std::string& outError)
    {
        outResponse = BakeResponse{};
        if (!validateRequest(request, outError))
        {
            return false;
        }

        std::vector<BakeWarning> warnings;
        std::vector<LeafCluster> leafClusters;
        Float2 uvMin{};
        Float2 uvMax{};
        Bounds assetBounds{};
        int32_t droppedDegenerateTriangleCount = 0;
        int32_t sourceTriangleCount = 0;

        buildLeafClusters(
            request,
            leafClusters,
            warnings,
            droppedDegenerateTriangleCount,
            uvMin,
            uvMax,
            assetBounds,
            sourceTriangleCount);

        if (leafClusters.empty())
        {
            outResponse.success = false;
            outResponse.message = droppedDegenerateTriangleCount > 0
                ? "No valid triangles remained after removing degenerate triangles."
                : "The mesh did not produce any bakeable clusters.";
            outResponse.warnings = warnings;
            return true;
        }

        std::vector<NodeBuildData> hierarchyNodes;
        std::vector<int32_t> coarseRoots;
        int32_t hierarchyDepth = 0;
        buildHierarchy(request, leafClusters, hierarchyNodes, coarseRoots, hierarchyDepth, warnings);

        std::vector<LeafCluster> renderClusters;
        buildRenderableClusters(request, leafClusters, hierarchyNodes, renderClusters);

        std::vector<SubmeshMaterialRange> materialRanges = buildMaterialRanges(renderClusters);
        std::vector<uint8_t> packedVertexData;
        std::vector<uint8_t> packedIndexData;
        buildPackedPayloads(uvMin, uvMax, renderClusters, materialRanges, packedVertexData, packedIndexData);
        remapHierarchyClusterIndices(renderClusters, hierarchyNodes);

        fillResponse(
            request,
            uvMin,
            uvMax,
            assetBounds,
            renderClusters,
            hierarchyNodes,
            coarseRoots,
            materialRanges,
            warnings,
            droppedDegenerateTriangleCount,
            sourceTriangleCount,
            packedVertexData,
            packedIndexData,
            outResponse);

        return true;
    }
}
