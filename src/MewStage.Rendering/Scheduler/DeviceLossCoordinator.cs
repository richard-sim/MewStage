// Device loss and recovery. On loss: recreate the device, swapchain and GPU resources (IGraphicsDevice.TryRecreate),
// re-upload static assets from their CPU-side source through the callback, then resume.

using System;

using MewStage.Graphics;

namespace MewStage.Rendering.Scheduler;

/// <summary>Wires <see cref="IGraphicsDevice.OnDeviceLost"/> to recreation and re-upload, and exposes a per-frame poll.</summary>
public sealed class DeviceLossCoordinator
{
    private readonly IGraphicsDevice _device;
    private readonly Action _reuploadStaticAssets;

    /// <summary>Creates the coordinator.</summary>
    /// <param name="device">The device.</param>
    /// <param name="reuploadStaticAssets">Called after a successful recreation to re-upload static GPU content.</param>
    public DeviceLossCoordinator(IGraphicsDevice device, Action reuploadStaticAssets)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _reuploadStaticAssets = reuploadStaticAssets ?? throw new ArgumentNullException(nameof(reuploadStaticAssets));
        _device.OnDeviceLost += OnLost;
    }

    /// <summary>Number of recoveries so far.</summary>
    public int RecoveryCount { get; private set; }

    /// <summary>True between a loss and its recovery.</summary>
    public bool IsLost { get; private set; }

    /// <summary>Polls the device once per frame; when lost or removed, recreates, re-uploads and resumes.</summary>
    /// <returns>True when a recovery happened this call.</returns>
    public bool PollAndRecover()
    {
        DeviceStatus status = _device.PollStatus();
        if (!IsLost && status == DeviceStatus.Ok)
        {
            return false;
        }

        if (_device.TryRecreate())
        {
            _reuploadStaticAssets();
            RecoveryCount++;
            IsLost = false;
            return true;
        }

        return false;
    }

    private void OnLost()
    {
        IsLost = true;
    }
}
