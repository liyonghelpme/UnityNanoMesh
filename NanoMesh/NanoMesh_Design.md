# NanoMesh Design for Unity URP

## Summary

NanoMesh is a mobile-first, cluster-based rendering system for Unity that aims to bring Nanite-like ideas into a Unity/URP production path without depending on mesh shaders, bindless resources, or a custom engine rewrite. The first version focuses on static opaque meshes, offline-baked cluster hierarchies, GPU-driven culling, and indirect hardware rasterization.

This design intentionally does not try to reproduce full UE5 Nanite behavior in v1. The target is a practical Unity implementation that can be built incrementally, validated on mobile-class hardware, and extended later toward visibility-buffer shading, streaming, skinned meshes, and more advanced lighting.

## Origin NanoMesh Source

This design is derived in part from the original NanoMesh-related source document:

- `D:\UnityProj\testBakery\Doc\AdavanceRealtimeRendering_NanoMesh0810.md`

That document is a text export of Tencent Games' "Seamless rendering on mobile" presentation and is the primary source in this workspace for the intended mobile NanoMesh direction.

### What this design takes from the origin document

- mobile-first constraints drive the architecture
- offline and runtime stages are clearly separated
- meshes are split into clusters and organized into a hierarchy
- runtime selection is driven at cluster level rather than mesh-level LOD swaps
- GPU-driven culling is central to the design
- projected-area or distance-aware LOD selection is more appropriate than classic discrete LODs
- visbuffer-style or deferred material resolution is a later-stage optimization path, not the first thing to build
- skinned meshes require special handling and should not be mixed into the initial static-mesh milestone
- dynamic GI is a separate subsystem and should not be part of the first renderer milestone

### What this Unity v1 design intentionally does not carry over yet

- the full visbuffer material shading path
- the mobile-specific material tiling and per-material visbuffer resolve path
- the dynamic GI brick or voxel system
- skinned-mesh conservative bound handling
- streaming of fine cluster data
- deeper mobile hardware specializations discussed in the presentation

### Why this distinction matters

The original NanoMesh source describes a broader rendering research and production direction. This Unity design is narrower by intent. It keeps the cluster hierarchy, GPU culling, and mobile compatibility goals, but cuts the first implementation down to a tractable URP milestone.

## Goals and Non-Goals

### Goals

- Render dense static meshes in URP with better scalability than traditional mesh-level submission.
- Remove the need for manually authored discrete LOD chains for supported assets.
- Bake meshes offline into a cluster hierarchy suitable for GPU-driven culling and LOD selection.
- Keep the runtime compatible with mobile-first constraints.
- Preserve normal URP material shading for the first implementation.
- Build the system in phases so each milestone is testable in Unity.

### Non-Goals for V1

- No visibility buffer shading.
- No software rasterizer.
- No dynamic GI or voxel GI.
- No skinned meshes or deformable mesh support.
- No ray tracing, mesh shaders, or bindless material model.
- No full runtime streaming system in the first milestone.
- No transparent, masked, or two-sided material support beyond explicit future hooks.

## Reference Projects

NanoMesh should use the reference projects in `D:\UnityProj\testBakery\refProj4` as follows.

### 1. meshoptimizer

Primary use:
- meshlet or cluster generation
- simplification
- quantization
- continuous LOD building primitives
- potential depth-only index optimization ideas

Why it matters:
- It is the best portable foundation for the offline asset pipeline.
- Its APIs map well to a Unity editor baker or native plugin.
- It avoids tying the bake pipeline to any particular renderer.

Key reference:
- `D:\UnityProj\testBakery\refProj4\meshoptimizer-master\meshoptimizer-master\README.md`

### 2. Bevy meshlet renderer

Primary use:
- runtime stage breakdown
- cluster asset layout ideas
- BVH-backed culling structure
- visibility-buffer-era concepts that still inform cluster submission organization
- separation of preprocess data, per-frame GPU resources, and material resolution

