# Step 6: Cluster Culling And LOD

## Goal

Traverse the baked hierarchy per visible instance, reject invisible clusters, and choose the appropriate LOD level using a practical projected-error or distance-curve metric.

## Deliverables

- cluster traversal compute path
- per-cluster frustum culling
- optional cone culling hook
- LOD selection logic
- visible cluster output list
- indirect draw args generation from visible clusters

## Core Work

1. Start traversal from coarse roots for each visible instance.
2. Evaluate cluster visibility against the camera frustum.
3. Evaluate LOD using cluster error and camera distance or projection.
4. Descend into children when finer detail is needed.
5. Accept clusters for draw submission when the stop condition is met.
6. Build indirect args for raster pass consumption.

## Technical Rules

- LOD must degrade smoothly with distance.
- Support multiple coarse roots.
- Cone culling must remain optional and off by default until measured.
- Avoid overfitting the metric too early; use a simple stable metric first.

## Recommended Reference Usage

- Bevy meshlet hierarchy traversal organization
- `nanite-webgpu` for projected-error reasoning and failure cases

## Acceptance

- Cluster count decreases with distance.
- Nearby views retain fine detail.
- Meshes that do not simplify to one root still render correctly.
- Raster path consumes only the visible cluster list.

## Dependencies

- [Step_05_Instance_Culling.md](D:/UnityProj/testBakery/NanoMesh/Step_05_Instance_Culling.md)

