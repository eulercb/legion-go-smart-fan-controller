namespace LegionGoFanControl.Core;

/// <summary>Serializes all hardware access. Call TickAsync at the configured polling interval.</summary>
public sealed class FanController
{
    private readonly IFanHardware hardware;
    private ControllerOptions options;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ControllerSnapshot snapshot = new(ControllerMode.Automatic, ControllerState.Starting, null, null, false, null, null);
    private bool initialized, owned, blocked, suspended, stopped, sensorHung;
    private long? cooldownTick, previousTick;
    private DateTimeOffset? cooldownStarted;
    private DateTimeOffset? lastSampleTimestamp;
    private bool telemetryValid;
    private bool? actual;

    public FanController(IFanHardware hardware, ControllerOptions? options = null, TimeProvider? timeProvider = null)
    {
        this.hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
        this.options = options ?? new ControllerOptions();
        this.options.Validate();
        time = timeProvider ?? TimeProvider.System;
    }
    public ControllerSnapshot Snapshot => Volatile.Read(ref snapshot);
    public ControllerOptions Options => options;

    private void Publish(ControllerState state, double? temperature = null, string? error = null) =>
        Volatile.Write(ref snapshot, new(snapshot.Mode, state, temperature, actual, owned, error, cooldownStarted));
    private void ClearCooldown() { cooldownTick = null; cooldownStarted = null; }
    private void Mode(ControllerMode mode) => Volatile.Write(ref snapshot, snapshot with { Mode = mode });

    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await TickCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    private async Task TickCoreAsync(CancellationToken ct)
    {
        if (stopped || suspended) return;
        try
        {
            if (sensorHung) { Publish(ControllerState.Error, error: "Temperature read timed out; restart the application before retrying hardware access."); return; }
            bool observed = await hardware.ReadFullSpeedAsync(ct).ConfigureAwait(false);
            actual = observed;
            if (initialized && owned && !observed)
            {
                owned = false; blocked = true; ClearCooldown();
                Publish(ControllerState.Interference, error: "Full Speed changed externally; control is paused until the mode is selected again.");
                return;
            }
            initialized = true;
            if (blocked) { Publish(ControllerState.Interference, error: "Control paused after an ambiguous or external fan state change; select a mode to retry."); return; }
            if (snapshot.Mode == ControllerMode.Firmware)
            {
                if (owned && !await WriteAsync(false, ct).ConfigureAwait(false)) return;
                Publish(actual == true && !owned ? ControllerState.ExternalFullSpeed : ControllerState.Firmware);
                return;
            }
            if (snapshot.Mode == ControllerMode.ManualFullSpeed)
            {
                if (!observed && !await WriteAsync(true, ct).ConfigureAwait(false)) return;
                Publish(owned ? ControllerState.FullSpeed : ControllerState.ExternalFullSpeed);
                return;
            }
            TemperatureSample sample;
            try { sample = await hardware.ReadTemperatureAsync(ct).WaitAsync(options.MaximumSampleAge, ct).ConfigureAwait(false); }
            catch (TimeoutException) { sensorHung = true; blocked = true; throw new TimeoutException("Temperature read timed out; hardware access is stopped until restart."); }
            DateTimeOffset now = time.GetUtcNow();
            if (!double.IsFinite(sample.Celsius) || sample.Celsius < 0 || sample.Celsius > 120 || sample.Timestamp > now + TimeSpan.FromSeconds(2) || now - sample.Timestamp > options.MaximumSampleAge || (lastSampleTimestamp.HasValue && sample.Timestamp < lastSampleTimestamp))
                throw new InvalidOperationException("Temperature reading is invalid, stale, or has a future timestamp.");
            lastSampleTimestamp = sample.Timestamp; telemetryValid = true;
            long tick = time.GetTimestamp();
            if (previousTick.HasValue && time.GetElapsedTime(previousTick.Value, tick) > options.MaximumSampleAge) ClearCooldown();
            previousTick = tick;
            if (sample.Celsius >= options.OnTemperature)
            {
                ClearCooldown();
                if (!observed && !await WriteAsync(true, ct).ConfigureAwait(false)) return;
            }
            else if (owned && sample.Celsius <= options.OffTemperature)
            {
                if (!cooldownTick.HasValue) { cooldownTick = tick; cooldownStarted = now; }
                if (time.GetElapsedTime(cooldownTick.Value, tick) >= options.ReleaseCooldown)
                {
                    if (!await WriteAsync(false, ct).ConfigureAwait(false)) return;
                    ClearCooldown();
                }
            }
            else ClearCooldown();
            Publish(cooldownTick.HasValue ? ControllerState.CoolingDown : owned ? ControllerState.FullSpeed : actual == true ? ControllerState.ExternalFullSpeed : ControllerState.Monitoring, sample.Celsius);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { ClearCooldown(); throw; }
        catch (Exception ex)
        {
            ClearCooldown(); previousTick = null; telemetryValid = false;
            Publish(ControllerState.Error, error: ex.Message);
        }
    }
    // A failed write or readback may have changed the hardware. Relinquish ownership and stop writing.
    private async Task<bool> WriteAsync(bool enabled, CancellationToken ct)
    {
        try
        {
            FanWriteResult result = await hardware.SetFullSpeedAsync(enabled, ct).ConfigureAwait(false);
            actual = await hardware.ReadFullSpeedAsync(ct).ConfigureAwait(false);
            if (actual != enabled) throw new InvalidOperationException("Full Speed readback disagrees with the requested state.");
            owned = enabled && result == FanWriteResult.Changed;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { owned = false; blocked = true; actual = null; throw; }
        catch (Exception ex) { owned = false; blocked = true; actual = null; ClearCooldown(); Publish(ControllerState.Error, error: ex.Message); return false; }
    }
    public async Task UpdateOptionsAsync(ControllerOptions newOptions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newOptions);
        newOptions.Validate();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (stopped) throw new InvalidOperationException("Controller has stopped.");
            options = newOptions; ClearCooldown(); previousTick = null; telemetryValid = false;
            await TickCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    public async Task SetModeAsync(ControllerMode mode, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (stopped) throw new InvalidOperationException("Controller has stopped.");
            Mode(mode); ClearCooldown(); if (!sensorHung) blocked = false;
            await TickCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    public async Task SuspendAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Release before sleep only with a fresh reading showing a cool CPU.
            if (owned && !blocked && !sensorHung && telemetryValid && lastSampleTimestamp.HasValue &&
                time.GetUtcNow() - lastSampleTimestamp.Value <= options.MaximumSampleAge &&
                snapshot.Temperature is double temperature && temperature <= options.OffTemperature)
            {
                try
                {
                    actual = await hardware.ReadFullSpeedAsync(cancellationToken).ConfigureAwait(false);
                    if (actual == true) await WriteAsync(false, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { blocked = true; Publish(ControllerState.Error, error: ex.Message); }
            }
            suspended = true; owned = false; initialized = false; actual = null; ClearCooldown(); previousTick = null; telemetryValid = false;
            Publish(ControllerState.Suspended);
        }
        finally { gate.Release(); }
    }
    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { if (stopped) return; suspended = false; initialized = false; if (!sensorHung) blocked = false; lastSampleTimestamp = null; await TickCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (stopped) return;
            ClearCooldown();
            if (owned && snapshot.Mode == ControllerMode.Automatic && (!telemetryValid || !lastSampleTimestamp.HasValue || time.GetUtcNow() - lastSampleTimestamp.Value > options.MaximumSampleAge))
            {
                stopped = true; owned = false;
                Publish(ControllerState.Error, error: "Full Speed left enabled because temperature telemetry is unavailable or stale.");
                return;
            }
            if (owned && !blocked && !suspended)
            {
                try
                {
                    actual = await hardware.ReadFullSpeedAsync(cancellationToken).ConfigureAwait(false);
                    if (actual == true && !await WriteAsync(false, cancellationToken).ConfigureAwait(false)) { stopped = true; return; }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { stopped = true; owned = false; actual = null; Publish(ControllerState.Error, error: ex.Message); return; }
            }
            owned = false; stopped = true; Publish(ControllerState.Stopped);
        }
        finally { gate.Release(); }
    }
}






