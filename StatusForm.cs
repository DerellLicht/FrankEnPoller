using System.Text.Json;

namespace FrankEnPoller;

/// <summary>
/// On-disk form of the remembered status-window state (window.json). X and Y are null until
/// the user has moved the window. Files written by the earlier position-only version
/// (just X and Y) still load; AlwaysOnTop then defaults to false.
/// </summary>
public sealed class WindowState
{
    public int? X { get; set; }
    public int? Y { get; set; }
    public bool AlwaysOnTop { get; set; }
}

/// <summary>
/// Small window that pops up above the tray icon on first use, and thereafter reopens
/// wherever the user last moved it. Whether it floats above other windows is a user
/// option (AlwaysOnTop, off by default) that is remembered between runs.
/// </summary>
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
    private bool _exiting;
    private readonly WindowState _state; // contents of window.json: saved position and the always-on-top choice
    private Point? _savedLocation;      // where the user last left the window; null = never moved
    private Point _locationAtMoveStart; // position when a move began, to tell a real move from a bare title-bar click

    public StatusForm()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "FrankEnPoller";
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = SystemColors.Window;
        _state = LoadState();
        TopMost = _state.AlwaysOnTop;
        if (_state.X is int x && _state.Y is int y) {
            _savedLocation = new Point(x, y);
        }

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

        // The window now stays open and can be moved by the user, so a size change (the
        // readings change the text, and AutoSize resizes us) must NOT snap it back to the
        // tray corner. It only nudges the window back on-screen if it grew past the edge.
        SizeChanged += (_, _) => {
            if (Visible) {
                KeepOnScreen();
            }
        };
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

        var (solar, solarSource) = r.GetSolar();
        _solar.Text = solar is double sw ? Fmt.Power(sw) : "—";
        _solarNote.Text = string.Join("\n",
            new[] { solarSource.Length > 0 ? solarSource : null, Fmt.SolarToday(r), Fmt.SolarPeak(r) }.Where(s => s != null));

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

    /// <summary>
    /// Tray-icon left-click. The window no longer auto-hides, so this no longer toggles:
    /// if the window is closed it opens it, and if it is already open it just brings it
    /// to the front. Closing is done only with the X on the title bar.
    /// </summary>
    public void ShowOrActivate()
    {
        ShowNearTray();
    }

    /// <summary>
    /// Shows the window if it is hidden. It opens at the position the user last moved it to,
    /// or near the tray if it has never been moved. If it is already visible it is left
    /// where it is and just activated.
    /// </summary>
    public void ShowNearTray()
    {
        if (!Visible) {
            if (_savedLocation is Point saved) {
                // Set before Show() so it appears in place instead of flashing elsewhere first.
                Location = saved;
                Show();
                // The remembered spot may be off-screen now (monitor unplugged, resolution
                // change), and the size may differ from last time; pull it back into view.
                KeepOnScreen();
            } else {
                Show();
                PlaceNearTray();
            }
        }
        Activate();
    }

    /// <summary>Remembers where a move started, so OnResizeEnd can tell if the window really moved.</summary>
    protected override void OnResizeBegin(EventArgs e)
    {
        base.OnResizeBegin(e);
        _locationAtMoveStart = Location;
    }

    /// <summary>
    /// Fires when the user finishes dragging the window by its title bar (or moving it with
    /// the keyboard). If the position actually changed, it is remembered for next time.
    /// Programmatic placement (tray corner, KeepOnScreen) does not come through here, so
    /// only a deliberate move is ever saved.
    /// </summary>
    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        if (Location != _locationAtMoveStart) {
            _savedLocation = Location;
            _state.X = Left;
            _state.Y = Top;
            SaveState();
        }
    }

    /// <summary>
    /// Whether the window floats above other windows. Setting it takes effect immediately
    /// and is remembered in window.json. When off, the window behaves like a normal window
    /// and can go behind others; a tray-icon click (or "Show status") brings it forward.
    /// </summary>
    public bool AlwaysOnTop
    {
        get => TopMost;
        set {
            TopMost = value;
            _state.AlwaysOnTop = value;
            SaveState();
        }
    }

    /// <summary>Reads window.json. Returns a default state (no position, not on top) if it is missing or unreadable.</summary>
    private static WindowState LoadState()
    {
        try {
            if (File.Exists(AppPaths.WindowFile)) {
                var state = JsonSerializer.Deserialize<WindowState>(File.ReadAllText(AppPaths.WindowFile));
                if (state is not null) {
                    return state;
                }
            }
        } catch (Exception ex) {
            Log.Error("Could not read window.json; using defaults", ex);
        }
        return new WindowState();
    }

    /// <summary>Writes the current state to window.json. A failure to write is logged, never fatal.</summary>
    private void SaveState()
    {
        try {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(AppPaths.WindowFile, JsonSerializer.Serialize(_state));
        } catch (Exception ex) {
            Log.Error("Could not save window.json", ex);
        }
    }

    private void PlaceNearTray()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Right - Width - 8, area.Bottom - Height - 8);
    }

    /// <summary>
    /// Moves the window the minimum distance needed to bring it fully inside the working
    /// area of the screen it is on (e.g. after it grows past the bottom edge). A window
    /// that is already fully on-screen is not touched.
    /// </summary>
    private void KeepOnScreen()
    {
        var area = Screen.FromRectangle(Bounds).WorkingArea;
        var x = Math.Max(area.Left, Math.Min(Left, area.Right - Width));
        var y = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
        if (x != Left || y != Top) {
            Location = new Point(x, y);
        }
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
