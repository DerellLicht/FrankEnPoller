using System.Diagnostics;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FrankEnPoller;

/// <summary>
/// The tray icon shows battery SoC while the battery is active, or grid kW while it's idle
/// (or there's no aGate data), on a background coloured by grid flow: green exporting over 100 W, amber importing over 100 W, blue in between.
/// Left-click toggles the status window; right-click opens the menu.
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "FrankEnPoller";
    private const string OldRunValueName = "EnphasePoller"; // before the rename

    // The system is rarely at exactly 0 W, so small trickles either way count as balanced.
    private const double GridDeadbandW = 100;

    private static readonly Color ExportColor = Color.FromArgb(0x1E, 0x8E, 0x3E);
    private static readonly Color ImportColor = Color.FromArgb(0xC2, 0x6A, 0x00);
    private static readonly Color BalancedColor = Color.FromArgb(0x3B, 0x6E, 0xA8);
    private static readonly Color NoDataColor = Color.FromArgb(0x70, 0x70, 0x70);

    // Order matters: creating the form installs the WinForms SynchronizationContext,
    // which the poller captures to raise its events on the UI thread.
    private readonly StatusForm _status = new();
    private readonly Poller _poller = new();
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _startupItem;
    private Settings _settings;
    private IntPtr _iconHandle;
    private bool _settingsOpen;

    public TrayApp()
    {
        _settings = Settings.Load();
        MigrateStartupEntry();

        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup())
        {
            Checked = IsStartupEnabled()
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show status", null, (_, _) => _status.ShowNearTray());
        menu.Items.Add("Settings…", null, async (_, _) => await OpenSettingsAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open data folder", null, (_, _) => OpenFolder(_settings.DataFolder));
        menu.Items.Add("Open log", null, (_, _) => OpenFile(AppPaths.LogFile));
        menu.Items.Add("aGate diagnostics…", null, async (_, _) => await DiagnoseAgateAsync());
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) => await ExitAsync());

        _tray = new NotifyIcon { ContextMenuStrip = menu, Text = "FrankEnPoller: starting…", Visible = true };
        SetIcon("…", NoDataColor);
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _status.Toggle(); };

        _poller.ReadingAvailable += OnReading;
        _poller.Start(_settings);

        if (!_settings.EnphaseConfigured ||
            (_settings.FranklinSource == FranklinSource.Cloud && !_settings.FranklinCloudConfigured))
            SynchronizationContext.Current?.Post(async _ => await OpenSettingsAsync(), null);
    }

    private void OnReading(CombinedReading r)
    {
        _status.UpdateReading(r);
        _tray.Text = Tooltip(r);

        var grid = r.GetGrid().Watts;
        var color = grid switch
        {
            null => NoDataColor,
            < -GridDeadbandW => ExportColor,
            > GridDeadbandW => ImportColor,
            _ => BalancedColor
        };
        SetIcon(IconText(r), color);
    }

    /// <summary>
    /// Battery SoC while the battery is charging or discharging. When it's idle (full, or
    /// held at its reserve), SoC says little, so show grid power in kW instead. The kW
    /// value always has a decimal point below 10 kW, which tells it apart from SoC at a glance.
    /// </summary>
    private static string IconText(CombinedReading r)
    {
        var soc = r.Franklin?.SocPct;
        var batteryW = r.Franklin?.BatteryW;
        var gridW = r.GetGrid().Watts;

        // The aGate can sit at ~99.9% for hours, trickle-charging a few dozen watts. A battery
        // that displays as 100% and isn't discharging counts as full, so treat it like idle.
        var showSoc = soc is not null &&
                      (gridW is null || batteryW is not double b ||
                       !(Fmt.BatteryIdle(b) || IsFull(soc.Value, b)));
        if (showSoc)
            return Math.Round(soc!.Value).ToString("0");
        if (gridW is double g)
        {
            var kw = Math.Abs(g) / 1000;
            return kw < 9.95 ? kw.ToString("0.0") : kw.ToString("0");
        }
        return "?";
    }

    /// <summary>SoC rounds to 100% and the battery is charging or idle, not discharging.</summary>
    private static bool IsFull(double socPct, double batteryW) =>
        Math.Round(socPct) >= 100 && batteryW <= Fmt.BatteryIdleW;

    private static string Tooltip(CombinedReading r)
    {
        var lines = new List<string>();
        if (r.Enphase is { } e)
            lines.Add(Fmt.SolarToday(r) is { } solarToday
                ? $"Solar {Fmt.Power(e.SolarW)}, {solarToday}"
                : $"Solar {Fmt.Power(e.SolarW)}");
        else
            lines.Add("Enphase: no data");

        if (r.GetHomeLoad().Watts is double home)
            lines.Add(r.Franklin?.HomeTodayKwh is double today
                ? $"Home {Fmt.Power(home)}, {today:0.0} kWh today"
                : $"Home {Fmt.Power(home)}");

        if (r.GetGrid().Watts is double grid)
            lines.Add($"Grid {(grid < 0 ? "exporting" : "importing")} {Fmt.Power(Math.Abs(grid))}");

        if (r.Franklin is { } f)
            lines.Add($"Battery {f.SocPct?.ToString("0") ?? "?"}% {Fmt.BatteryState(f.BatteryW)}".TrimEnd());
        else
            lines.Add("aGate: no data");

        var text = string.Join("\n", lines);
        return text.Length <= 127 ? text : text[..127]; // NotifyIcon.Text limit
    }

    private void SetIcon(string text, Color background)
    {
        var size = SystemInformation.SmallIconSize;
        using var bitmap = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var brush = new SolidBrush(background);
            g.FillRectangle(brush, 0, 0, size.Width, size.Height);
            var fontPx = size.Height * (text.Length >= 3 ? 0.52f : 0.7f);
            using var font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, Brushes.White, new RectangleF(-3, 0, size.Width + 6, size.Height + 1), format);
        }

        var handle = bitmap.GetHicon();
        var oldIcon = _tray.Icon;
        _tray.Icon = Icon.FromHandle(handle);
        oldIcon?.Dispose();
        if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
        _iconHandle = handle;
    }

    private async Task OpenSettingsAsync()
    {
        if (_settingsOpen) return;
        _settingsOpen = true;
        try
        {
            using var dialog = new SettingsForm(_settings);
            if (dialog.ShowDialog() != DialogResult.OK) return;
            _settings = dialog.Result;
            _settings.Save();
            await _poller.RestartAsync(_settings);
        }
        catch (Exception ex)
        {
            Log.Error("Settings failed", ex);
            MessageBox.Show(ex.Message, "FrankEnPoller", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _settingsOpen = false;
        }
    }

    private async Task DiagnoseAgateAsync()
    {
        using var source = FranklinSources.Create(_settings, out var reason);
        if (source is null)
        {
            MessageBox.Show($"No aGate source to check: {reason}.", "FrankEnPoller");
            return;
        }
        try
        {
            _tray.ShowBalloonTip(3000, "FrankEnPoller", $"Checking aGate via {source.Description}…", ToolTipIcon.Info);
            var report = await Task.Run(() => source.DiagnoseAsync(CancellationToken.None));
            Directory.CreateDirectory(AppPaths.Root);
            var path = Path.Combine(AppPaths.Root, $"agate-check-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            await File.WriteAllTextAsync(path, report);
            OpenFile(path);
        }
        catch (Exception ex)
        {
            Log.Error("aGate diagnostics failed", ex);
            MessageBox.Show($"aGate check failed: {ex.Message}", "FrankEnPoller", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is string;
    }

    /// <summary>If the old Enphase Poller was set to start with Windows, point that at this exe instead.</summary>
    private static void MigrateStartupEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(OldRunValueName) is not string) return;
            key.DeleteValue(OldRunValueName, throwOnMissingValue: false);
            key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
            Log.Info("Moved the Start with Windows entry from Enphase Poller to FrankEnPoller");
        }
        catch (Exception ex)
        {
            Log.Error("Could not migrate the Start with Windows entry", ex);
        }
    }

    private void ToggleStartup()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (IsStartupEnabled())
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        else
            key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
        _startupItem.Checked = IsStartupEnabled();
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private static void OpenFile(string path)
    {
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private async Task ExitAsync()
    {
        _tray.Visible = false;
        await _poller.StopAsync();
        _status.ForceClose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _poller.Dispose();
            _status.Dispose();
        }
        if (_iconHandle != IntPtr.Zero)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
