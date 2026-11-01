namespace LegionGoFanControl.Core;

public interface IFanHardware
{
    Task<TemperatureSample> ReadTemperatureAsync(CancellationToken cancellationToken);
    Task<bool> ReadFullSpeedAsync(CancellationToken cancellationToken);
    Task<FanWriteResult> SetFullSpeedAsync(bool enabled, CancellationToken cancellationToken);
}

public enum FanWriteResult { Changed, AlreadyInRequestedState }
public sealed record TemperatureSample(double Celsius, DateTimeOffset Timestamp);
public enum ControllerMode { Automatic, ManualFullSpeed, Firmware }
public enum ControllerState { Starting, Monitoring, FullSpeed, CoolingDown, ExternalFullSpeed, Firmware, Suspended, Interference, Error, Stopped }

public sealed record ControllerOptions
{
    public double OnTemperature { get; init; } = 80;
    public double OffTemperature { get; init; } = 70;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan ReleaseCooldown { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaximumSampleAge { get; init; } = TimeSpan.FromSeconds(10);

    public void Validate()
    {
        if (!double.IsFinite(OnTemperature) || OnTemperature is < 40 or > 100) throw new ArgumentOutOfRangeException(nameof(OnTemperature));
        if (!double.IsFinite(OffTemperature) || OffTemperature < 30 || OffTemperature >= OnTemperature) throw new ArgumentOutOfRangeException(nameof(OffTemperature));
        if (PollInterval < TimeSpan.FromSeconds(1) || PollInterval > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(PollInterval));
        if (ReleaseCooldown < TimeSpan.Zero || ReleaseCooldown > TimeSpan.FromMinutes(30)) throw new ArgumentOutOfRangeException(nameof(ReleaseCooldown));
        if (MaximumSampleAge < PollInterval || MaximumSampleAge > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(MaximumSampleAge));
    }
}

public sealed record ControllerSnapshot(ControllerMode Mode, ControllerState State, double? Temperature, bool? FullSpeed, bool OwnsFullSpeed, string? Error, DateTimeOffset? CooldownStarted);

