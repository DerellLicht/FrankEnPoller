using System.Globalization;

namespace FrankEnPoller;

/// <summary>Both sources at one poll. Each side has either a reading or an error.</summary>
public sealed record CombinedReading(
    DateTimeOffset Timestamp,
    EnphaseReading? Enphase,
    string? EnphaseError,
    FranklinReading? Franklin,
    string? FranklinError,
    DateTimeOffset? FranklinUpdated,
    double? SolarTodayKwh = null,
    DateTimeOffset? SolarTodaySince = null, // set when the total covers only part of today
    BackupEstimate? Backup = null)
{
    /// <summary>Today's highest Enphase solar reading.</summary>
    public double? SolarPeakW { get; init; }
    public DateTimeOffset? SolarPeakTime { get; init; }

    /// <summary>Set when the peak only covers part of today (log starts after sunrise).</summary>
    public DateTimeOffset? SolarPeakSince { get; init; }

    /// <summary>
    /// What the house itself is using. Prefers the aGate's own measurement; otherwise
    /// corrects the Enphase consumption figure for battery charge/discharge.
    /// </summary>
    public (double? Watts, string Source) GetHomeLoad()
    {
        if (Franklin?.HomeLoadW is double measured)
            return (measured, "measured by aGate");
        if (Enphase?.ConsumptionW is double load && Franklin?.BatteryW is double battery)
            return (load + battery, "Enphase load + battery");
        if (Enphase?.ConsumptionW is double load2)
            return (load2, "Enphase load (includes battery charging)");
        return (null, "");
    }

    /// <summary>
    /// Grid power, + import / - export. Prefers the Enphase consumption CTs; gateways
    /// without them fall back to the aGate's grid reading.
    /// </summary>
    public (double? Watts, string Source) GetGrid()
    {
        if (Enphase?.GridW is double enphase)
            return (enphase, "");
        if (Franklin?.GridW is double agate)
            return (agate, "measured by aGate");
        return (null, "");
    }
}

public static class FranklinSources
{
    /// <summary>Builds the configured aGate source, or returns null with the reason there isn't one.</summary>
    public static IFranklinSource? Create(Settings s, out string? reason)
    {
        reason = null;
        switch (s.FranklinSource)
        {
            case FranklinSource.Cloud when s.FranklinCloudConfigured:
                return new FranklinCloudClient(s.FranklinEmail.Trim(), s.GetFranklinPassword(), s.FranklinGatewayId);
            case FranklinSource.Cloud:
                reason = "FranklinWH login not set: open Settings";
                return null;
            case FranklinSource.Modbus when !string.IsNullOrWhiteSpace(s.FranklinHost):
                return new FranklinModbusClient(s.FranklinHost.Trim(), s.FranklinPort);
            case FranklinSource.Modbus:
                reason = "aGate IP address not set: open Settings";
                return null;
            default:
                reason = "Off in Settings";
                return null;
        }
    }
}

/// <summary>
/// Polls the Enphase gateway and the aGate in parallel on a background task, appends to
/// CSV, and raises <see cref="ReadingAvailable"/> on the UI thread.
/// </summary>
public sealed class Poller : IDisposable
{
    private static readonly TimeSpan FranklinRetryDelay = TimeSpan.FromSeconds(60);

    private readonly SynchronizationContext _ui = SynchronizationContext.Current ?? new SynchronizationContext();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event Action<CombinedReading>? ReadingAvailable;

    public void Start(Settings settings)
    {
        if (_loop != null) throw new InvalidOperationException("Poller is already running.");
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var snapshot = settings.Clone();
        _loop = Task.Run(() => RunAsync(snapshot, token));
    }

