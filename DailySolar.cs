using System.Globalization;
using System.Text.Json;

namespace FrankEnPoller;

/// <summary>Where the lifetime solar counter stood at the start of a day.</summary>
/// <param name="Since">Null if the baseline was taken while the panels were idle (before sunrise),
/// so it covers the whole day; otherwise the time it was taken, and today's total starts there.</param>
public sealed record SolarBaseline(DateOnly Date, double LifetimeWh, DateTimeOffset? Since);

/// <summary>Today's solar so far, from the Enphase meter.</summary>
/// <param name="Since">Set when <paramref name="Kwh"/> covers only part of today.</param>
/// <param name="PeakSince">Set when the peak covers only part of today (log starts after sunrise).</param>
public sealed record SolarDay(
    double Kwh, DateTimeOffset? Since,
    double PeakW, DateTimeOffset PeakTime, DateTimeOffset? PeakSince);

/// <summary>
/// Today's solar energy = the Enphase production meter's lifetime counter minus its value at
/// the start of the day. The gateway's own daily figures can't be used on this firmware:
/// production.json's whToday repeats the lifetime total and /api/v1/production reports 0.
///
/// The start-of-day value comes from, in order: the saved baseline for today, today's first
/// row in the CSV log, or (failing both) the current reading. If that baseline was taken
/// after sunrise, the Poller prefers the aGate's own daily solar total when it has one.
///
/// Also tracks today's peak solar power: seeded from today's CSV rows, then updated live.
/// </summary>
public sealed class DailySolar(string dataFolder)
{
    private const double NightW = 10; // below this the panels are idle, so nothing is missed before it
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string StatePath => Path.Combine(AppPaths.Root, "solar-baseline.json");

    private DateOnly _day;
    private SolarBaseline? _baseline;
    private double _peakW;
    private DateTimeOffset _peakTime;
    private DateTimeOffset? _peakSince;

    private sealed record CsvDay(DateTimeOffset FirstTime, double FirstLifetimeWh, double FirstSolarW,
        double PeakW, DateTimeOffset PeakTime);

    public SolarDay Update(EnphaseReading e, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        if (_baseline is null || _day != today)
            StartDay(today, e, now);

        if (e.SolarW > _peakW)
        {
            _peakW = e.SolarW;
            _peakTime = now;
        }

        return new SolarDay(
            Math.Max(0, e.SolarLifetimeWh - _baseline!.LifetimeWh) / 1000, _baseline.Since,
            _peakW, _peakTime, _peakSince);
    }

    private void StartDay(DateOnly today, EnphaseReading e, DateTimeOffset now)
    {
        _day = today;
        var csv = ScanCsv(today);

        _baseline = LoadSaved(today)
            ?? (csv is null ? null : new SolarBaseline(today, csv.FirstLifetimeWh, csv.FirstSolarW < NightW ? null : csv.FirstTime))
            ?? new SolarBaseline(today, e.SolarLifetimeWh, e.SolarW < NightW ? null : now);
        Save(_baseline);

        if (csv is null)
        {
            _peakW = 0;
            _peakTime = now;
            _peakSince = e.SolarW < NightW ? null : now;
        }
        else
        {
            _peakW = csv.PeakW;
            _peakTime = csv.PeakTime;
            _peakSince = csv.FirstSolarW < NightW ? null : csv.FirstTime;
        }

        Log.Info($"Solar day {today}: baseline {_baseline.LifetimeWh:F0} Wh" +
                 (_baseline.Since is { } since ? $" (partial since {since.LocalDateTime:t})" : "") +
                 $", peak so far {_peakW:F0} W" +
                 (_peakSince is { } ps ? $" (log since {ps.LocalDateTime:t})" : ""));
    }

    private static SolarBaseline? LoadSaved(DateOnly today)
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            var saved = JsonSerializer.Deserialize<SolarBaseline>(File.ReadAllText(StatePath));
            return saved?.Date == today ? saved : null;
        }
        catch
        {
            return null;
        }
    }

    private static void Save(SolarBaseline baseline)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(baseline));
        }
        catch (Exception ex)
        {
            Log.Error("Could not save solar baseline", ex);
        }
    }

    /// <summary>Today's earliest row and highest solar reading across this month's CSV files.</summary>
    private CsvDay? ScanCsv(DateOnly today)
    {
        try
        {
            if (!Directory.Exists(dataFolder)) return null;
            DateTimeOffset? firstTime = null, peakTime = null;
            double firstLifetime = 0, firstSolar = double.NaN, peakW = 0;

            foreach (var file in Directory.EnumerateFiles(dataFolder, $"readings-{today:yyyy-MM}*.csv"))
            {
                using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                var header = reader.ReadLine()?.Split(',');
                if (header is null) continue;
                int timeCol = Array.IndexOf(header, "timestamp"),
                    lifetimeCol = Array.IndexOf(header, "solar_lifetime_wh"),
                    solarCol = Array.IndexOf(header, "solar_w");
                if (timeCol < 0 || lifetimeCol < 0 || solarCol < 0) continue;

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    var f = line.Split(',');
                    if (f.Length <= Math.Max(Math.Max(timeCol, lifetimeCol), solarCol)) continue;
                    if (!DateTimeOffset.TryParse(f[timeCol], Inv, DateTimeStyles.None, out var time) ||
                        DateOnly.FromDateTime(time.LocalDateTime) != today) continue;
                    if (!double.TryParse(f[lifetimeCol], NumberStyles.Float, Inv, out var lifetimeWh) || lifetimeWh <= 0) continue;
                    if (!double.TryParse(f[solarCol], NumberStyles.Float, Inv, out var solarW)) continue;

                    if (firstTime is null || time < firstTime)
                    {
                        firstTime = time;
                        firstLifetime = lifetimeWh;
                        firstSolar = solarW;
                    }
                    if (peakTime is null || solarW > peakW)
                    {
                        peakW = solarW;
                        peakTime = time;
                    }
                }
            }
            return firstTime is null ? null : new CsvDay(firstTime.Value, firstLifetime, firstSolar, peakW, peakTime!.Value);
        }
        catch (Exception ex)
        {
            Log.Error("Could not read CSV history for today's solar", ex);
            return null;
        }
    }
}
