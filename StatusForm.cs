namespace FrankEnPoller;

/// <summary>Small always-on-top window that pops up above the tray icon.</summary>
public sealed class StatusForm : Form
{
    private static readonly Color ExportColor = Color.FromArgb(0x1E, 0x8E, 0x3E);
    private static readonly Color ImportColor = Color.FromArgb(0xC2, 0x6A, 0x00);

    private readonly TableLayoutPanel _table = new()
    {
        ColumnCount = 2,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(14, 10, 14, 12)
    };
    private readonly Label _solar, _solarNote, _home, _homeNote, _grid, _gridNote, _battery, _batteryNote,
        _backup, _backupNote, _footer;
    private int _row;
    private DateTime _hiddenAt;
    private bool _exiting;

    public StatusForm()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "FrankEnPoller";
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = SystemColors.Window;

        (_solar, _solarNote) = AddItem("Solar");
        (_home, _homeNote) = AddItem("Home");
        (_grid, _gridNote) = AddItem("Grid");
        (_battery, _batteryNote) = AddItem("Battery");
        (_backup, _backupNote) = AddItem("Backup");

        _footer = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(320, 0),
            Margin = new Padding(3, 10, 3, 0),
            Text = "Waiting for the first reading…"
        };
        _table.Controls.Add(_footer, 0, _row);
        _table.SetColumnSpan(_footer, 2);

        Controls.Add(_table);
        ResumeLayout();

        SizeChanged += (_, _) => { if (Visible) PlaceNearTray(); };
    }

    private (Label Value, Label Note) AddItem(string name)
    {
        var nameLabel = new Label
        {
            Text = name,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 6, 18, 0)
        };
        var value = new Label
        {
            Text = "—",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 15f),
            Margin = new Padding(3, 4, 3, 0)
        };
        var note = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 4) };

        _table.Controls.Add(nameLabel, 0, _row);
        _table.Controls.Add(value, 1, _row);
        _table.Controls.Add(note, 1, _row + 1);
        _row += 2;
        return (value, note);
    }

    public void UpdateReading(CombinedReading r)
    {
        var e = r.Enphase;
        var f = r.Franklin;

        _solar.Text = e is null ? "—" : Fmt.Power(e.SolarW);
        _solarNote.Text = string.Join("\n", new[] { Fmt.SolarToday(r), Fmt.SolarPeak(r) }.Where(s => s != null));

        var (home, homeSource) = r.GetHomeLoad();
        _home.Text = home is double h ? Fmt.Power(h) : "—";
        _homeNote.Text = f?.HomeTodayKwh is double today ? $"{homeSource} · {today:0.0} kWh today" : homeSource;

        var (grid, gridSource) = r.GetGrid();
        if (grid is not double g)
        {
            _grid.Text = "—";
            _grid.ForeColor = SystemColors.ControlText;
            _gridNote.Text = "";
        }
        else
        {
            _grid.Text = Fmt.Power(Math.Abs(g));
            _grid.ForeColor = g < 0 ? ExportColor : ImportColor;
            var direction = g < 0 ? "exporting" : "importing";
            _gridNote.Text = gridSource.Length > 0 ? $"{direction} · {gridSource}" : direction;
        }

        _battery.Text = f?.SocPct is double soc ? $"{soc:0}%" : "—";
        _batteryNote.Text = f is null ? "no data from aGate" : Fmt.BatteryState(f.BatteryW);

        UpdateBackup(r.Backup);

        // With inverter-only data, the timestamp is the inverters' last report, minutes apart.
        var enphaseStatus = r.EnphaseError ?? (e!.SolarFromInverters
            ? $"inverters reported {e.Timestamp.LocalDateTime:T} (no CT meters)"
            : $"updated {e.Timestamp.LocalDateTime:T}");
        var franklinStatus = r.FranklinError ?? $"updated {r.FranklinUpdated?.LocalDateTime:T}";
        _footer.Text = $"Enphase: {enphaseStatus}\naGate: {franklinStatus}";
    }

    private void UpdateBackup(BackupEstimate? b)
    {
        if (b is null)
        {
            _backup.Text = "—";
            _backupNote.Text = "needs battery data from the aGate";
            return;
        }

        _backup.Text = b.HoursAtCurrent is double now ? Fmt.Duration(now) : "—";

        var lines = new List<string>();
        if (b.CurrentLoadW is double current)
            lines.Add($"at current load ({Fmt.Power(current)})");
        if (b.HoursAtAverage is double avgHours && b.AverageLoadW is double avgW)
        {
            var covered = b.AverageCoverage;
            var basis = covered >= LoadHistory.Window - TimeSpan.FromMinutes(30) ? "24 h avg"
                : covered.TotalHours >= 1 ? $"avg of {covered.TotalHours:0.#} h logged"
                : $"avg of {covered.TotalMinutes:0} min logged";
            lines.Add($"{Fmt.Duration(avgHours)} at {basis} ({Fmt.Power(avgW)})");
        }
        if (b.HoursAtPeak is double peakHours && b.PeakLoadW is double peakW && b.PeakTime is { } peakTime)
        {
            var when = peakTime.LocalDateTime.Date == DateTime.Today
                ? $"{peakTime.LocalDateTime:t}"
                : $"yesterday {peakTime.LocalDateTime:t}";
            lines.Add($"{Fmt.Duration(peakHours)} at peak 5-min load ({Fmt.Power(peakW)}, {when})");
        }
        lines.Add($"{b.UsableKwh:0.0} kWh usable above {b.FloorPct:0.#}%");
        _backupNote.Text = string.Join("\n", lines);
    }

    public void Toggle()
    {
        if (Visible)
            Hide();
        else if ((DateTime.UtcNow - _hiddenAt).TotalMilliseconds > 300) // the click that deactivated us
            ShowNearTray();
    }

    public void ShowNearTray()
    {
        if (!Visible) Show();
        PlaceNearTray();
        Activate();
    }

    private void PlaceNearTray()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Right - Width - 8, area.Bottom - Height - 8);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Hide();
        _hiddenAt = DateTime.UtcNow;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }

    public void ForceClose()
    {
        _exiting = true;
        Close();
    }
}