Why it matters:
- It is the closest runtime reference in the available projects to a real engine integration.
- Its architecture is more useful to Unity than the Rust/Vulkan mesh-shader-heavy reference.

Key references:
- `D:\UnityProj\testBakery\refProj4\bevy-main\bevy-main\crates\bevy_pbr\src\meshlet\mod.rs`
- `D:\UnityProj\testBakery\refProj4\bevy-main\bevy-main\crates\bevy_pbr\src\meshlet\asset.rs`
- `D:\UnityProj\testBakery\refProj4\bevy-main\bevy-main\crates\bevy_pbr\src\meshlet\visibility_buffer_raster_node.rs`

### 3. nanite-webgpu

Primary use:
- readable end-to-end pipeline explanation
- DAG or hierarchy traversal ideas
- culling order and LOD rationale
- practical notes on projected-error limits, simplification tradeoffs, and mobile constraints

Why it matters:
- It is the best educational reference in the folder.
- The README explains the system decisions clearly enough to guide Unity architecture choices.

Key reference:
- `D:\UnityProj\testBakery\refProj4\nanite-webgpu-master\nanite-webgpu-master\README.md`

### 4. nanite-at-home

Primary use:
- conceptual background only

Why it is not a base:
- It assumes mesh shaders and a Vulkan-first renderer.
- It is not a good portability baseline for Unity URP or mobile.

Key reference:
- `D:\UnityProj\testBakery\refProj4\nanite-at-home-main\nanite-at-home-main\README.md`

### 5. Original NanoMesh presentation text

Primary use:
- product-direction source for mobile NanoMesh goals
- confirmation of which ideas belong to the original Tencent mobile approach
- guidance for what should be kept separate from v1

Why it matters:
- it is the local source document that motivated this design
- it clarifies that the full talk covers more than the v1 Unity implementation should attempt

Key reference:
- `D:\UnityProj\testBakery\Doc\AdavanceRealtimeRendering_NanoMesh0810.md`

## Target Constraints

### Platform

- Mobile first
- Desktop support later is desirable, but v1 design must not depend on desktop-only GPU features

### Rendering Stack

- Unity URP extension
- Integration via `ScriptableRendererFeature` and custom `ScriptableRenderPass` stages
- No custom SRP replacement in v1

### GPU Feature Assumptions

- Compute shaders available
- Indirect draw supported on target graphics APIs used by URP mobile targets
- No requirement for:
  - mesh shaders
  - bindless textures
  - 64-bit atomics
  - software raster visibility pipeline

### Content Constraints for V1

- Static opaque meshes only
- No skinned meshes
- No vertex animation
- No transparency or alpha-tested foliage in v1

## System Overview

NanoMesh has two major stages:

1. Offline bake in the Unity editor
2. Runtime rendering and culling in URP

At bake time, source meshes are partitioned into clusters, simplified into a hierarchy, quantized, and serialized into a custom Unity asset. At runtime, instances reference the baked asset. Compute passes determine visible clusters and appropriate LOD selections, then visible cluster geometry is submitted through indirect hardware rasterization and shaded with the normal URP material path.

## Offline Asset Pipeline

### Responsibilities

The bake pipeline converts a standard Unity mesh into a `NanoMeshAsset` containing:

- clustered geometry
- hierarchy or BVH data for traversal and LOD
- per-cluster bounds
- per-cluster error metrics
- quantized vertex payloads
- cluster metadata for rendering

### Proposed Flow

1. Read the source Unity mesh in editor code.
2. Normalize vertex and index buffers into a bake-friendly layout.
3. Build leaf clusters using meshoptimizer-style clusterization.
4. Generate simplified parent clusters iteratively until termination conditions are reached.
5. Compute per-cluster metadata:
   - AABB
   - bounding sphere
   - cone or average normal data for optional backface cone culling
   - projected-error input metric
   - parent or child links
6. Quantize vertex payloads per cluster or per asset.
7. Pack cluster geometry and metadata into a serialized Unity asset.

### Simplification Strategy

