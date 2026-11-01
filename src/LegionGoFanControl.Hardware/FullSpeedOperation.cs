using LegionGoFanControl.Core;

namespace LegionGoFanControl.Hardware;

internal static class FullSpeedOperation
{
    internal static FanWriteResult Set(bool enabled, CancellationToken token, Action requireDevice, Func<bool> read, Action<bool> write)
    {
        requireDevice();
        if (read() == enabled) return FanWriteResult.AlreadyInRequestedState;
        token.ThrowIfCancellationRequested();
        write(enabled);
        if (read() != enabled) throw new InvalidOperationException("Full Speed readback did not match the requested state; write outcome is uncertain.");
        return FanWriteResult.Changed;
    }
}
