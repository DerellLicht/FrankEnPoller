using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrankEnPoller;

/// <summary>
/// The program version, taken from the Version field of FrankEnPoller.csproj (it is compiled
/// into the assembly), so the title bars never need a separate edit when it changes.
/// </summary>
public static class AppInfo
{
    /// <summary>Version as "a.b.c", for example "2.0.1".</summary>
    public static string Version { get; } =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
}

public static class AppPaths
{
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static readonly string Root = Path.Combine(LocalAppData, "FrankEnPoller");

    /// <summary>Data folder used before the app was renamed from Enphase Poller.</summary>
    public static readonly string OldRoot = Path.Combine(LocalAppData, "EnphasePoller");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string TokenCache => Path.Combine(Root, "enphase-token.txt");
    public static string LogFile => Path.Combine(Root, "log.txt");

    /// <summary>Remembered position of the status window (kept apart from settings.json, which holds the encrypted passwords).</summary>
    public static string WindowFile => Path.Combine(Root, "window.json");

    /// <summary>First run after the rename: move the old data folder (settings, logs, CSVs) across.</summary>
    public static void MigrateFromOldName()
    {
        if (Directory.Exists(Root) || !Directory.Exists(OldRoot)) return;
        try
        {
            Directory.Move(OldRoot, Root);
            Log.Info($"Moved data folder from {OldRoot}");
        }
        catch (Exception ex)
        {
            Log.Error($"Could not move {OldRoot} to {Root}", ex);
        }
    }
}

/// <summary>
/// User settings, stored in %LOCALAPPDATA%\FrankEnPoller\settings.json. Passwords are
/// encrypted with Windows DPAPI, so only this Windows user can decrypt them.
/// </summary>
public sealed class Settings
{
    // Part of the DPAPI key for saved passwords. Kept as the app's original name on purpose:
    // changing it would make passwords saved before the rename undecryptable.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EnphasePoller");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public string EnphaseHost { get; set; } = "envoy.local"; // the gateway's own mDNS name; an IP address is more reliable
    public string EnphaseSerial { get; set; } = "";
    public string EnphaseEmail { get; set; } = "";
    public string? EnphasePasswordEncrypted { get; set; }

    public FranklinSource FranklinSource { get; set; } = FranklinSource.Cloud;
    public string FranklinEmail { get; set; } = "";
    public string? FranklinPasswordEncrypted { get; set; }
    public string FranklinGatewayId { get; set; } = "";   // blank = first aGate on the account
    public string FranklinHost { get; set; } = "192.168.0.110";
    public int FranklinPort { get; set; } = 502;

    public double BatteryCapacityKwh { get; set; } = 28.6; // aPower (13.6) + aPower 2 (15.0); not reported by the aGate
    public double BackupFloorPct { get; set; } = 5;        // lowest SoC usable in an outage

    public int PollSeconds { get; set; } = 15;
    public bool CsvEnabled { get; set; } = true;
    public string DataFolder { get; set; } = Path.Combine(AppPaths.Root, "data");

    [JsonIgnore] public bool HasEnphasePassword => !string.IsNullOrEmpty(EnphasePasswordEncrypted);
    [JsonIgnore] public bool HasFranklinPassword => !string.IsNullOrEmpty(FranklinPasswordEncrypted);

    [JsonIgnore]
    public bool EnphaseConfigured =>
        !string.IsNullOrWhiteSpace(EnphaseHost) && !string.IsNullOrWhiteSpace(EnphaseSerial) &&
        !string.IsNullOrWhiteSpace(EnphaseEmail) && HasEnphasePassword;

    [JsonIgnore]
    public bool FranklinCloudConfigured => !string.IsNullOrWhiteSpace(FranklinEmail) && HasFranklinPassword;

    public string GetEnphasePassword() => Unprotect(EnphasePasswordEncrypted);
    public void SetEnphasePassword(string password) => EnphasePasswordEncrypted = Protect(password);
    public string GetFranklinPassword() => Unprotect(FranklinPasswordEncrypted);
    public void SetFranklinPassword(string password) => FranklinPasswordEncrypted = Protect(password);

