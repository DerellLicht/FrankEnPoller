using System.Globalization;

namespace FrankEnPoller;

/// <summary>How long the batteries could carry the house in an outage, ignoring solar.</summary>
public sealed record BackupEstimate(
    double UsableKwh,          // energy above the usable floor
    double FloorPct,
    double? CurrentLoadW,
    double? HoursAtCurrent,
    double? AverageLoadW,
    TimeSpan AverageCoverage,  // how much logged time the average is built from (up to 24 h)
    double? HoursAtAverage,
    double? PeakLoadW,         // highest single reading in the last 24 h
    DateTimeOffset? PeakTime,
    double? HoursAtPeak)
{
    private const double MinLoadW = 20; // below this the estimate is meaningless

    public static BackupEstimate? Create(double? socPct, double? currentLoadW,
        LoadStats? history, double capacityKwh, double floorPct)
    {
        if (socPct is not double soc) return null;
        var usableKwh = capacityKwh * Math.Max(0, soc - floorPct) / 100;
        return new BackupEstimate(
            usableKwh, floorPct,
            currentLoadW, Hours(usableKwh, currentLoadW),
            history?.AverageW, history?.Coverage ?? TimeSpan.Zero, Hours(usableKwh, history?.AverageW),
            history?.PeakW, history?.PeakTime, Hours(usableKwh, history?.PeakW));
    }

    private static double? Hours(double usableKwh, double? loadW) =>
        loadW is double w && w >= MinLoadW ? usableKwh / (w / 1000) : null;
}

/// <param name="PeakW">Highest 5-minute median load; null until there's a full enough 5-minute window.</param>
/// <param name="PeakTime">End of the 5-minute window the peak came from.</param>
/// <param name="Coverage">How much logged time the stats are built from (gaps excluded).</param>
public sealed record LoadStats(double AverageW, double? PeakW, DateTimeOffset? PeakTime, TimeSpan Coverage);

/// <summary>
/// Rolling 24-hour history of home load, for the average-load backup estimate. Seeded from
/// the CSV log at startup so the average doesn't start from scratch after a restart.
/// </summary>
public sealed class LoadHistory(string dataFolder, TimeSpan sampleInterval)
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    // Peak = highest median over any 5-minute window. A median ignores one-off glitches (e.g.
    // the aGate briefly counting a battery discharge as home load) but keeps loads that
    // really run for minutes, which are what drain a battery.
    private static readonly TimeSpan PeakWindow = TimeSpan.FromMinutes(5);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly Queue<(DateTimeOffset Time, double Watts)> _samples = new();
    private DateTimeOffset _last = DateTimeOffset.MinValue;
    private double _sum;

    public void Add(DateTimeOffset time, double watts)
    {
        if (time <= _last) return;
        _samples.Enqueue((time, watts));
        _sum += watts;
        _last = time;
    }

    /// <summary>
    /// Mean and peak home load over the last 24 h, and how much logged time they're based on.
    /// Gaps (app not running) don't count as coverage, so a day with the app off overnight
    /// reads as e.g. 6.6 h.
    /// </summary>
    public LoadStats? GetStats(DateTimeOffset now)
    {
        while (_samples.Count > 0 && now - _samples.Peek().Time > Window)
            _sum -= _samples.Dequeue().Watts;
        if (_samples.Count == 0) return null;

        var (peakW, peakTime) = PeakMedian();
        var span = now - _samples.Peek().Time;
        var logged = sampleInterval * _samples.Count;
        return new LoadStats(_sum / _samples.Count, peakW, peakTime, logged < span ? logged : span);
    }

    /// <summary>Highest median over sliding 5-minute windows (~5,800 samples a day; cheap per poll).</summary>
    private (double?, DateTimeOffset?) PeakMedian()
    {
        var samples = _samples.ToArray();
        // A window needs at least half its expected readings, so a lone reading next to a gap
        // (app just started) can't count as a 5-minute median on its own.
        var minCount = Math.Max(3, (int)(PeakWindow / sampleInterval / 2));
        var buffer = new List<double>();
        double? best = null;
        DateTimeOffset? bestTime = null;

        for (int start = 0, end = 0; end < samples.Length; end++)
        {
            while (samples[end].Time - samples[start].Time >= PeakWindow) start++;
            var count = end - start + 1;
            if (count < minCount) continue;

            buffer.Clear();
            for (var i = start; i <= end; i++) buffer.Add(samples[i].Watts);
            buffer.Sort();
            var median = count % 2 == 1 ? buffer[count / 2] : (buffer[count / 2 - 1] + buffer[count / 2]) / 2;
            if (best is null || median > best)
            {
                best = median;
                bestTime = samples[end].Time;
            }
        }
        return (best, bestTime);
    }

    public void LoadFromCsv(DateTimeOffset now)
    {
        var cutoff = now - Window;
        var rows = new List<(DateTimeOffset, double)>();
        try
        {
            if (!Directory.Exists(dataFolder)) return;
            var months = new[] { cutoff, now }.Select(t => t.ToString("yyyy-MM", Inv)).Distinct();
            foreach (var month in months)
            foreach (var file in Directory.EnumerateFiles(dataFolder, $"readings-{month}*.csv"))
            {
                using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                var header = reader.ReadLine()?.Split(',');
                if (header is null) continue;
                int timeCol = Array.IndexOf(header, "timestamp"),
                    loadCol = Array.IndexOf(header, "home_load_w"),
                    batteryCol = Array.IndexOf(header, "battery_w");
                if (timeCol < 0 || loadCol < 0 || batteryCol < 0) continue;

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    var f = line.Split(',');
                    if (f.Length <= Math.Max(Math.Max(timeCol, loadCol), batteryCol)) continue;
                    // Without aGate data, home_load_w is only Enphase consumption, which misses
                    // whatever the batteries supply (e.g. 498 W vs ~1.5 kW real on 2026-11-18).
                    if (f[batteryCol].Length == 0) continue;
                    if (DateTimeOffset.TryParse(f[timeCol], Inv, DateTimeStyles.None, out var t) && t >= cutoff && t <= now &&
                        double.TryParse(f[loadCol], NumberStyles.Float, Inv, out var w))
                        rows.Add((t, w));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not read CSV history for the average load", ex);
        }

        foreach (var (t, w) in rows.OrderBy(r => r.Item1))
            Add(t, w);
        if (rows.Count > 0)
            Log.Info($"Loaded {rows.Count} home-load samples from CSV for the 24 h average");
    }
}
