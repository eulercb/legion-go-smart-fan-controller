using System.Diagnostics;
using System.Text.Json;
using LegionGoFanControl.Core;
using LegionGoFanControl.Hardware;
using Microsoft.Win32;

namespace LegionGoFanControl.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        bool uxDisabledSmoke = args.Contains("--ux-disabled-smoke");
        bool uxSmokeTest = args.Contains("--ux-smoke-test") || uxDisabledSmoke;
        bool asyncSmokeTest = args.Contains("--async-smoke-test") || args.Contains("--startup-conflict-smoke");
        bool preview = args.Contains("--preview") || args.Contains("--smoke-test") || asyncSmokeTest || uxSmokeTest;
        using var mutex = new Mutex(true, preview ? @"Local\LegionGoFanControl.Tray.Preview" : @"Local\LegionGoFanControl.Tray", out bool first);
        if (!first) { if (args.Contains("--smoke-test") || asyncSmokeTest || uxSmokeTest) Environment.ExitCode = 1; else MessageBox.Show("The fan controller is already running."); return; }
        ApplicationConfiguration.Initialize();
        if (asyncSmokeTest) SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        int snapshotIndex = Array.IndexOf(args, "--snapshot");
        string? snapshot = preview && snapshotIndex >= 0 && snapshotIndex + 1 < args.Length ? args[snapshotIndex + 1] : null;
        Application.Run(new TrayContext(preview, args.Contains("--smoke-test") || asyncSmokeTest, args.Contains("--settings"), snapshot, asyncSmokeTest, args.Contains("--startup-conflict-smoke"), uxSmokeTest, uxDisabledSmoke));
    }
}

internal sealed class TrayContext : ApplicationContext
{
    private readonly string directory;
    private string ConfigPath => Path.Combine(directory, "settings.json");
    private string LogPath => Path.Combine(directory, "controller.log");
    private readonly NotifyIcon icon;
    private readonly Icon appIcon;
    private readonly BoundedLog log;
    private readonly SettingsMenu menu;
    private bool enabled = true;
    private bool changing;
    private int saves;
    private bool failSaveForSmoke, suppressErrorsForSmoke;
    private readonly UxPreviewHardware? uxHardware;
    private TaskCompletionSource? changeFinished;
    private readonly ToolStripMenuItem status = new("Starting...") { Enabled = false };
    private readonly ToolStripMenuItem toggle = new("Disable fan control");
    private readonly Icon enabledIcon;
    private readonly Icon disabledIcon;
    private readonly CancellationTokenSource cancellation = new();
    private readonly FanController controller;
    private readonly SynchronizationContext uiContext;
    private readonly int uiThreadId;
    private int statusUpdates;
    private int settingsPaints;
    private readonly uint nativeUiThreadId;
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    private ControllerOptions options;
    private Task? loop;
    private readonly bool preview;
    private readonly bool startupConflictSmoke;
    private bool settingsShown;
    private bool closing;
    private bool configValid = true;
    private string? lastError;