    private static string Protect(string secret) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));

    private static string Unprotect(string? encrypted) => encrypted is null ? "" :
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(encrypted), Entropy, DataProtectionScope.CurrentUser));

    public Settings Clone() => (Settings)MemberwiseClone();

    public static Settings Load()
    {
        if (File.Exists(AppPaths.SettingsFile))
        {
            try
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new Settings();
                // A data folder inside the pre-rename folder moved along with it.
                if (s.DataFolder.StartsWith(AppPaths.OldRoot, StringComparison.OrdinalIgnoreCase))
                {
                    s.DataFolder = AppPaths.Root + s.DataFolder[AppPaths.OldRoot.Length..];
                    s.Save();
                }
                return s;
            }
            catch (Exception ex)
            {
                Log.Error("Could not read settings.json; using defaults", ex);
            }
        }
        return FromEnvironment();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, JsonOpts));
    }

    /// <summary>First run: carry over the ENPHASE_* environment variables the console version used.</summary>
    private static Settings FromEnvironment()
    {
        static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

        var s = new Settings();
        s.EnphaseHost = Env("ENPHASE_HOST") ?? s.EnphaseHost;
        s.EnphaseSerial = Env("ENPHASE_SERIAL") ?? "";
        s.EnphaseEmail = Env("ENPHASE_EMAIL") ?? "";
        if (Env("ENPHASE_PASSWORD") is { } pw) s.SetEnphasePassword(pw);
        if (int.TryParse(Env("ENPHASE_POLL_SECONDS"), out var poll)) s.PollSeconds = poll;
        if (Env("ENPHASE_CSV") is { } csv && Path.GetDirectoryName(Path.GetFullPath(csv)) is { } dir) s.DataFolder = dir;
        if (s.EnphaseConfigured) s.Save();
        return s;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FranklinSource { Off, Cloud, Modbus }

public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Root);
                var file = new FileInfo(AppPaths.LogFile);
                if (file.Exists && file.Length > 1_000_000)
                    File.Move(file.FullName, file.FullName + ".old", overwrite: true);
                File.AppendAllText(file.FullName, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
    }
}

public static class Fmt
{
    public static string Power(double watts) =>
        Math.Abs(watts) < 1000 ? $"{watts:0} W" : $"{watts / 1000:0.00} kW";

    /// <summary>"45 min", "20 h 50 m", or "3.2 days".</summary>
    public static string Duration(double hours)
    {
        if (hours < 1) return $"{hours * 60:0} min";
        if (hours < 48)
        {
            var whole = (int)hours;
            var minutes = (int)Math.Round((hours - whole) * 60);
            if (minutes == 60) { whole++; minutes = 0; }
            return $"{whole} h {minutes:00} m";
        }
        return $"{hours / 24:0.0} days";
    }

    /// <summary>"12.4 kWh today", or "3.1 kWh since 1:20 PM" when the start of the day wasn't seen.</summary>
    public static string? SolarToday(CombinedReading r) => r.SolarTodayKwh switch
    {
        null => null,
        double kwh when r.SolarTodaySince is { } since => $"{kwh:0.0} kWh since {since.LocalDateTime:t}",
        double kwh => $"{kwh:0.0} kWh today"
    };

    /// <summary>"peak 5.64 kW at 12:47 PM", or "peak since 1:02 PM: …" when the log starts after sunrise.</summary>
    public static string? SolarPeak(CombinedReading r)
    {
        if (r.SolarPeakW is not double w || w < 10 || r.SolarPeakTime is not { } at) return null;
        var value = $"{Power(w)} at {at.LocalDateTime:t}";
        return r.SolarPeakSince is { } since ? $"peak since {since.LocalDateTime:t}: {value}" : $"peak {value}";
    }

    /// <summary>Battery power within this many watts of zero counts as idle.</summary>
    public const double BatteryIdleW = 50;

    public static bool BatteryIdle(double batteryW) => Math.Abs(batteryW) <= BatteryIdleW;

    /// <param name="batteryW">+ discharging, − charging.</param>
    public static string BatteryState(double? batteryW) => batteryW switch
    {
        null => "",
        < -BatteryIdleW => $"charging {Power(-batteryW.Value)}",
        > BatteryIdleW => $"discharging {Power(batteryW.Value)}",
        _ => "idle"
    };
}
