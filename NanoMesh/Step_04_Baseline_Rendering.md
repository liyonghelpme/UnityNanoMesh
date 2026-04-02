# Step 4: Baseline Rendering

## Goal

Render a baked NanoMesh asset through URP using hardware raster only, with no culling and no LOD, to prove the geometry path and material mapping are correct.

## Deliverables

- active `NanoMeshRenderFeature`
- one or more URP passes that render NanoMesh geometry
- material mapping from baked sections to URP materials
- validation scene that renders a NanoMesh asset correctly

## Core Work

1. Add a render pass that binds NanoMesh buffers.
2. Implement a draw path that renders all clusters for a test asset.
3. Preserve standard URP shading behavior where possible.
4. Validate transforms, normals, UVs, and material sections.

## Technical Rules

- Do not introduce culling in this step.
- Do not optimize submission prematurely.
- Prefer correctness and observability over batching sophistication.

## Main Risk

If the geometry reconstruction path is wrong, every later culling optimization will be harder to debug. This step must be visually correct before moving on.

## Acceptance

- One baked asset renders correctly in URP.
- Multiple instances render correctly with distinct transforms.
- Materials align with expected submesh or section ranges.
- Debug view can confirm all clusters are being drawn.

## Dependencies

- [Step_03_Asset_And_Runtime_Data.md](D:/UnityProj/testBakery/NanoMesh/Step_03_Asset_And_Runtime_Data.md)

## Reference Code

Use the Unity Virtual Mesh sample package as architecture reference for this step:

- package root: `D:/UnityProj/testBakery/refProj4/com.unity.virtualmesh-main/com.unity.virtualmesh-main`
- render feature: `D:/UnityProj/testBakery/refProj4/com.unity.virtualmesh-main/com.unity.virtualmesh-main/Runtime/RenderFeatures/VirtualMeshRenderFeature.cs`
- runtime manager: `D:/UnityProj/testBakery/refProj4/com.unity.virtualmesh-main/com.unity.virtualmesh-main/Runtime/VirtualMeshManager.cs`
- implementation notes: `D:/UnityProj/testBakery/refProj4/com.unity.virtualmesh-main/com.unity.virtualmesh-main/Documentation~/implementation.md`
- package overview: `D:/UnityProj/testBakery/refProj4/com.unity.virtualmesh-main/com.unity.virtualmesh-main/README.md`

Use this reference for render pass structure, runtime-owned GPU buffer flow, and material-to-draw organization only.

Do not pull in Virtual Mesh streaming, occlusion culling, depth pyramid generation, or cluster LOD selection in this step. Step 4 is a correctness-first baseline draw path.