    public TrayContext(bool preview, bool smokeTest, bool showSettings, string? snapshot, bool asyncSmokeTest = false, bool startupConflictSmoke = false, bool uxSmokeTest = false, bool uxDisabledSmoke = false)
    {
        this.preview = preview; this.startupConflictSmoke = startupConflictSmoke; uiThreadId = Environment.CurrentManagedThreadId; nativeUiThreadId = GetCurrentThreadId();
        directory = preview ? Path.Combine(Path.GetTempPath(), "LegionGoFanControl-preview-" + Guid.NewGuid().ToString("N")) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LegionGoFanControl");
        uiContext = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(uiContext);
        Directory.CreateDirectory(directory);
        log = new BoundedLog(LogPath);
        if (uxSmokeTest) File.WriteAllText(ConfigPath, (uxDisabledSmoke ? "{\"Enabled\":false," : "{") + "\"OnTemperature\":80,\"OffTemperature\":70,\"PollInterval\":\"00:00:02\",\"ReleaseCooldown\":\"00:00:10\",\"MaximumSampleAge\":\"00:00:10\"}");
        string? configError = null;
        try
        {
            options = File.Exists(ConfigPath) ? JsonSerializer.Deserialize<ControllerOptions>(File.ReadAllText(ConfigPath)) ?? throw new InvalidDataException("Settings file is empty.") : new();
            options.Validate();
            using var doc = JsonDocument.Parse(File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : "{}");
            if (doc.RootElement.TryGetProperty("Enabled", out var flag)) enabled = flag.GetBoolean();
        }
        catch (Exception ex) { options = new(); configError = ex.Message; }
        uxHardware = uxSmokeTest ? new UxPreviewHardware() : null;
        controller = new FanController(uxHardware is not null ? uxHardware : asyncSmokeTest ? new AsyncPreviewHardware() : preview ? new PreviewHardware() : new LenovoFanHardware(), options);
        menu = new SettingsMenu(options, preview, startupConflictSmoke, ApplySettingsAsync, ChangeStartupAsync);
        menu.Items.Insert(0, status); menu.Items.Insert(1, toggle); menu.Items.Insert(2, new ToolStripSeparator());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open logs", null, (_, _) => OpenFile(LogPath));
        menu.Items.Add("Exit", null, async (_, _) => await ExitAsync());
        toggle.Click += async (_, _) => await ChangeEnabledAsync(!enabled);
        using var iconStream = typeof(TrayContext).Assembly.GetManifestResourceStream("LegionGoFanControl.Tray.fan.ico")!;
        using var resourceIcon = new Icon(iconStream);
        appIcon = (Icon)resourceIcon.Clone();
        enabledIcon = MakeStatusIcon(Color.FromArgb(0, 155, 125)); disabledIcon = MakeStatusIcon(Color.Gray);
        icon = new NotifyIcon { Icon = enabledIcon, ContextMenuStrip = menu, Text = "Legion Go Fan Control", Visible = true };
        icon.MouseClick += async (_, e) => { if (e.Button == MouseButtons.Left) await ChangeEnabledAsync(!enabled); };
        menu.Paint += (_, _) => settingsPaints++;
        menu.Opened += (_, _) => settingsShown = true;
        SystemEvents.PowerModeChanged += PowerModeChanged;
        if (configError is not null)
        {
            configValid = false; enabled = false;
            Log("Invalid configuration: " + configError);
            MessageBox.Show("Invalid settings; control is paused. Fix settings before selecting a mode.\n\n" + configError, "Legion Go Fan Control", MessageBoxButtons.OK, MessageBoxIcon.Error);
            // Firmware mode performs no writes on a newly created controller.
            uiContext.Post(_ => loop = StartPausedAsync(), null);
        }
        else if (!enabled) uiContext.Post(_ => loop = StartPausedAsync(), null);
        else uiContext.Post(_ => loop = PollAsync(cancellation.Token), null);
        if (showSettings) uiContext.Post(_ => ShowSettings(), null);
        if (uxSmokeTest) uiContext.Post(async _ => { try { if (uxDisabledSmoke) await RunDisabledSmokeAsync(); else await RunUxSmokeAsync(); } catch (Exception ex) { Environment.ExitCode = 1; Log("UX smoke failed: " + ex); } await ExitAsync(); }, null);
                if (smokeTest)
        {
            int heartbeats = 0;
            var heartbeat = new System.Windows.Forms.Timer { Interval = 100 };
            heartbeat.Tick += (_, _) => heartbeats++; heartbeat.Start();
            var timer = new System.Windows.Forms.Timer { Interval = 1500 };
            timer.Tick += async (_, _) =>
            {
                timer.Stop(); timer.Dispose(); heartbeat.Stop(); heartbeat.Dispose();
                try
                {
                    if (asyncSmokeTest && (statusUpdates < 1 || heartbeats < 3)) throw new InvalidOperationException("Async status update or UI heartbeat missing.");
                    if (showSettings)
                    {
                        if (!settingsShown || !menu.Visible) throw new InvalidOperationException("Settings menu did not open.");
                        if (asyncSmokeTest && settingsPaints < 1) throw new InvalidOperationException("Settings menu did not paint.");
                        if (GetWindowThreadProcessId(menu.Handle, out _) != nativeUiThreadId) throw new InvalidOperationException("Settings menu belongs to the wrong native thread.");
                        ShowSettings();
                        if (!menu.Visible) throw new InvalidOperationException("Settings menu was not retained.");
                        if (snapshot is not null) { using var bitmap = new Bitmap(menu.Width, menu.Height); menu.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(snapshot, System.Drawing.Imaging.ImageFormat.Png); }
                        menu.VerifyLayout();
                        if (startupConflictSmoke && (menu.StartupAvailable || !await ApplySettingsAsync(options with { OnTemperature = options.OnTemperature + 1 }))) throw new InvalidOperationException("Settings save failed when startup is unavailable.");
                    }
                }
                catch (Exception ex) { Environment.ExitCode = 1; Log("Settings smoke test failed: " + ex); }
                await ExitAsync();
            };
            timer.Start();
        }
    }
    private async Task RunUxSmokeAsync()
    {
        var hw = uxHardware ?? throw new InvalidOperationException("Missing fake hardware.");
        await Task.Delay(500);
        if (!enabled || !hw.FullSpeed || !controller.Snapshot.OwnsFullSpeed || options.ReleaseCooldown != TimeSpan.FromSeconds(10)) throw new InvalidOperationException("Legacy config or owned automatic state failed.");
        string original = File.ReadAllText(ConfigPath); int writes = hw.OffWrites;
        failSaveForSmoke = true; suppressErrorsForSmoke = true; await ChangeEnabledAsync(false); failSaveForSmoke = false; suppressErrorsForSmoke = false;
        if (!enabled || !hw.FullSpeed || hw.OffWrites != writes || File.ReadAllText(ConfigPath) != original) throw new InvalidOperationException("Failed persistence changed owned state.");
        await ChangeEnabledAsync(false);
        if (enabled || hw.FullSpeed || hw.OffWrites != 1) throw new InvalidOperationException("Disable did not release owned override.");
        VerifySaved(false, 80, 70, 10);
        int reads = hw.Reads; await Task.Delay(2300);
        if (hw.Reads != reads) throw new InvalidOperationException("Disabled polling continued.");
        hw.FullSpeed = true; hw.Temperature = 65; int samples = hw.Samples;
        await ChangeEnabledAsync(true);
        if (!enabled || hw.Samples <= samples || !hw.FullSpeed || controller.Snapshot.State != ControllerState.ExternalFullSpeed) throw new InvalidOperationException("Re-enable did not take fresh sample or preserve external override.");
        VerifySaved(true, 80, 70, 10);
        await ChangeEnabledAsync(false);
        if (!hw.FullSpeed || hw.OffWrites != 1 || controller.Snapshot.State != ControllerState.ExternalFullSpeed) throw new InvalidOperationException("External override was changed.");
        int before = saves; var row = menu.Row(0); row.PreviewValue(162); row.PreviewValue(164);
        if (saves != before) throw new InvalidOperationException("Slider wrote during drag.");
        if (!await row.CommitForSmokeAsync() || saves != before + 1) throw new InvalidOperationException("Slider commit was not saved once.");
        VerifySaved(false, 82, 70, 10);
        before = saves; menu.SuppressValidationMessage = true; var off = menu.Row(1); off.PreviewValue(164);
        if (await off.CommitForSmokeAsync() || saves != before || options.OffTemperature != 70) throw new InvalidOperationException("Invalid threshold mutated settings.");
        VerifySaved(false, 82, 70, 10);
        Task pending = ChangeEnabledAsync(true);
        await ExitAsync();
        if (!pending.IsCompletedSuccessfully || !closing) throw new InvalidOperationException("Exit did not await the pending transition.");
    }
    private async Task RunDisabledSmokeAsync()
    {
        var hw = uxHardware ?? throw new InvalidOperationException("Missing fake hardware.");
        await Task.Delay(2300);
        if (enabled || hw.FullSpeed || hw.Samples != 0 || hw.OffWrites != 0 || controller.Snapshot.Mode != ControllerMode.Firmware) throw new InvalidOperationException("Disabled restart activated control.");
        VerifySaved(false, 80, 70, 10);
    }
    private void VerifySaved(bool expectedEnabled, double on, double off, int cooldown)
    {
        string json = File.ReadAllText(ConfigPath);
        var parsed = JsonSerializer.Deserialize<ControllerOptions>(json) ?? throw new InvalidOperationException("Missing flat options.");
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.GetProperty("Enabled").GetBoolean() != expectedEnabled || parsed.OnTemperature != on || parsed.OffTemperature != off || parsed.PollInterval != TimeSpan.FromSeconds(2) || parsed.ReleaseCooldown != TimeSpan.FromSeconds(cooldown) || parsed.MaximumSampleAge != TimeSpan.FromSeconds(10)) throw new InvalidOperationException("Persisted settings mismatch.");
    }
    private async Task StartPausedAsync()
    {
        await controller.SetModeAsync(ControllerMode.Firmware);
        UpdateStatus();
        await PollAsync(cancellation.Token);
    }
    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (enabled) { await controller.TickAsync(); UpdateStatus(); }
                await Task.Delay(options.PollInterval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { ShowError("Polling stopped: " + ex.Message); }
    }
    private async Task ChangeEnabledAsync(bool next)
    {
        if (changing || closing || next == enabled) return;
        if (!configValid && next) { ShowError("Save valid settings before enabling control."); return; }
        changing = true; changeFinished = new(); toggle.Enabled = false;
        try
        {
            // Persist first: a failed disk write must not alter ownership or fan state.
            SaveConfig(options, next); enabled = next;
            await controller.SetModeAsync(next ? ControllerMode.Automatic : ControllerMode.Firmware);
            UpdateStatus();
            if (controller.Snapshot.Error is string error) ShowError(error);
        }
        catch (Exception ex) { UpdateStatus(); ShowError(ex.Message); }
        finally { changing = false; toggle.Enabled = true; changeFinished.SetResult(); }
    }
    private void SaveConfig(ControllerOptions value, bool active)
    {
        if (failSaveForSmoke) throw new IOException("Injected settings write failure.");
        var data = JsonSerializer.SerializeToNode(value)!.AsObject(); data["Enabled"] = active;
        string temporary = ConfigPath + ".tmp";
        File.WriteAllText(temporary, data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, ConfigPath, true); saves++;
    }
    private static Icon MakeStatusIcon(Color color)
    {
        using var bitmap = new Bitmap(32, 32); using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color); g.FillEllipse(brush, 1, 1, 30, 30);
        using var blade = new SolidBrush(Color.White);
        for (int angle = 0; angle < 360; angle += 90) { g.ResetTransform(); g.TranslateTransform(16, 16); g.RotateTransform(angle); g.FillEllipse(blade, -2, -12, 7, 12); }
        g.ResetTransform(); g.FillEllipse(blade, 13, 13, 6, 6);
        IntPtr handle = bitmap.GetHicon(); try { using var temp = Icon.FromHandle(handle); return (Icon)temp.Clone(); } finally { DestroyIcon(handle); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
    private void UpdateStatus()
    {
        if (Environment.CurrentManagedThreadId != uiThreadId) throw new InvalidOperationException("Status update occurred off the UI thread.");
        statusUpdates++;
        var s = controller.Snapshot;
        string temperature = s.Temperature is double t ? $"{t:0.0} °C" : "temperature unavailable";
        status.Text = $"{temperature} | {s.State} | Full Speed: {(s.FullSpeed is null ? "unknown" : s.FullSpeed.Value ? "on" : "off")}";
        icon.Text = status.Text.Length > 63 ? status.Text[..63] : status.Text;
        toggle.Text = enabled ? "Disable fan control" : "Enable fan control";
        icon.Icon = enabled ? enabledIcon : disabledIcon;
        icon.Text = (enabled ? "Enabled: " : "Disabled: ") + (status.Text.Length > 50 ? status.Text[..50] : status.Text);
        if (s.Error is not null && s.Error != lastError) { lastError = s.Error; Log(s.Error); icon.ShowBalloonTip(5000, "Fan controller error", s.Error, ToolTipIcon.Error); }
        else if (s.Error is null) lastError = null;
    }
    private void ShowSettings()
    {
        if (Environment.CurrentManagedThreadId != uiThreadId) throw new InvalidOperationException("Settings must be shown on the UI thread.");
        if (!menu.Visible) menu.Show(Cursor.Position);
    }
    private async Task<bool> ApplySettingsAsync(ControllerOptions next)
    {
        if (changing || closing) return false;
        changing = true; changeFinished = new(); toggle.Enabled = false;
        try
        {
            next.Validate(); SaveConfig(next, enabled); options = next; configValid = true;
            await controller.UpdateOptionsAsync(next); UpdateStatus();
            if (controller.Snapshot.Error is string error) { ShowError("Settings saved, but controller reported: " + error); return true; }
            return true;
        }
        catch (Exception ex) { ShowError("Could not apply or save settings: " + ex.Message); return false; }
        finally { changing = false; toggle.Enabled = true; changeFinished.SetResult(); }
    }
    private Task<bool> ChangeStartupAsync(bool value)
    {
        try { StartupTask.SetEnabled(value); return Task.FromResult(true); }
        catch (Exception ex) { ShowError("Could not change startup: " + ex.Message); return Task.FromResult(false); }
    }
    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e) => uiContext.Post(async _ => await HandlePowerModeAsync(e.Mode), null);
    private async Task HandlePowerModeAsync(PowerModes mode)
    {
        try
        {
            if (mode == PowerModes.Suspend) await controller.SuspendAsync();
            else if (mode == PowerModes.Resume) await controller.ResumeAsync();
            UpdateStatus();
        }
        catch (Exception ex) { ShowError("Power transition: " + ex.Message); }
    }
    private async Task ExitAsync()
    {
        if (closing) return; closing = true; menu.Enabled = false;
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        if (changeFinished is { Task.IsCompleted: false } pending) await pending.Task;
        cancellation.Cancel();
        if (loop is not null) await loop;
        try { await controller.ShutdownAsync(); UpdateStatus(); if (controller.Snapshot.Error is string error) MessageBox.Show(error, "Shutdown", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        catch (Exception ex) { ShowError("Shutdown: " + ex.Message); }
        menu.Close(); menu.Dispose(); icon.Visible = false; icon.Dispose(); appIcon.Dispose(); enabledIcon.Dispose(); disabledIcon.Dispose(); cancellation.Dispose(); ExitThread();
    }
    private void ShowError(string message) { Log(message); if (suppressErrorsForSmoke) return; MessageBox.Show(message, "Legion Go Fan Control", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    private void Log(string message) => log.Write(message);
    private void OpenFile(string path)
    {
        try { if (!File.Exists(path)) File.WriteAllText(path, path == ConfigPath ? JsonSerializer.Serialize(options, new JsonSerializerOptions { WriteIndented = true }) : ""); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex.Message); }
    }
}

internal sealed class PreviewHardware : IFanHardware
{
    private bool fullSpeed;
    public Task<TemperatureSample> ReadTemperatureAsync(CancellationToken ct) => Task.FromResult(new TemperatureSample(65, DateTimeOffset.UtcNow));
    public Task<bool> ReadFullSpeedAsync(CancellationToken ct) => Task.FromResult(fullSpeed);
    public Task<FanWriteResult> SetFullSpeedAsync(bool enabled, CancellationToken ct) { var result = fullSpeed == enabled ? FanWriteResult.AlreadyInRequestedState : FanWriteResult.Changed; fullSpeed = enabled; return Task.FromResult(result); }
}

















internal sealed class AsyncPreviewHardware : IFanHardware
{
    public async Task<TemperatureSample> ReadTemperatureAsync(CancellationToken ct) { await Task.Delay(100, ct).ConfigureAwait(false); return new TemperatureSample(65, DateTimeOffset.UtcNow); }
    public async Task<bool> ReadFullSpeedAsync(CancellationToken ct) { await Task.Delay(100, ct).ConfigureAwait(false); return false; }
    public Task<FanWriteResult> SetFullSpeedAsync(bool enabled, CancellationToken ct) => throw new InvalidOperationException("Async smoke test cannot write hardware.");
}



internal sealed class UxPreviewHardware : IFanHardware
{
    internal bool FullSpeed; internal double Temperature = 90; internal int Reads, Samples, OffWrites;
    public async Task<TemperatureSample> ReadTemperatureAsync(CancellationToken ct) { Samples++; await Task.Delay(25, ct).ConfigureAwait(false); return new(Temperature, DateTimeOffset.UtcNow); }
    public async Task<bool> ReadFullSpeedAsync(CancellationToken ct) { Reads++; await Task.Delay(25, ct).ConfigureAwait(false); return FullSpeed; }
    public Task<FanWriteResult> SetFullSpeedAsync(bool value, CancellationToken ct) { if (!value) OffWrites++; var result = value == FullSpeed ? FanWriteResult.AlreadyInRequestedState : FanWriteResult.Changed; FullSpeed = value; return Task.FromResult(result); }
}
