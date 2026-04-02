# NanoMesh

[English](README.md)

NanoMesh 是一个受 Unreal Engine 5 Nanite 启发的实验性 Unity 项目。它将普通 Mesh 烘焙为分簇的 NanoMesh 资产，并在运行时通过 GPU 友好的裁剪与 LOD 选择进行渲染。

## 概述

- 将源 Mesh 烘焙为紧凑的 `NanoMeshAsset` 数据
- 将几何体组织为 cluster 和 hierarchy node
- 在运行时执行 instance culling 和 cluster culling
- 通过 URP 集成程序化绘制
- 包含示例内容和验证测试

## 基本算法

1. 从普通 Unity Mesh 开始。
2. 将 Mesh 三角形切分为较小的几何 cluster。
3. 为这些 cluster 构建层级结构，支持由粗到细的遍历。
4. 将顶点和索引量化并打包进紧凑的 `NanoMeshAsset`。
5. 在运行时把烘焙后的数据上传到 GPU buffer。
6. 先做实例级裁剪，再遍历 cluster hierarchy 找出可见区域。
7. 根据误差和相机距离选择更粗或更细的 cluster 层级。
8. 最后通过 URP 以 procedural draw 的方式绘制选中的 cluster。

## 基本工作流

1. 在 Unity 中导入或创建源 Mesh。
2. 使用编辑器工具将其烘焙为 `NanoMeshAsset`。
3. 在场景中把烘焙资产赋给 `NanoMeshRenderer`。
4. 由 `NanoMeshManager` 注册实例并准备运行时 buffer。
5. 渲染时执行 instance culling 和 cluster culling/LOD。
6. 通过 NanoMesh 的 URP Render Feature 绘制可见 cluster。

## 项目结构

- `Assets/NanoMesh`：NanoMesh 核心运行时代码、编辑器烘焙工具、着色器、示例和测试
- `Doc`：研究笔记和参考资料
- `Packages`：Unity 包依赖
- `ProjectSettings`：Unity 项目配置

## 文档

- `Doc/AdavanceRealtimeRendering_NanoMesh0810.md`：项目笔记与 NanoMesh 渲染概览
- `NanoMesh/NanoMesh_Design.md`：主设计文档
- `NanoMesh/Implementation_Steps.md`：Spec-driven implementation plan
- `NanoMesh/Step_01_Project_Skeleton.md` 到 `NanoMesh/Step_08_Debug_Profiling_And_Hardening.md`：分步骤规格文档

## 测试场景

- `Assets/NanoMesh/Samples/Scenes/NanoMeshValidation.unity`：用于测试 NanoMesh 渲染和运行时行为的示例验证场景

## 需求

- Unity 2022.3
- URP

## 当前状态

该仓库目前是一个实验性原型，主要用于 Unity 内部开发与验证。

## 来源说明

该项目完全由 AI 生成，不包含人工编写的生产代码。

## 开发方式

项目采用 spec-driven development 工作流。设计说明与实现步骤文档位于 `NanoMesh/` 目录中，作为项目结构和功能推进的主要参考。

## 许可证

MIT，详见 `LICENSE`。
