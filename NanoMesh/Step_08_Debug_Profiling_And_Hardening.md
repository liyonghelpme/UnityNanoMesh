# Step 8: Debug, Profiling, And Hardening

## Goal

Make the NanoMesh v1 pipeline diagnosable, measurable, and safe enough to iterate on mobile-oriented scenes.

## Deliverables

- debug visualization modes
- bake statistics inspector
- GPU timing breakdown
- memory usage reporting
- failure-mode handling for unsupported assets and poor simplification cases
- streaming-ready boundaries documented in code comments or interfaces

## Required Debug Views

- cluster bounds
- cluster LOD colorization
- instance cull result view
- cluster cull result view
- occlusion result view

## Required Metrics

- source triangle count
- cluster count
- visible cluster count
- submitted cluster count
- culling GPU time
- depth pyramid GPU time
- raster pass GPU time
- runtime buffer memory usage

## Hardening Tasks

1. Add clear failure messages for unsupported asset types.
2. Validate material count and section mapping.
3. Validate buffer resizing and lifecycle under scene changes.
4. Document extension points for future:
   - streaming
   - visbuffer
   - skinned meshes
   - masked materials

## Technical Rules

- Do not ship the system without counters.
- Every major pass must be individually disableable for debugging.
- Keep one small deterministic validation scene and one heavier stress scene.

## Acceptance

- Engineers can identify whether regressions come from bake, traversal, occlusion, or raster stages.
- Memory and timing data are available in editor or development builds.
- The codebase has obvious extension points for future phases without changing the v1 data model.

## Dependencies

- [Step_07_Occlusion_Depth_Pyramid.md](D:/UnityProj/testBakery/NanoMesh/Step_07_Occlusion_Depth_Pyramid.md)

