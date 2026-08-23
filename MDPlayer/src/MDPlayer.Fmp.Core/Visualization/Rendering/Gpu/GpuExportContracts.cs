using System;

namespace Fmp.Core.Visualization.Rendering.Gpu;

/// <summary>
/// Describes one GPU-resident rendered frame ready for CUDA/NVENC consumption.
/// The pixel data stays on the GPU: <see cref="TextureId"/> is the OpenGL
/// texture (registered once with <c>cuGraphicsGLRegisterImage</c>) holding the
/// finished RGBA frame. No CPU buffer and no readback are involved.
/// </summary>
internal readonly record struct GpuExportFrame(int Slot, int TextureId, int Width, int Height);

/// <summary>
/// A bounded ring of GPU render targets that a source renders into, exposing
/// each finished frame as a live GL texture (the seam the in-process
/// CUDA/NVENC encoder consumes). Consumers hold at most <see cref="Capacity"/>
/// frames in flight: render into a slot, map its texture into CUDA, and call
/// <see cref="Release"/> only once the encoder has finished reading that slot so
/// it can be reused.
/// </summary>
internal interface IGpuExportSession : IDisposable
{
    /// <summary>Number of render targets in the ring (max frames in flight).</summary>
    int Capacity { get; }

    /// <summary>
    /// Renders frame <paramref name="frameIndex"/> into the next ring slot and
    /// returns its texture. Never reads back to the CPU. The returned texture's
    /// contents are valid until <see cref="Release"/> is called for that slot.
    /// </summary>
    GpuExportFrame RenderNext(long frameIndex, ReadOnlySpan<byte> scopeGrid);

    /// <summary>
    /// Marks slot <paramref name="slot"/> available for reuse. Must be called
    /// once the consumer has finished reading the slot's texture (e.g. after
    /// the CUDA copy landed in a hardware frame the encoder owns).
    /// </summary>
    void Release(int slot);
}

/// <summary>
/// A renderer that can produce GPU-resident frames without a GPU→CPU readback.
/// Only <see cref="GpuPanelRenderer"/> implements this today.
/// </summary>
internal interface IGpuExportSource
{
    /// <summary>
    /// Opens a GPU-resident export session. Returns null when this renderer's
    /// runtime does not support GPU export (callers fall back to the existing
    /// readback path). Throwing means the caller explicitly demanded GPU export
    /// and the backend is broken — fail loudly, never silently fall back.
    /// </summary>
    IGpuExportSession CreateGpuExportSession(
        int capacity = 4,
        bool scopeFramesAreOpaque = false);
}