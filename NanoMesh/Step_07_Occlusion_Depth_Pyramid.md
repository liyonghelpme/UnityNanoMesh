# Step 7: Occlusion And Depth Pyramid

## Goal

Add conservative occlusion culling using a depth pyramid after the basic cluster-rendering path is stable.

## Deliverables

- depth pyramid generation pass
- occlusion testing in cluster culling
- occlusion debug counters and visualization

## Core Work

1. Build a hierarchical Z texture from camera depth.
2. Add conservative cluster bound tests against the depth pyramid.
3. Integrate occlusion decisions into the cluster traversal or post-traversal cull stage.
4. Expose counters for:
   - clusters tested
   - clusters rejected
   - clusters submitted after occlusion

## Technical Rules

- Conservative culling only. False positives are acceptable. False negatives are not.
- Keep the feature togglable for debugging.
- Introduce this only after frustum culling and LOD are stable.

## Main Risk

Poorly tuned occlusion logic can cause flicker and popping that looks like geometry corruption. Debug visibility for this step is mandatory.

## Acceptance

- Occluded scenes submit fewer visible clusters than non-occluded scenes.
- No visible flicker or missing geometry under normal camera motion.
- Depth pyramid cost is measurable independently from the culling pass.

## Dependencies

- [Step_06_Cluster_Culling_And_LOD.md](D:/UnityProj/testBakery/NanoMesh/Step_06_Cluster_Culling_And_LOD.md)