V1 should not assume every mesh reduces to one tiny root cluster. The bake pipeline must allow multiple coarse roots if simplification quality stalls. This is important because many real assets do not simplify cleanly.

Termination conditions should include:

- simplification ratio stops improving enough
- geometric error exceeds threshold
- cluster count reaches configured floor
- topology preservation constraints are violated

### Editor Implementation Shape

Core editor-facing systems:

- `NanoMeshBaker`
- optional native bake backend wrapping meshoptimizer through plugin or command-line bridge
- validation utilities for cluster stats and bake failures

### Bake Outputs

The baker should emit:

- a `NanoMeshAsset`
- optional debug data for editor inspection
- summary metrics:
  - source triangles
  - cluster count
  - hierarchy depth
  - quantized memory size
  - simplification fallback warnings

## Unity Data Model

### NanoMeshAsset

Purpose:
- Serialized baked geometry and hierarchy asset consumed by runtime systems

Contents:
- asset header and version
- packed vertex buffer data
- packed index or micro-index data
- cluster records
- cluster cull data
- hierarchy or BVH nodes
- coarse root references
- material section mapping
- source bounds
- bake statistics and debug metadata

Suggested fields:

- `Bounds assetBounds`
- `int sourceVertexCount`
- `int sourceTriangleCount`
- `int clusterCount`
- `int hierarchyNodeCount`
- `GraphicsBufferLayoutInfo[] bufferLayouts`
- `ClusterRecord[] clusters`
- `ClusterCullRecord[] clusterCullData`
- `HierarchyNode[] hierarchyNodes`
- `byte[] packedVertexData`
- `byte[] packedIndexData`
- `SubmeshMaterialRange[] materialRanges`

### NanoMeshRenderer

Purpose:
- Scene component used by game objects that render a baked NanoMesh asset

Responsibilities:
- reference a `NanoMeshAsset`
- reference materials
- expose instance-level overrides and debug flags
- register instance data with runtime systems

Suggested fields:

- `NanoMeshAsset asset`
- `Material[] materials`
- `bool enableOcclusionCulling`
- `bool enableConeCulling`
- `float lodBias`
- `float errorScale`
- `bool forceDebugDraw`

### NanoMeshRuntimeBuffers

Purpose:
- Own GPU buffers and frame-lifetime resources

Responsibilities:
- instance buffer
- visible cluster list
- culled cluster list or traversal queue
- indirect args buffer
- optional depth pyramid resources
- debug counters

Suggested buffers:

- instance data buffer
- cluster metadata buffer
- hierarchy node buffer
- visible cluster append buffer
- draw command or indirect args buffer
- culling statistics buffer
- depth pyramid texture

## Runtime Architecture

### Main Runtime Components

- `NanoMeshSystem`
- `NanoMeshRenderer`
- `NanoMeshRuntimeBuffers`
- `NanoMeshCullingPass`
- `NanoMeshDepthPyramidPass`
- `NanoMeshRasterPass`
- `NanoMeshDebugPass`

### Frame Flow

1. Gather visible `NanoMeshRenderer` components from Unity scene objects.
2. Upload or update per-instance transforms and instance metadata.
3. Run instance culling compute pass.
4. Run cluster traversal and LOD selection compute pass.
5. Optionally build or consume depth pyramid for occlusion.
6. Run cluster occlusion culling pass.
7. Generate indirect draw arguments.
8. Submit visible clusters through hardware raster path.
9. Let URP perform normal material shading using the selected geometry path.
10. Emit debug overlays and profiling counters if enabled.

## URP Integration

### Integration Decision

V1 uses a URP extension, not a custom SRP. The implementation should be organized around:

- `ScriptableRendererFeature`
- one or more `ScriptableRenderPass` instances
- compute shader dispatches for culling and args generation
- indirect draw for visible cluster geometry

### Proposed URP Passes

#### 1. Instance and cluster preparation pass

- uploads per-frame instance data
- clears append buffers and counters
- initializes indirect args buffers

#### 2. Depth pyramid preparation pass

