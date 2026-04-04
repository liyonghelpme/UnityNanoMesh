# NanoMesh

[中文说明](README.zh-CN.md)

NanoMesh is an experimental Unity project inspired by Unreal Engine 5 Nanite. It bakes regular meshes into clustered NanoMesh assets and renders them with GPU-friendly culling and LOD selection at runtime.

## Overview

- Bakes source meshes into compact `NanoMeshAsset` data.
- Organizes geometry into clusters and hierarchy nodes.
- Uses runtime instance and cluster culling.
- Integrates with URP for procedural rendering.
- Includes sample content and validation tests.

## Basic Algorithm

1. Start from a regular Unity mesh.
2. Split the mesh triangles into small geometry clusters.
3. Build a hierarchy over those clusters for coarse-to-fine traversal.
4. Quantize and pack vertex and index data into a compact `NanoMeshAsset`.
5. Upload the baked data to GPU buffers at runtime.
6. Cull whole instances first, then traverse cluster hierarchy for visible regions.
7. Select a coarser or finer cluster level based on error and camera distance.
8. Render the selected clusters with procedural draws in URP.

## Basic Workflow

1. Import or create a source mesh in Unity.
2. Bake it into a `NanoMeshAsset` with the editor tools.
3. Assign the baked asset to a `NanoMeshRenderer` in the scene.
4. Let `NanoMeshManager` register instances and prepare runtime buffers.
5. During rendering, run instance culling and cluster culling/LOD.
6. Draw the visible clusters through the NanoMesh URP render feature.

## Project Structure

- `Assets/NanoMesh`: core NanoMesh runtime, editor baking tools, shaders, samples, and tests
- `Doc`: research notes and reference material
- `Packages`: Unity package dependencies
- `ProjectSettings`: Unity project configuration

## Documentation

- `Doc/AdavanceRealtimeRendering_NanoMesh0810.md`: project notes and NanoMesh rendering overview
- `NanoMesh/NanoMesh_Design.md`: main design document
- `NanoMesh/Implementation_Steps.md`: spec-driven implementation plan
- `NanoMesh/Step_01_Project_Skeleton.md` to `NanoMesh/Step_08_Debug_Profiling_And_Hardening.md`: step-by-step specs

## Test Scene

- `Assets/NanoMesh/Samples/Scenes/NanoMeshValidation.unity`: sample validation scene for testing NanoMesh rendering and runtime behavior

## Requirements

- Unity 2022.3
- URP

## Status

This repository is currently an experimental prototype intended for development and validation inside Unity.

## Provenance

This project is fully AI-generated and does not contain human-written production code.

## Development Approach

Development is fully based on SDD (Spec-Driven Development).

The workflow is:

1. AI studies papers and related engineering references first, then produces the design document.
2. The design document is used to generate the implementation document.
3. The implementation document is then executed step by step.

The design and implementation steps are documented in the `NanoMesh/` folder and serve as the primary reference for the project structure and feature progression.

Related engineering references:

- [OpenAI: Harness Engineering](https://openai.com/zh-Hans-CN/index/harness-engineering/)
- [yage.ai: Harness Engineering Scalability](https://yage.ai/share/harness-engineering-scalability-20260330.html)
- [Cursor: Self-Driving Codebases](https://cursor.com/cn/blog/self-driving-codebases)
- [Cursor: Scaling Agents](https://cursor.com/blog/scaling-agents)
- [Anthropic: Harness Design for Long-Running Apps](https://www.anthropic.com/engineering/harness-design-long-running-apps)

## License

MIT. See `LICENSE`.
