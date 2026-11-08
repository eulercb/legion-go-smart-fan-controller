using LegionGoFanControl.Core;

namespace LegionGoFanControl.Tray;

// One persistent menu keeps the hosted controls and their keyboard focus intact while dragging.
internal sealed class SettingsMenu : ContextMenuStrip
{
    private ControllerOptions options;
    private readonly Func<ControllerOptions, Task<bool>> apply;
    private readonly Func<bool, Task<bool>> changeStartup;
    private readonly bool preview, conflict;
    internal bool SuppressValidationMessage;
    private readonly CheckBox startup = new() { AutoSize = true, Text = "Start at Windows sign-in", AccessibleName = "Start at Windows sign-in" };
    private readonly ToolTip tips = new();
    private ToolStripControlHost? startupHost;
    private readonly List<SliderRow> rows = new();
    private bool loadingStartup, busy;

    internal SettingsMenu(ControllerOptions options, bool preview, bool conflict, Func<ControllerOptions, Task<bool>> apply, Func<bool, Task<bool>> changeStartup)
    {
        this.options = options; this.preview = preview; this.conflict = conflict; this.apply = apply; this.changeStartup = changeStartup;
        ShowImageMargin = false; Padding = new Padding(4); AutoClose = true;
        Add("CPU Full Speed on", "°C", 80, 200, 2, o => o.OnTemperature, (o, v) => o with { OnTemperature = v });
        Add("CPU Full Speed off", "°C", 60, 199, 2, o => o.OffTemperature, (o, v) => o with { OffTemperature = v });
        Add("SSD Full Speed on", "°C", 80, 200, 2, o => o.SsdOnTemperature, (o, v) => o with { SsdOnTemperature = v });
        Add("SSD Full Speed off", "°C", 60, 199, 2, o => o.SsdOffTemperature, (o, v) => o with { SsdOffTemperature = v });
        Add("Poll interval", "seconds", 1, 60, 1, o => o.PollInterval.TotalSeconds, (o, v) => o with { PollInterval = TimeSpan.FromSeconds(v), MaximumSampleAge = TimeSpan.FromSeconds(v) > o.MaximumSampleAge ? TimeSpan.FromSeconds(v) : o.MaximumSampleAge });
        Add("Release cooldown", "seconds", 0, Math.Min(1800, Math.Max(120, (int)Math.Ceiling(options.ReleaseCooldown.TotalSeconds))), 1, o => o.ReleaseCooldown.TotalSeconds, (o, v) => o with { ReleaseCooldown = TimeSpan.FromSeconds(v) });
        startupHost = new ToolStripControlHost(startup) { AutoSize = true, Margin = new Padding(8, 4, 8, 4) }; Items.Add(startupHost);
        startup.CheckedChanged += async (_, _) => { if (loadingStartup || busy) return; busy = true; startup.Enabled = false; bool value = startup.Checked; if (!await changeStartup(value)) { loadingStartup = true; startup.Checked = !value; loadingStartup = false; } startup.Enabled = true; busy = false; };
        Opening += (_, _) => RefreshStartup();
    }
    private void RefreshStartup()
    {
        loadingStartup = true;
        try
        {
            if (preview) throw new InvalidOperationException(conflict ? "A different scheduled task already uses the startup task name. It was not changed." : "Startup is unavailable in preview.");
            startup.Checked = StartupTask.IsEnabled(); startup.Enabled = true; startupHost!.ToolTipText = ""; tips.SetToolTip(startup, "");
        }
        catch (Exception ex) { startup.Enabled = false; startupHost!.ToolTipText = ex.Message; tips.SetToolTip(startup, ex.Message); }
        finally { loadingStartup = false; }
    }
    private void Add(string name, string unit, int min, int max, int scale, Func<ControllerOptions, double> read, Func<ControllerOptions, double, ControllerOptions> update)
    {
        var row = new SliderRow(name, unit, min, max, scale, read(options)); rows.Add(row);
        Items.Add(new ToolStripControlHost(row) { AutoSize = false, Size = row.Size, Margin = new Padding(8, 1, 8, 1) });
        row.Commit = async () =>
        {
            if (busy) { row.SetValue(read(options)); return false; }
            busy = true; row.Enabled = false;
            var next = update(options, row.Value);
            try { next.Validate(); if (await apply(next)) { options = next; return true; } return false; }
            catch (Exception ex) { if (!SuppressValidationMessage) MessageBox.Show(ex.Message, "Invalid settings", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
            finally { row.SetValue(read(options)); row.Enabled = true; busy = false; }
        };
    }
    internal bool StartupAvailable => startup.Enabled;
    internal SliderRow Row(int index) => rows[index];
    internal void VerifyLayout()
    {
        foreach (var row in rows) if (!row.Visible || row.Width < 200 || row.Height < 50 || row.Bottom > ClientSize.Height) throw new InvalidOperationException("Slider is clipped or hidden.");
        if (Height > 800 || Height > Screen.FromControl(this).WorkingArea.Height) throw new InvalidOperationException("Settings menu exceeds available screen height.");
    }
    protected override void Dispose(bool disposing) { if (disposing) tips.Dispose(); base.Dispose(disposing); }
}

internal sealed class SliderRow : UserControl
{
    private readonly Label label = new() { AutoSize = false, Dock = DockStyle.Top, Height = 36 };
    private readonly TrackBar slider = new() { Dock = DockStyle.Fill, TickStyle = TickStyle.None, AutoSize = false, Height = 32 };
    private readonly string name, unit;
    private readonly int scale;
    private double exact;
    private bool changed;
    private readonly System.Windows.Forms.Timer wheelTimer = new() { Interval = 400 };
    internal Func<Task<bool>>? Commit;
    internal double Value => changed ? (double)slider.Value / scale : exact;
    internal SliderRow(string name, string unit, int min, int max, int scale, double value)
    {
        this.name = name; this.unit = unit; this.scale = scale;
        Size = new Size(400, 64); Margin = Padding.Empty;
        slider.Minimum = min; slider.Maximum = max; slider.SmallChange = 1; slider.LargeChange = scale * 5; slider.AccessibleName = name + " in " + unit;
        Controls.Add(slider); Controls.Add(label); SetValue(value);
        slider.ValueChanged += (_, _) => { changed = true; UpdateLabel(); };
        wheelTimer.Tick += (_, _) => { wheelTimer.Stop(); Finish(); };
        slider.MouseWheel += (_, _) => { wheelTimer.Stop(); wheelTimer.Start(); };
        slider.MouseUp += (_, _) => Finish(); slider.KeyUp += (_, e) => { if (e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End) Finish(); };
    }
    internal void SetValue(double value) { exact = value; slider.Value = Math.Clamp((int)Math.Round(value * scale), slider.Minimum, slider.Maximum); changed = false; UpdateLabel(); }
    private void UpdateLabel() { label.Text = $"{name}: {Value:0.########} {unit}"; slider.AccessibleDescription = label.Text; }
    protected override void Dispose(bool disposing) { if (disposing) wheelTimer.Dispose(); base.Dispose(disposing); }
    private async void Finish() { if (changed && Commit is not null) await Commit(); }
    internal void PreviewValue(int raw) => slider.Value = raw;
    internal Task<bool> CommitForSmokeAsync() => changed && Commit is not null ? Commit() : Task.FromResult(false);
}
