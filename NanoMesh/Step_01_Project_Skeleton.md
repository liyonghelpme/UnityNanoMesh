# Step 1: Project Skeleton

## Goal

Create the minimum Unity-side structure needed to host NanoMesh as a URP extension and to support later bake and runtime work without reorganization.

## Deliverables

- `Runtime` folder for core components and renderer integration
- `Editor` folder for bake pipeline and import tooling
- `Shaders` or `Compute` folder for compute and draw shaders
- `Samples` or test scene folder for validation content
- one validation scene with:
  - camera
  - URP renderer configured
  - at least one static test mesh

## Required Types

- `NanoMeshRenderer`
- `NanoMeshAsset`
- `NanoMeshRenderFeature`
- `NanoMeshRuntimeBuffers`

At this stage these can be empty or stubbed, but naming and ownership should be fixed.

## Decisions To Lock

- Use `ScriptableRendererFeature` as the entry point.
- Keep runtime code independent from editor code.
- Keep asset definitions serializable and friendly to future binary payloads.
- Keep test content in-project so later phases can run on stable scenes.

## Implementation Notes

- Do not implement advanced logic here.
- The objective is stable structure, assembly boundaries, and URP hook points.
- If assembly definitions are used, split runtime and editor assemblies now.

## Acceptance

- Project compiles cleanly with empty NanoMesh stubs.
- URP can include a disabled NanoMesh renderer feature without errors.
- Validation scene opens and runs.

## Dependencies

- None

