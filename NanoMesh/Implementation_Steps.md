# NanoMesh Implementation Steps

This folder contains the execution breakdown for the Unity URP NanoMesh v1 described in [NanoMesh_Design.md](D:/UnityProj/testBakery/NanoMesh/NanoMesh_Design.md).

The steps are ordered to keep the system shippable and debuggable at each milestone. Do not skip ahead to occlusion, streaming, or advanced material work before the earlier steps are stable.

## Step Order

1. [Step_01_Project_Skeleton.md](D:/UnityProj/testBakery/NanoMesh/Step_01_Project_Skeleton.md)
2. [Step_02_Bake_Pipeline.md](D:/UnityProj/testBakery/NanoMesh/Step_02_Bake_Pipeline.md)
3. [Step_03_Asset_And_Runtime_Data.md](D:/UnityProj/testBakery/NanoMesh/Step_03_Asset_And_Runtime_Data.md)
4. [Step_04_Baseline_Rendering.md](D:/UnityProj/testBakery/NanoMesh/Step_04_Baseline_Rendering.md)
5. [Step_05_Instance_Culling.md](D:/UnityProj/testBakery/NanoMesh/Step_05_Instance_Culling.md)
6. [Step_06_Cluster_Culling_And_LOD.md](D:/UnityProj/testBakery/NanoMesh/Step_06_Cluster_Culling_And_LOD.md)
7. [Step_07_Occlusion_Depth_Pyramid.md](D:/UnityProj/testBakery/NanoMesh/Step_07_Occlusion_Depth_Pyramid.md)
8. [Step_08_Debug_Profiling_And_Hardening.md](D:/UnityProj/testBakery/NanoMesh/Step_08_Debug_Profiling_And_Hardening.md)

## Scope Guardrails

- Target: mobile-first
- Render path: URP extension
- V1 mesh support: static opaque only
- V1 raster path: hardware raster only
- No visbuffer in v1
- No software raster in v1
- No skinned meshes in v1
- No streaming implementation in v1

## Recommended Work Cadence

- Finish one step completely before starting the next.
- Keep a validation scene in the project from Step 1 onward.
- Add debug counters as soon as a subsystem first works, not at the end.
- Preserve a known-good fallback path at each stage so regressions are easy to isolate.

## Reference Mapping

- Offline mesh processing: `meshoptimizer`
- Runtime organization and data flow: Bevy meshlet renderer
- End-to-end reasoning and tradeoffs: `nanite-webgpu`

