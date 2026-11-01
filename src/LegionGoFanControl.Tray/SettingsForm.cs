using LegionGoFanControl.Core;

namespace LegionGoFanControl.Tray;

internal sealed class SettingsForm : Form
{
    private readonly NumericUpDown on = Number(40, 100, 2);
    private readonly NumericUpDown off = Number(30, 100, 2);
    private readonly NumericUpDown poll = Number(1, 60, 2);
    private readonly NumericUpDown cooldown = Number(0, 1800, 2);
    private readonly CheckBox startup = new() { Text = "Start at Windows sign-in", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly ControllerOptions original;
    private bool onChanged, offChanged, pollChanged, cooldownChanged;
    public bool? StartupEnabled => startup.Enabled ? startup.Checked : null;
    internal Func<ControllerOptions, bool?, Task<bool>>? SaveAsync;
    public ControllerOptions Options => original with { OnTemperature = onChanged ? (double)on.Value : original.OnTemperature, OffTemperature = offChanged ? (double)off.Value : original.OffTemperature, PollInterval = pollChanged ? TimeSpan.FromSeconds((double)poll.Value) : original.PollInterval, ReleaseCooldown = cooldownChanged ? TimeSpan.FromSeconds((double)cooldown.Value) : original.ReleaseCooldown, MaximumSampleAge = pollChanged && TimeSpan.FromSeconds((double)poll.Value) > original.MaximumSampleAge ? TimeSpan.FromSeconds((double)poll.Value) : original.MaximumSampleAge };
    public SettingsForm(ControllerOptions options, bool startAtLogon = false, bool startupAvailable = true, string? startupError = null)
    {
        original = options; Text = "Fan controller settings"; FormBorderStyle = FormBorderStyle.FixedDialog; StartPosition = FormStartPosition.CenterScreen; MaximizeBox = false; ClientSize = new Size(680, 360); AutoScaleMode = AutoScaleMode.Font;
        on.Value = (decimal)options.OnTemperature; off.Value = (decimal)options.OffTemperature; poll.Value = (decimal)options.PollInterval.TotalSeconds; cooldown.Value = (decimal)options.ReleaseCooldown.TotalSeconds; startup.Checked = startAtLogon; startup.Enabled = startupAvailable; if (!startupAvailable) startup.Text += startupError is null ? " (preview)" : " (unavailable)";
        on.ValueChanged += (_, _) => onChanged = true; off.ValueChanged += (_, _) => offChanged = true; poll.ValueChanged += (_, _) => pollChanged = true; cooldown.ValueChanged += (_, _) => cooldownChanged = true;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 7 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        for (int i = 0; i < 5; i++) layout.RowStyles.Add(new RowStyle(SizeType.Percent, 20)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Add("Full Speed on (°C)", on, 0); Add("Full Speed off (°C)", off, 1); Add("Poll interval (seconds)", poll, 2); Add("Release cooldown (seconds)", cooldown, 3);
        layout.Controls.Add(startup, 0, 4); layout.SetColumnSpan(startup, 2); if (startupError is not null) { var reason = new Label { Text = startupError, AutoSize = true, MaximumSize = new Size(620, 0), ForeColor = Color.DarkRed }; layout.Controls.Add(reason, 0, 5); layout.SetColumnSpan(reason, 2); }
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        var save = new Button { Text = "Save", AutoSize = true }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        save.Click += async (_, _) => { try { Options.Validate(); save.Enabled = false; if (SaveAsync is null || await SaveAsync(Options, StartupEnabled)) { DialogResult = DialogResult.OK; Close(); } } catch (Exception ex) { MessageBox.Show(ex.Message, "Invalid settings", MessageBoxButtons.OK, MessageBoxIcon.Warning); } finally { if (!IsDisposed) save.Enabled = true; } };
        buttons.Controls.Add(cancel); buttons.Controls.Add(save); layout.Controls.Add(buttons, 0, 6); layout.SetColumnSpan(buttons, 2); Controls.Add(layout); AcceptButton = save; CancelButton = cancel;
        void Add(string label, Control field, int row) { layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row); field.Dock = DockStyle.Fill; layout.Controls.Add(field, 1, row); }
    }
    internal void VerifyLayout()
    {
        foreach (NumericUpDown field in new[] { on, off, poll, cooldown })
        {
            if (!field.Visible || field.Width < TextRenderer.MeasureText(field.Text + " 00", field.Font).Width + SystemInformation.VerticalScrollBarWidth)
                throw new InvalidOperationException("A numeric value is clipped or hidden.");
        }
        foreach (var label in Controls.OfType<TableLayoutPanel>().SelectMany(panel => panel.Controls.OfType<Label>()))
            if (!label.Visible || (label.MaximumSize.Width == 0 && label.Width < TextRenderer.MeasureText(label.Text, label.Font).Width - 4))
                throw new InvalidOperationException("A settings label is clipped or hidden.");
    }
    private static NumericUpDown Number(decimal min, decimal max, int decimals) => new() { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = 0.5m, ThousandsSeparator = false };
}