- builds a depth pyramid from camera depth
- v1 can initially skip this pass until occlusion culling milestone

#### 3. NanoMesh culling pass

- compute pass
- performs instance frustum culling
- traverses hierarchy
- evaluates LOD
- appends visible clusters

#### 4. NanoMesh raster pass

- submits indirect draws for visible clusters
- uses hardware raster only
- outputs into standard URP-compatible targets

#### 5. Optional debug pass

- overlays cluster bounds
- false-color LOD visualization
- culling counters

### Material Strategy

V1 should not invent a new material system. The design should preserve normal URP material usage where possible.

Rules:

- `NanoMeshRenderer` uses standard material references
- cluster records carry enough metadata to map geometry back to submesh or material ranges
- shading happens through the normal URP material path after cluster selection
- no visibility-buffer material resolve in v1

### Geometry Submission Strategy

The raster pass should submit only visible clusters. There are two viable implementation styles:

- aggregated cluster buffers with one or few indirect draws
- per-material indirect draws from the visible cluster list

For v1, prefer per-material indirect draws if it reduces integration risk with URP material handling. This is less elegant than a full visibility-buffer path but much simpler and more compatible with Unity.

## Culling and LOD Selection

### Instance Culling

Perform first because it is cheap and reduces downstream cluster work.

Checks:

- frustum culling against asset bounds transformed to world space
- optional distance cutoff
- optional coarse occlusion using previous-frame or current depth pyramid later in the roadmap

### Cluster Traversal

Each surviving instance starts cluster hierarchy traversal from one or more coarse roots. The traversal pass decides whether to:

- accept a cluster for rendering
- descend into children for more detail
- reject a cluster entirely

### LOD Metric

V1 uses a practical projected-error or distance-weighted metric rather than trying to reproduce full Nanite error selection.

Inputs:

- cluster geometric error
- projected cluster size or distance to camera
- instance-level `lodBias`
- platform tuning constants

Decision rules:

- choose coarse cluster if projected error is below threshold
- descend if projected error exceeds threshold and children exist
- stop at leaf cluster if no finer data exists

The design should allow replacing this with a better metric later without changing the asset format substantially.

### Cluster Culling Checks

V1 cluster culling stages:

- frustum culling
- optional cone-based backface culling
- occlusion culling once depth pyramid is integrated

Backface cone culling should remain optional because gains vary heavily with asset shape and cluster quality.

### Occlusion Culling

V1 target includes a depth pyramid milestone, but this should be introduced after the basic hardware-raster path is stable.

Approach:

- build a hierarchical Z buffer from camera depth
- use cluster bounds to test visibility conservatively
- accept some false positives to keep the implementation stable

## Streaming-Ready Boundaries

Streaming is not implemented in the first milestone, but the asset and runtime must not block it later.

Design requirements:

- cluster records must support chunked storage
- hierarchy nodes should be able to reference unloaded children
- runtime visible-cluster flow should tolerate missing fine LODs by rendering coarser available data
- asset serialization should separate metadata from large geometry blocks where practical

V1 may keep all cluster data resident, but the runtime interfaces should anticipate:

- cluster page requests
- residency maps
- background asset loading
- fallback to currently resident coarser roots

## Debugging and Profiling Requirements

The system should include debug support from the first working render path.

Required debug views:

- cluster bounds overlay
- cluster ID or false-color view
- LOD level visualization
- instance-cull result count
- cluster-cull result count
- occlusion-cull result count once available

Required profiling counters:

- source triangle count
- visible cluster count
- submitted cluster count
- rendered triangle estimate
- culling compute time
- depth pyramid time
- raster pass time
- GPU memory used by cluster buffers

Editor inspection should also expose bake-time stats and warnings.

## Phased Implementation Plan

### Phase 1. Offline bake

- Build `NanoMeshBaker`
- Convert a static Unity mesh into clustered data
- Serialize a valid `NanoMeshAsset`
- Add editor inspection for asset stats

Acceptance:
- high-poly static mesh can be baked into a reusable asset

