namespace FrankEnPoller;

public sealed class SettingsForm : Form
{
    private static readonly (FranklinSource Value, string Label)[] SourceChoices =
    [
        (FranklinSource.Cloud, "FranklinWH cloud (app login)"),
        (FranklinSource.Modbus, "Local Modbus TCP"),
        (FranklinSource.Off, "Off")
    ];

    private readonly Settings _original;
    private readonly TableLayoutPanel _table = new()
    {
        ColumnCount = 3,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(12),
        Dock = DockStyle.Fill
    };
    private readonly TextBox _enphaseHost, _serial, _email, _password;
    private readonly ComboBox _franklinSource;
    private readonly TextBox _franklinEmail, _franklinPassword, _gatewayId, _franklinHost, _dataFolder;
    private readonly NumericUpDown _franklinPort, _pollSeconds, _capacity, _floor;
    private readonly CheckBox _csvEnabled;
    private int _row;

    public Settings Result { get; private set; }

    public SettingsForm(Settings settings)
    {
        _original = settings;
        Result = settings;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "FrankEnPoller settings";
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        Section("Enphase IQ Gateway");
        _enphaseHost = AddText("Gateway IP address", settings.EnphaseHost);
        _serial = AddText("Gateway serial number", settings.EnphaseSerial);
        _email = AddText("Enlighten email", settings.EnphaseEmail);
        _password = AddPassword("Enlighten password", settings.HasEnphasePassword);

        Section("FranklinWH aGate");
        _franklinSource = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        foreach (var (_, label) in SourceChoices) _franklinSource.Items.Add(label);
        _franklinSource.SelectedIndex = Math.Max(0, Array.FindIndex(SourceChoices, c => c.Value == settings.FranklinSource));
        AddRow("Read the aGate from", _franklinSource);
        _franklinEmail = AddText("FranklinWH app email",
            settings.FranklinEmail.Length > 0 ? settings.FranklinEmail : settings.EnphaseEmail);
        _franklinPassword = AddPassword("FranklinWH app password", settings.HasFranklinPassword);
        _gatewayId = AddText("aGate gateway ID", settings.FranklinGatewayId);
        _gatewayId.PlaceholderText = "automatic (first aGate on the account)";
        _franklinHost = AddText("aGate IP address (Modbus)", settings.FranklinHost);
        _franklinPort = AddNumber("Modbus port", settings.FranklinPort, 1, 65535);
        _franklinSource.SelectedIndexChanged += (_, _) => UpdateFranklinFields();
        UpdateFranklinFields();

        Section("Battery backup estimate");
        _capacity = AddDecimal("Battery capacity (kWh)", settings.BatteryCapacityKwh, 1, 500);
        _floor = AddDecimal("Usable floor in an outage (%)", settings.BackupFloorPct, 0, 90);

        Section("Polling and logging");
        _pollSeconds = AddNumber("Poll interval (seconds)", settings.PollSeconds, 5, 3600);
        _csvEnabled = new CheckBox { Text = "Log readings to CSV (one file per month)", Checked = settings.CsvEnabled, AutoSize = true };
        AddRow("", _csvEnabled);
        _dataFolder = AddText("Data folder", settings.DataFolder);
        var browse = new Button { Text = "Browse…", AutoSize = true, Anchor = AnchorStyles.Left };
        browse.Click += (_, _) => BrowseForFolder();
        _table.Controls.Add(browse, 2, _row - 1);

        var note = new Label
        {
            Text = "The FranklinWH cloud is polled at most every 30 seconds. Passwords are stored encrypted for this Windows user.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(440, 0),
            Margin = new Padding(3, 12, 3, 0)
        };
        _table.Controls.Add(note, 0, _row);
        _table.SetColumnSpan(note, 3);
        _row++;

        var save = new Button { Text = "Save", AutoSize = true };
        save.Click += (_, _) => SaveAndClose();
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 16, 0, 0)
        };
        buttons.Controls.AddRange([cancel, save]);
        _table.Controls.Add(buttons, 0, _row);
        _table.SetColumnSpan(buttons, 3);
        AcceptButton = save;
        CancelButton = cancel;

        Controls.Add(_table);
        ResumeLayout();
    }

    private FranklinSource SelectedSource => SourceChoices[_franklinSource.SelectedIndex].Value;

    private void UpdateFranklinFields()
    {
        var cloud = SelectedSource == FranklinSource.Cloud;
        var modbus = SelectedSource == FranklinSource.Modbus;
        _franklinEmail.Enabled = _franklinPassword.Enabled = _gatewayId.Enabled = cloud;
        _franklinHost.Enabled = _franklinPort.Enabled = modbus;
    }

    private void Section(string title)
    {
        var label = new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(3, _row == 0 ? 0 : 14, 3, 4)
        };
        _table.Controls.Add(label, 0, _row);
        _table.SetColumnSpan(label, 3);
        _row++;
    }

    private void AddRow(string label, Control control)
    {
        if (label.Length > 0)
            _table.Controls.Add(new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 3, 12, 3)
            }, 0, _row);
        control.Anchor = AnchorStyles.Left;
        _table.Controls.Add(control, 1, _row);
        _row++;
    }

    private TextBox AddText(string label, string value)
    {
        var box = new TextBox { Text = value, Width = 260 };
        AddRow(label, box);
        return box;
    }

    private TextBox AddPassword(string label, bool hasSaved)
    {
        var box = AddText(label, "");
        box.UseSystemPasswordChar = true;
        if (hasSaved) box.PlaceholderText = "saved (leave blank to keep)";
        return box;
    }

    private NumericUpDown AddNumber(string label, int value, int min, int max)
    {
        var number = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Width = 90 };
        AddRow(label, number);
        return number;
    }

    private NumericUpDown AddDecimal(string label, double value, decimal min, decimal max)
    {
        var number = new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            DecimalPlaces = 1,
            Increment = 0.1m,
            Value = Math.Clamp((decimal)value, min, max),
            Width = 90
        };
        AddRow(label, number);
        return number;
    }

    private void BrowseForFolder()
    {
        using var dialog = new FolderBrowserDialog { InitialDirectory = _dataFolder.Text, UseDescriptionForTitle = true, Description = "Folder for CSV files" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _dataFolder.Text = dialog.SelectedPath;
    }

    private void SaveAndClose()
    {
        var s = _original.Clone();
        s.EnphaseHost = _enphaseHost.Text.Trim();
        s.EnphaseSerial = _serial.Text.Trim();
        s.EnphaseEmail = _email.Text.Trim();
        if (_password.Text.Length > 0) s.SetEnphasePassword(_password.Text);

        s.FranklinSource = SelectedSource;
        s.FranklinEmail = _franklinEmail.Text.Trim();
        if (_franklinPassword.Text.Length > 0) s.SetFranklinPassword(_franklinPassword.Text);
        s.FranklinGatewayId = _gatewayId.Text.Trim();
        s.FranklinHost = _franklinHost.Text.Trim();
        s.FranklinPort = (int)_franklinPort.Value;

        s.BatteryCapacityKwh = (double)_capacity.Value;
        s.BackupFloorPct = (double)_floor.Value;
        s.PollSeconds = (int)_pollSeconds.Value;
        s.CsvEnabled = _csvEnabled.Checked;
        s.DataFolder = _dataFolder.Text.Trim();

        if (s.FranklinSource == FranklinSource.Cloud && !s.FranklinCloudConfigured)
        {
            MessageBox.Show(this, "Enter your FranklinWH app email and password, or choose another aGate source.", Text);
            return;
        }
        if (s.CsvEnabled && s.DataFolder.Length == 0)
        {
            MessageBox.Show(this, "Choose a data folder, or turn off CSV logging.", Text);
            return;
        }

        Result = s;
        DialogResult = DialogResult.OK;
    }
}
