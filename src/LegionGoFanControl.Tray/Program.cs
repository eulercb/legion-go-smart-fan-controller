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
        bool asyncSmokeTest = args.Contains("--async-smoke-test");
        bool preview = args.Contains("--preview") || args.Contains("--smoke-test") || asyncSmokeTest;
        using var mutex = new Mutex(true, preview ? @"Local\LegionGoFanControl.Tray.Preview" : @"Local\LegionGoFanControl.Tray", out bool first);
        if (!first) { if (args.Contains("--smoke-test") || asyncSmokeTest) Environment.ExitCode = 1; else MessageBox.Show("The fan controller is already running."); return; }
        ApplicationConfiguration.Initialize();
        if (asyncSmokeTest) SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        if (args.Contains("--smoke-test"))
        {
            var boundary = new ControllerOptions { OnTemperature = 100, OffTemperature = 99.5, PollInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond + 1), ReleaseCooldown = TimeSpan.FromTicks(1) };
            boundary.Validate();
            using var settings = new SettingsForm(boundary);
            if (settings.Options != boundary) throw new InvalidOperationException("Settings boundary round-trip failed.");
        }
        int snapshotIndex = Array.IndexOf(args, "--snapshot");
        string? snapshot = preview && snapshotIndex >= 0 && snapshotIndex + 1 < args.Length ? args[snapshotIndex + 1] : null;
        Application.Run(new TrayContext(preview, args.Contains("--smoke-test") || asyncSmokeTest, args.Contains("--settings"), snapshot, asyncSmokeTest));
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
    private SettingsForm? settingsForm;
    private readonly ToolStripMenuItem status = new("Starting...") { Enabled = false };
    private readonly ToolStripMenuItem auto = new("Automatic monitoring");
    private readonly ToolStripMenuItem manual = new("Manual Full Speed");
    private readonly ToolStripMenuItem firmware = new("Pause / firmware control");
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
    private bool settingsShown;
    private bool closing;
    private bool configValid = true;
    private string? lastError;

    public TrayContext(bool preview, bool smokeTest, bool showSettings, string? snapshot, bool asyncSmokeTest = false)
    {
        this.preview = preview; uiThreadId = Environment.CurrentManagedThreadId; nativeUiThreadId = GetCurrentThreadId();
        directory = preview ? Path.Combine(Path.GetTempPath(), "LegionGoFanControl-preview-" + Guid.NewGuid().ToString("N")) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LegionGoFanControl");
        uiContext = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(uiContext);
        Directory.CreateDirectory(directory);
        log = new BoundedLog(LogPath);
        string? configError = null;
        try
        {
            options = File.Exists(ConfigPath) ? JsonSerializer.Deserialize<ControllerOptions>(File.ReadAllText(ConfigPath)) ?? throw new InvalidDataException("Settings file is empty.") : new();
            options.Validate();
        }
        catch (Exception ex) { options = new(); configError = ex.Message; }
        controller = new FanController(asyncSmokeTest ? new AsyncPreviewHardware() : preview ? new PreviewHardware() : new LenovoFanHardware(), options);
        var menu = new ContextMenuStrip();
        menu.Items.Add(status); menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(auto); menu.Items.Add(manual); menu.Items.Add(firmware);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings...", null, (_, _) => uiContext.Post(_ => ShowSettings(), null));
        menu.Items.Add("Open settings file", null, (_, _) => OpenFile(ConfigPath));
        menu.Items.Add("Open logs", null, (_, _) => OpenFile(LogPath));
        menu.Items.Add("Exit", null, async (_, _) => await ExitAsync());
        auto.Click += async (_, _) => await ChangeModeAsync(ControllerMode.Automatic);
        manual.Click += async (_, _) => await ChangeModeAsync(ControllerMode.ManualFullSpeed);
        firmware.Click += async (_, _) => await ChangeModeAsync(ControllerMode.Firmware);
        using var iconStream = typeof(TrayContext).Assembly.GetManifestResourceStream("LegionGoFanControl.Tray.fan.ico")!;
        using var resourceIcon = new Icon(iconStream);
        appIcon = (Icon)resourceIcon.Clone();
        icon = new NotifyIcon { Icon = appIcon, ContextMenuStrip = menu, Text = "Legion Go Fan Control", Visible = true };
        SystemEvents.PowerModeChanged += PowerModeChanged;
        if (configError is not null)
        {
            configValid = false;
            Log("Invalid configuration: " + configError);
            MessageBox.Show("Invalid settings; control is paused. Fix settings before selecting a mode.\n\n" + configError, "Legion Go Fan Control", MessageBoxButtons.OK, MessageBoxIcon.Error);
            // Firmware mode performs no writes on a newly created controller.
            uiContext.Post(_ => loop = StartPausedAsync(), null);
        }
        else uiContext.Post(_ => loop = PollAsync(cancellation.Token), null);
        if (showSettings) uiContext.Post(_ => ShowSettings(), null);
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
                        var original = settingsForm ?? throw new InvalidOperationException("Settings did not open.");
                        if (!settingsShown || !original.Visible) throw new InvalidOperationException("Settings Shown event did not fire.");
                        if (asyncSmokeTest && settingsPaints < 1) throw new InvalidOperationException("Settings did not paint.");
                        if (GetWindowThreadProcessId(original.Handle, out _) != nativeUiThreadId) throw new InvalidOperationException("Settings window belongs to the wrong native thread.");
                        ShowSettings();
                        if (!ReferenceEquals(original, settingsForm) || Application.OpenForms.OfType<SettingsForm>().Count() != 1) throw new InvalidOperationException("Settings window was duplicated.");

                        if (snapshot is not null)
                        {
                            using var bitmap = new Bitmap(original.Width, original.Height);
                            original.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                            bitmap.Save(snapshot, System.Drawing.Imaging.ImageFormat.Png);
                        }
                        original.VerifyLayout();
                    }
                }
                catch (Exception ex) { Environment.ExitCode = 1; Log("Settings smoke test failed: " + ex); }
                await ExitAsync();
            };
            timer.Start();
        }
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
                await controller.TickAsync(); UpdateStatus();
                await Task.Delay(options.PollInterval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { ShowError("Polling stopped: " + ex.Message); }
    }
    private async Task ChangeModeAsync(ControllerMode mode)
    {
        if (!configValid) { ShowError("Save valid settings before enabling control."); return; }
        try { await controller.SetModeAsync(mode); UpdateStatus(); }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private void UpdateStatus()
    {
        if (Environment.CurrentManagedThreadId != uiThreadId) throw new InvalidOperationException("Status update occurred off the UI thread.");
        statusUpdates++;
        var s = controller.Snapshot;
        string temperature = s.Temperature is double t ? $"{t:0.0} °C" : "temperature unavailable";
        status.Text = $"{temperature} | {s.State} | Full Speed: {(s.FullSpeed is null ? "unknown" : s.FullSpeed.Value ? "on" : "off")}";
        icon.Text = status.Text.Length > 63 ? status.Text[..63] : status.Text;
        auto.Checked = s.Mode == ControllerMode.Automatic; manual.Checked = s.Mode == ControllerMode.ManualFullSpeed; firmware.Checked = s.Mode == ControllerMode.Firmware;
        if (s.Error is not null && s.Error != lastError) { lastError = s.Error; Log(s.Error); icon.ShowBalloonTip(5000, "Fan controller error", s.Error, ToolTipIcon.Error); }
        else if (s.Error is null) lastError = null;
    }
    private void ShowSettings()
    {
        if (Environment.CurrentManagedThreadId != uiThreadId) throw new InvalidOperationException("Settings must be shown on the UI thread.");
        if (settingsForm is { IsDisposed: false })
        {
            if (settingsForm.WindowState == FormWindowState.Minimized) settingsForm.WindowState = FormWindowState.Normal;
            if (!settingsForm.Visible) settingsForm.Show(); settingsForm.Activate(); return;
        }
        try
        {
            settingsForm = new SettingsForm(options, !preview && StartupTask.IsEnabled(), !preview) { Icon = appIcon, SaveAsync = ApplySettingsAsync };
            settingsForm.Shown += (_, _) => settingsShown = true;
            settingsForm.Paint += (_, _) => settingsPaints++;
            settingsForm.FormClosed += (_, _) => settingsForm = null;
            settingsForm.Show();
        }
        catch (Exception ex) { ShowError("Could not read startup settings: " + ex.Message); }
    }
    private async Task<bool> ApplySettingsAsync(ControllerOptions next, bool startupEnabled)
    {
        StartupTask.Snapshot? previousStartup = null;
        bool taskChanged = false;
        bool controllerUpdated = false;
        try
        {
            next.Validate();
            previousStartup = preview ? null : StartupTask.Capture();
            // Refresh an enabled task as well, so saving after moving the app updates its path.
            if (previousStartup is not null && (startupEnabled || previousStartup.Xml is not null)) { taskChanged = true; StartupTask.SetEnabled(startupEnabled); }
            string temporary = ConfigPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true }));
            await controller.UpdateOptionsAsync(next); controllerUpdated = true;
            File.Move(temporary, ConfigPath, true);
            options = next; configValid = true; UpdateStatus(); return true;
        }
        catch (Exception ex)
        {
            string rollbackError = "";
            if (controllerUpdated) try { await controller.UpdateOptionsAsync(options); } catch (Exception rollback) { rollbackError += "\nController settings could not be restored: " + rollback.Message; }
            if (taskChanged && previousStartup is not null)
                try { StartupTask.Restore(previousStartup); } catch (Exception rollback) { rollbackError += "\nStartup setting could not be restored: " + rollback.Message; }
            ShowError("Could not apply or save settings: " + ex.Message + rollbackError); return false;
        }
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
        if (closing) return; closing = true;
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        cancellation.Cancel();
        if (loop is not null) await loop;
        try { await controller.ShutdownAsync(); UpdateStatus(); if (controller.Snapshot.Error is string error) MessageBox.Show(error, "Shutdown", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        catch (Exception ex) { ShowError("Shutdown: " + ex.Message); }
        settingsForm?.Close(); icon.Visible = false; icon.Dispose(); appIcon.Dispose(); cancellation.Dispose(); ExitThread();
    }
    private void ShowError(string message) { Log(message); MessageBox.Show(message, "Legion Go Fan Control", MessageBoxButtons.OK, MessageBoxIcon.Error); }
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