### Phase 2. Render all clusters without culling

- Upload cluster data to GPU
- Render all clusters through a simple URP pass
- Validate geometry reconstruction and material mapping

Acceptance:
- baked asset renders correctly in URP before any optimization

### Phase 3. Add instance culling

- compute pass for instance frustum culling
- compact active instance list

Acceptance:
- off-screen instances do not submit work

### Phase 4. Add cluster frustum culling

- hierarchy traversal
- per-cluster frustum tests

Acceptance:
- visible cluster count drops significantly versus full-cluster rendering

### Phase 5. Add LOD selection

- projected-error or distance-curve selection
- per-instance LOD bias

Acceptance:
- cluster density decreases with distance while silhouette remains stable

### Phase 6. Add occlusion culling with depth pyramid

- depth pyramid generation
- conservative cluster occlusion testing

Acceptance:
- hidden geometry reduces cluster submission cost in occluded scenes

### Phase 7. Add profiling and debug views

- GPU timings
- on-screen counters
- false-color debug modes

Acceptance:
- bottlenecks can be diagnosed in Unity without external tooling only

### Phase 8. Prepare streaming hooks

- split asset metadata from geometry payload assumptions
- define residency interfaces without enabling paging

Acceptance:
- design supports future streaming without rewriting the core runtime

## Risks

### 1. Unity and URP integration overhead

URP is not designed around a native Nanite-style pipeline. Material compatibility, render pass ordering, and indirect draw control will be the main engine-integration risks.

Mitigation:
- keep v1 on standard URP opaque material workflows
- avoid visibility-buffer and software-raster features early

### 2. Simplification quality

Bad simplification will destroy the value of the system by producing too many triangles in coarse levels.

Mitigation:
- support multiple coarse roots
- track simplification failure cases in the baker
- tune error thresholds per asset category

### 3. Mobile memory pressure

Cluster metadata, quantized vertex buffers, hierarchy nodes, and depth pyramid resources can still exceed practical budgets on lower-end devices.

Mitigation:
- quantify memory during bake and runtime
- keep all debug counters visible
- delay expensive features until metrics exist

### 4. Occlusion culling instability

Depth-pyramid occlusion can introduce flicker or poor conservatism if bounds and synchronization are wrong.

Mitigation:
- add it only after cluster rendering is stable
- prefer conservative tests and false positives over false negatives

### 5. Material batching cost

Without a visibility buffer or bindless path, material handling can become a submission bottleneck.

Mitigation:
- structure visible clusters by material range
- measure per-material indirect draw overhead before widening scope

## Deferred Features

The following features are explicitly deferred until after v1 stability:

- visibility-buffer shading
- software rasterizer for sub-pixel triangles
- skinned mesh support with conservative bone-space bounds
- dynamic GI integration
- alpha-tested or masked materials
- cluster streaming and residency
- bindless-style material indirection
- desktop-specific enhancements using stronger GPU features

## Acceptance Scenarios

The final implementation based on this design should satisfy these scenarios.

- Import a static high-poly mesh and produce a valid `NanoMeshAsset`.
- Render the baked asset in URP on mobile-capable graphics APIs without mesh shaders or bindless dependencies.
- Demonstrate that visible cluster count and effective triangle density decrease with distance.
- Demonstrate that off-screen and occluded content reduces submitted cluster work.
- Preserve stable rendering when a mesh cannot simplify into a single coarse root.
- Restrict v1 support to static opaque meshes and fail clearly for unsupported cases.
- Expose GPU timing and buffer usage sufficient to evaluate mobile viability.

## Default Technical Decisions

- Mobile first
- URP extension, not custom SRP
- Static opaque meshes only in v1
- Offline bake based primarily on meshoptimizer-style preprocessing
- Runtime architecture informed primarily by Bevy meshlets
- Hardware raster only in v1
- Standard URP material shading path in v1
- Depth-pyramid occlusion included after basic cluster path is stable
- Streaming prepared structurally but not implemented
