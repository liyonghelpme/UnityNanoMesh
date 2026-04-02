using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace NanoMesh
{
    public sealed class NanoMeshRuntimeBuffers : IDisposable
    {
        private const int IndirectArgsIntCount = 5;

        public GraphicsBuffer InstanceBuffer { get; private set; }
        public GraphicsBuffer VisibleInstanceBuffer { get; private set; }
        public GraphicsBuffer VisibleClusterBuffer { get; private set; }
        public GraphicsBuffer TraversalFrontierBufferA { get; private set; }
        public GraphicsBuffer TraversalFrontierBufferB { get; private set; }
        public GraphicsBuffer IndirectArgsBuffer { get; private set; }
        public GraphicsBuffer DebugCountersBuffer { get; private set; }

        public bool IsAllocated { get; private set; }
        public int InstanceCapacity { get; private set; }
        public int VisibleClusterCapacity { get; private set; }
        public int TraversalCapacity { get; private set; }
        public int DebugCounterCount { get; private set; }
        public long TransientGpuBytes =>
            ComputeBufferBytes(InstanceBuffer) +
            ComputeBufferBytes(VisibleInstanceBuffer) +
            ComputeBufferBytes(VisibleClusterBuffer) +
            ComputeBufferBytes(TraversalFrontierBufferA) +
            ComputeBufferBytes(TraversalFrontierBufferB) +
            ComputeBufferBytes(IndirectArgsBuffer) +
            ComputeBufferBytes(DebugCountersBuffer);

        public void EnsureFrameBuffers(int instanceCapacity, int visibleClusterCapacity, int traversalCapacity, int debugCounterCount)
        {
            instanceCapacity = Math.Max(1, instanceCapacity);
            visibleClusterCapacity = Math.Max(1, visibleClusterCapacity);
            traversalCapacity = Math.Max(1, traversalCapacity);
            debugCounterCount = Math.Max(1, debugCounterCount);

            var needsInstanceResize = InstanceBuffer == null || VisibleInstanceBuffer == null || InstanceCapacity < instanceCapacity;
            if (needsInstanceResize)
            {
                InstanceBuffer?.Release();
                InstanceCapacity = instanceCapacity;
                InstanceBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopySource,
                    InstanceCapacity,
                    Marshal.SizeOf<NanoMeshManager.NanoMeshInstanceData>());
            }

            if (VisibleClusterBuffer == null || VisibleClusterCapacity < visibleClusterCapacity)
            {
                VisibleClusterBuffer?.Release();
                VisibleClusterCapacity = visibleClusterCapacity;
                VisibleClusterBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopySource,
                    VisibleClusterCapacity,
                    Marshal.SizeOf<NanoMeshManager.VisibleClusterRecord>());
            }

            var needsTraversalResize =
                TraversalFrontierBufferA == null ||
                TraversalFrontierBufferB == null ||
                TraversalCapacity < traversalCapacity;
            if (needsTraversalResize)
            {
                TraversalFrontierBufferA?.Release();
                TraversalFrontierBufferA = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopySource,
                    traversalCapacity,
                    Marshal.SizeOf<NanoMeshManager.TraversalNodeRecord>());
            }

            if (needsTraversalResize)
            {
                TraversalFrontierBufferB?.Release();
                TraversalFrontierBufferB = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopySource,
                    traversalCapacity,
                    Marshal.SizeOf<NanoMeshManager.TraversalNodeRecord>());
            }

            if (needsInstanceResize)
            {
                VisibleInstanceBuffer?.Release();
                VisibleInstanceBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Append | GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopySource,
                    instanceCapacity,
                    sizeof(uint));
            }

            if (IndirectArgsBuffer == null)
            {
                IndirectArgsBuffer?.Release();
                IndirectArgsBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.CopySource,
                    IndirectArgsIntCount,
                    sizeof(uint));
            }

            if (DebugCountersBuffer == null || DebugCounterCount < debugCounterCount)
            {
                DebugCountersBuffer?.Release();
                DebugCounterCount = debugCounterCount;
                DebugCountersBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopySource,
                    DebugCounterCount,
                    sizeof(uint));
            }

            if (needsTraversalResize)
            {
                TraversalCapacity = traversalCapacity;
            }

            IsAllocated = true;
        }

        internal void UploadInstances(List<NanoMeshManager.NanoMeshInstanceData> instanceData)
        {
            if (InstanceBuffer == null || instanceData == null)
            {
                return;
            }

            if (instanceData.Count > 0)
            {
                InstanceBuffer.SetData(instanceData);
            }
        }

        public void ResetPerFrameBuffers()
        {
            if (VisibleInstanceBuffer != null)
            {
                VisibleInstanceBuffer.SetCounterValue(0);
            }

            if (VisibleClusterBuffer != null)
            {
                VisibleClusterBuffer.SetData(new NanoMeshManager.VisibleClusterRecord[Math.Max(1, VisibleClusterCapacity)]);
            }

            if (TraversalFrontierBufferA != null)
            {
                TraversalFrontierBufferA.SetData(new NanoMeshManager.TraversalNodeRecord[Math.Max(1, TraversalCapacity)]);
            }

            if (TraversalFrontierBufferB != null)
            {
                TraversalFrontierBufferB.SetData(new NanoMeshManager.TraversalNodeRecord[Math.Max(1, TraversalCapacity)]);
            }

            if (IndirectArgsBuffer != null)
            {
                IndirectArgsBuffer.SetData(new uint[IndirectArgsIntCount]);
            }

            if (DebugCountersBuffer != null)
            {
                DebugCountersBuffer.SetData(new uint[Math.Max(1, DebugCounterCount)]);
            }
        }

        public void ResetClusterCullingBuffers()
        {
            if (VisibleClusterBuffer != null)
            {
                VisibleClusterBuffer.SetData(new NanoMeshManager.VisibleClusterRecord[Math.Max(1, VisibleClusterCapacity)]);
            }

            if (TraversalFrontierBufferA != null)
            {
                TraversalFrontierBufferA.SetData(new NanoMeshManager.TraversalNodeRecord[Math.Max(1, TraversalCapacity)]);
            }

            if (TraversalFrontierBufferB != null)
            {
                TraversalFrontierBufferB.SetData(new NanoMeshManager.TraversalNodeRecord[Math.Max(1, TraversalCapacity)]);
            }

            if (IndirectArgsBuffer != null)
            {
                IndirectArgsBuffer.SetData(new uint[IndirectArgsIntCount]);
            }

            if (DebugCountersBuffer != null)
            {
                DebugCountersBuffer.SetData(new uint[Math.Max(1, DebugCounterCount)]);
            }
        }

        public void Release()
        {
            InstanceBuffer?.Release();
            VisibleInstanceBuffer?.Release();
            VisibleClusterBuffer?.Release();
            TraversalFrontierBufferA?.Release();
            TraversalFrontierBufferB?.Release();
            IndirectArgsBuffer?.Release();
            DebugCountersBuffer?.Release();
            InstanceBuffer = null;
            VisibleInstanceBuffer = null;
            VisibleClusterBuffer = null;
            TraversalFrontierBufferA = null;
            TraversalFrontierBufferB = null;
            IndirectArgsBuffer = null;
            DebugCountersBuffer = null;
            InstanceCapacity = 0;
            VisibleClusterCapacity = 0;
            TraversalCapacity = 0;
            DebugCounterCount = 0;
            IsAllocated = false;
        }

        public void Dispose()
        {
            Release();
        }

        private static long ComputeBufferBytes(GraphicsBuffer buffer)
        {
            if (buffer == null)
            {
                return 0L;
            }

            return (long)buffer.count * buffer.stride;
        }
    }
}
