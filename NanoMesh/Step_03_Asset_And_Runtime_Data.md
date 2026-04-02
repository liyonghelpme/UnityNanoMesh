# Step 3: Asset And Runtime Data

## Goal

Define the runtime data model and GPU buffer ownership for `NanoMeshAsset` consumption before real rendering begins.

## Deliverables

- finalized `NanoMeshAsset` runtime fields
- runtime upload path from asset to GPU buffers
- `NanoMeshRuntimeBuffers` lifecycle
- instance registration path from `NanoMeshRenderer`

## Required Runtime Data

- instance buffer
- cluster metadata buffer
- hierarchy node buffer
- geometry buffer payloads
- visible cluster append buffer
- indirect args buffer
- debug counters buffer

## Core Work

1. Finalize asset layout needed by runtime shaders.
2. Convert serialized asset payloads into GPU uploadable buffers.
3. Define per-frame buffer clearing and reuse rules.
4. Register `NanoMeshRenderer` instances into a central runtime list.
5. Upload transforms and per-instance settings every frame or on change.

## Technical Rules

- Buffer ownership must be centralized.
- Avoid per-object GPU buffer allocation at runtime.
- Keep the layout streaming-ready even if streaming is not implemented yet.
- Separate static asset data from per-frame instance data.

## Recommended Reference Usage

- Bevy meshlet asset and resource manager layout

## Acceptance

- A baked asset can be loaded and uploaded to GPU buffers.
- A scene with multiple NanoMesh instances can register and prepare runtime data without rendering yet.
- Buffer sizes and memory usage are inspectable.

## Dependencies

- [Step_02_Bake_Pipeline.md](D:/UnityProj/testBakery/NanoMesh/Step_02_Bake_Pipeline.md)

