# Step 2: Bake Pipeline

## Goal

Build the editor-time path that converts a standard Unity mesh into a serialized `NanoMeshAsset` containing clustered geometry and hierarchy metadata.

## Deliverables

- `NanoMeshBaker`
- editor UI or command entry for baking
- serialized `NanoMeshAsset`
- bake report with cluster and hierarchy stats

## Required Inputs

- static Unity mesh
- submesh and material section information
- vertex attributes required for v1 shading:
  - position
  - normal
  - UV0

## Required Outputs

- clustered geometry
- parent or child hierarchy links
- per-cluster bounds
- per-cluster error data
- packed or quantized geometry payloads
- material range metadata

## Core Work

1. Normalize mesh data from Unity into a bake-friendly layout.
2. Partition geometry into leaf clusters.
3. Generate simplified parent levels until simplification stalls or target conditions are met.
4. Compute cluster metadata:
   - AABB
   - sphere
   - optional cone data
   - error metric inputs
5. Serialize the result into `NanoMeshAsset`.

## Technical Rules

- Do not assume one single root cluster will always exist.
- Allow multiple coarse roots.
- Record bake warnings when simplification quality is poor.
- Keep unsupported mesh types out of the bake path for v1.

## Recommended Reference Usage

- `meshoptimizer` for clusterization, simplification, quantization
- `nanite-webgpu` for simplification fallback expectations and hierarchy reasoning

## Acceptance

- A high-poly static mesh can be baked into a reusable asset.
- Reopening the project preserves the asset.
- Bake output includes stable counts for clusters and hierarchy depth.
- Unsupported content fails clearly with editor warnings.

## Dependencies

- [Step_01_Project_Skeleton.md](D:/UnityProj/testBakery/NanoMesh/Step_01_Project_Skeleton.md)

