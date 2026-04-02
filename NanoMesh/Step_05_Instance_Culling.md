# Step 5: Instance Culling

## Goal

Reduce upstream work by rejecting whole NanoMesh instances before any cluster traversal.

## Deliverables

- compute pass for instance frustum culling
- compacted visible instance list
- per-frame counters for total vs visible instances

## Core Work

1. Add world-space bounds to runtime instance data.
2. Dispatch compute for instance frustum tests.
3. Append visible instances into a compact buffer.
4. Update later stages to consume the compacted list.

## Optional Work

- distance-based early rejection
- feature flags to disable instance culling per renderer for debugging

## Technical Rules

- Keep the no-culling fallback path available behind a debug toggle.
- Make counters visible in editor or on-screen.
- Use conservative bounds to avoid false negatives.

## Acceptance

- Off-screen instances no longer feed downstream cluster work.
- Visible instance count is stable under camera motion.
- Debug comparison between enabled and disabled instance culling is possible.

## Dependencies

- [Step_04_Baseline_Rendering.md](D:/UnityProj/testBakery/NanoMesh/Step_04_Baseline_Rendering.md)

