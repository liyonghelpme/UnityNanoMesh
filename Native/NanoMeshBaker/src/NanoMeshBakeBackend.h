#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace nanomesh
{
    struct Float2
    {
        float x = 0.0f;
        float y = 0.0f;
    };

    struct Float3
    {
        float x = 0.0f;
        float y = 0.0f;
        float z = 0.0f;
    };

    struct Float4
    {
        float x = 0.0f;
        float y = 0.0f;
        float z = 0.0f;
        float w = 0.0f;
    };

    struct Bounds
    {
        Float3 center{};
        Float3 extents{};
    };

    struct BakeOptions
    {
        uint32_t maxTrianglesPerCluster = 64;
        uint32_t maxChildrenPerParent = 4;
        uint32_t targetRootCount = 4;
        uint32_t hierarchyPartitionMode = 1;
        uint32_t hierarchyAdjacencyMode = 0;
    };

    struct SubmeshInput
    {
        uint32_t submeshIndex = 0;
        std::vector<uint32_t> indices;
    };

    struct BakeRequest
    {
        std::string meshName;
        BakeOptions options{};
        std::vector<Float3> positions;
        std::vector<Float3> normals;
        std::vector<Float2> uv0;
        std::vector<SubmeshInput> submeshes;
    };

    struct BakeWarning
    {
        std::string code;
        std::string message;
    };

    struct ClusterRecord
    {
        int32_t vertexDataOffsetBytes = 0;
        int32_t vertexCount = 0;
        int32_t indexDataOffsetBytes = 0;
        int32_t indexCount = 0;
        int32_t materialRangeIndex = -1;
        int32_t hierarchyNodeIndex = -1;
        int32_t hierarchyLevel = 0;
        float geometricError = 0.0f;
        Float3 positionOrigin{};
        Float3 positionExtent{};
    };

    struct ClusterCullRecord
    {
        Bounds localBounds{};
        Float4 boundingSphere{};
        Float4 normalCone{};
        float geometricError = 0.0f;
    };

    struct HierarchyNode
    {
        int32_t clusterIndex = -1;
        int32_t parentNodeIndex = -1;
        int32_t firstChildNodeIndex = -1;
        int32_t childCount = 0;
        int32_t hierarchyLevel = 0;
        Bounds localBounds{};
        Float4 boundingSphere{};
        Float4 normalCone{};
        float geometricError = 0.0f;
        bool isLeaf = false;
    };

    struct SubmeshMaterialRange
    {
        int32_t submeshIndex = 0;
        int32_t materialSlot = 0;
        int32_t firstClusterIndex = 0;
        int32_t clusterCount = 0;
        int32_t firstIndexOffsetBytes = 0;
        int32_t indexByteCount = 0;
    };

    struct BakeResponse
    {
        bool success = false;
        std::string message;
        std::vector<BakeWarning> warnings;
        Float3 assetBoundsCenter{};
        Float3 assetBoundsExtents{};
        Float2 uvMin{};
        Float2 uvMax{};
        int32_t sourceVertexCount = 0;
        int32_t sourceTriangleCount = 0;
        int32_t clusterCount = 0;
        int32_t hierarchyNodeCount = 0;
        int32_t coarseRootCount = 0;
        int32_t hierarchyDepth = 0;
        int32_t packedVertexStrideBytes = 16;
        int32_t packedIndexStrideBytes = 2;
        int32_t packedVertexDataSizeBytes = 0;
        int32_t packedIndexDataSizeBytes = 0;
        int32_t droppedDegenerateTriangleCount = 0;
        std::vector<ClusterRecord> clusters;
        std::vector<ClusterCullRecord> clusterCullData;
        std::vector<HierarchyNode> hierarchyNodes;
        std::vector<int32_t> coarseRootNodeIndices;
        std::vector<SubmeshMaterialRange> materialRanges;
        std::vector<uint8_t> packedVertexData;
        std::vector<uint8_t> packedIndexData;
    };

    bool readBakeRequest(const std::string& path, BakeRequest& outRequest, std::string& outError);
    bool writeBakeResponse(const std::string& path, const BakeResponse& response, std::string& outError);
    bool runBake(const BakeRequest& request, BakeResponse& outResponse, std::string& outError);
}
