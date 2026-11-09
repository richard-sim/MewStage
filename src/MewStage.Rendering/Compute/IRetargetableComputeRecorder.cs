// The render-graph pass context exposes ONE compute recorder per frame slot; a device's CreateComputeRecorder
// allocates a class, so the scheduler creates it once per slot and re-targets it to the frame's command list
// instead of allocating on the per-frame path.

using MewStage.Graphics;

namespace MewStage.Rendering.Compute;

/// <summary>A compute recorder that can be pointed at a new command list.</summary>
internal interface IRetargetableComputeRecorder : IComputeRecorder
{
    /// <summary>The command list the recorder currently forwards to.</summary>
    ICommandList CommandList { get; }

    /// <summary>Points the recorder at this frame's command list (same device, same queue kind).</summary>
    /// <param name="cmd">The command list.</param>
    void Retarget(ICommandList cmd);
}