    public async Task StopAsync()
    {
        if (_loop == null) return;
        _cts!.Cancel();
        try { await _loop; }
        catch (Exception ex) { Log.Error("Poller stopped with an error", ex); }
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    public async Task RestartAsync(Settings settings)
    {
        await StopAsync();
        Start(settings);
    }

    private async Task RunAsync(Settings s, CancellationToken ct)
    {
        using var enphase = s.EnphaseConfigured
            ? new EnphaseClient(s.EnphaseHost.Trim(), s.EnphaseSerial.Trim(), s.EnphaseEmail.Trim(), s.GetEnphasePassword(), AppPaths.TokenCache)
            : null;
        using var franklin = FranklinSources.Create(s, out var franklinUnavailable);
        var csv = s.CsvEnabled ? new CsvLog(s.DataFolder) : null;
        var dailySolar = new DailySolar(s.DataFolder);
        var interval = TimeSpan.FromSeconds(Math.Max(5, s.PollSeconds));
        var loadHistory = new LoadHistory(s.DataFolder, interval);
        loadHistory.LoadFromCsv(DateTimeOffset.Now);
        var franklinInterval = franklin is null || franklin.MinInterval < interval ? interval : franklin.MinInterval;

        Log.Info($"Polling every {interval.TotalSeconds:F0} s: Enphase {(enphase is null ? "not configured" : s.EnphaseHost)}, " +
                 $"aGate {franklin?.Description ?? franklinUnavailable} every {franklinInterval.TotalSeconds:F0} s");

        string? lastEnphaseError = null;
        (FranklinReading? Reading, string? Error) franklinResult = (null, franklinUnavailable ?? "Waiting for the first reading");
        DateTimeOffset? franklinUpdated = null;
        var nextFranklinPoll = DateTime.MinValue;

        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;

            var enphaseTask = enphase is null
                ? Task.FromResult<(EnphaseReading?, string?)>((null, "Not configured: open Settings from the tray menu"))
                : Attempt(() => enphase.GetReadingAsync(ct));

            // The aGate is polled on its own schedule: no faster than its source allows (the cloud
            // is slower than the LAN), and once a minute while it's failing so timeouts don't
            // hold up the Enphase readings. In between, the last result is reused.
            var pollFranklin = franklin != null && started >= nextFranklinPoll;
            var franklinTask = pollFranklin
                ? Attempt(() => franklin!.GetReadingAsync(ct))
                : Task.FromResult<(FranklinReading?, string?)>(franklinResult);

            await Task.WhenAll(enphaseTask, franklinTask);
            if (ct.IsCancellationRequested) break;

            var (e, enphaseError) = enphaseTask.Result;
            lastEnphaseError = LogIfChanged("Enphase", enphaseError, lastEnphaseError);

            if (pollFranklin)
            {
                var previousError = franklinResult.Error;
                franklinResult = franklinTask.Result;
                LogIfChanged("aGate", franklinResult.Error, previousError);
                if (franklinResult.Reading != null) franklinUpdated = DateTimeOffset.Now;
                nextFranklinPoll = started + (franklinResult.Error is null ? franklinInterval : FranklinRetryDelay);
            }

            var now = DateTimeOffset.Now;
            // Today's solar, best source first:
            //  1. Enphase lifetime counter minus a pre-dawn baseline (exact, independent of the cloud)
            //  2. the aGate's kwh_sun, when the last aGate poll succeeded
            //  3. Enphase counter minus a mid-day baseline, shown as "since <time>"
            var solarDay = e is null ? null : dailySolar.Update(e, now);
            (double Kwh, DateTimeOffset? Since)? solarToday = solarDay is null ? null : (solarDay.Kwh, solarDay.Since);
            if (solarToday is not { Since: null } && franklinResult.Reading?.SolarTodayKwh is double agateSolarKwh)
                solarToday = (agateSolarKwh, null);
            var reading = new CombinedReading(now, e, enphaseError,
                franklinResult.Reading, franklinResult.Error, franklinUpdated,
                solarToday?.Kwh, solarToday?.Since)
            {
                SolarPeakW = solarDay?.PeakW,
                SolarPeakTime = solarDay?.PeakTime,
                SolarPeakSince = solarDay?.PeakSince
            };

            var homeW = reading.GetHomeLoad().Watts;
            // Only readings that account for the battery go into the history; Enphase
            // consumption alone misses whatever the batteries supply.
            if (homeW is double w && reading.Franklin is not null) loadHistory.Add(now, w);
            reading = reading with
            {
                Backup = BackupEstimate.Create(reading.Franklin?.SocPct, homeW, loadHistory.GetStats(now),
                    s.BatteryCapacityKwh, s.BackupFloorPct)
            };
            if (csv != null && (e != null || reading.Franklin != null))
            {
                try { csv.Append(reading); }
                catch (Exception ex) { Log.Error("CSV write failed", ex); }
            }
            _ui.Post(_ => ReadingAvailable?.Invoke(reading), null);

            var wait = interval - (DateTime.UtcNow - started);
            if (wait <= TimeSpan.Zero) continue;
            try { await Task.Delay(wait, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static async Task<(T?, string?)> Attempt<T>(Func<Task<T>> read) where T : class
    {
        try { return (await read(), null); }
        catch (Exception ex) { return (null, ex.Message); }
    }

    private static string? LogIfChanged(string source, string? error, string? previous)
    {
        if (error != previous)
            Log.Info(error is null ? $"{source}: reading OK" : $"{source}: {error}");
        return error;
    }

    public void Dispose() => _cts?.Cancel();
}

/// <summary>Appends readings to one CSV file per month, e.g. readings-2026-11.csv.</summary>
public sealed class CsvLog(string folder)
{
    public const string Header =
        "timestamp,solar_w,enphase_load_w,grid_w,home_load_w,battery_w,battery_soc_pct,agate_grid_w,agate_updated," +
        "solar_lifetime_wh,consumption_lifetime_wh,grid_net_lifetime_wh,home_today_kwh";

    public void Append(CombinedReading r)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"readings-{r.Timestamp:yyyy-MM}.csv");
        var e = r.Enphase;
        var f = r.Franklin;
        var line = string.Join(',',
            r.Timestamp.ToString("o", CultureInfo.InvariantCulture),
            N(e?.SolarW), N(e?.ConsumptionW), N(e?.GridW), N(r.GetHomeLoad().Watts),
            N(f?.BatteryW), N(f?.SocPct), N(f?.GridW),
            r.FranklinUpdated?.ToString("o", CultureInfo.InvariantCulture) ?? "",
            N(e?.SolarLifetimeWh, "F0"), N(e?.ConsumptionLifetimeWh, "F0"), N(e?.GridNetLifetimeWh, "F0"),
            N(f?.HomeTodayKwh, "F3"));

        // A new column layout starts a fresh file (-v2, -v3, ...) rather than mixing layouts in one.
        for (var version = 2; File.Exists(path) && File.ReadLines(path).FirstOrDefault() != Header; version++)
            path = Path.Combine(folder, $"readings-{r.Timestamp:yyyy-MM}-v{version}.csv");
        if (!File.Exists(path))
            File.WriteAllText(path, Header + Environment.NewLine);
        File.AppendAllText(path, line + Environment.NewLine);
    }

    private static string N(double? value, string format = "F1") =>
        value?.ToString(format, CultureInfo.InvariantCulture) ?? "";
}
