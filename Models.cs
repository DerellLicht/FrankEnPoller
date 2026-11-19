namespace FrankEnPoller;

// Shapes of /production.json?details=1 (only the fields we use).
public sealed class ProductionResponse
{
    public List<Meter> Production { get; set; } = [];
    public List<Meter> Consumption { get; set; } = [];
}

public sealed class Meter
{
    public string Type { get; set; } = "";             // "inverters" or "eim" (CT meter)
    public string? MeasurementType { get; set; }        // production / total-consumption / net-consumption
    public long ReadingTime { get; set; }
    public double WNow { get; set; }
    public double WhLifetime { get; set; }
    public List<Line>? Lines { get; set; }
}

public sealed class Line
{
    public double WNow { get; set; }
    public double RmsVoltage { get; set; }
    public double RmsCurrent { get; set; }
}

/// <summary>
/// One reading, taken from the CT meters ("eim"), which update in real time.
/// The "inverters" entry lags by minutes, and the whToday / whLastSevenDays fields
/// on this firmware just repeat the lifetime totals, so neither is used.
/// GridW: positive = importing from the grid, negative = exporting.
/// ConsumptionW includes the FranklinWH aGate, which sits downstream of the
/// consumption CTs: battery charging adds to it and discharging subtracts from it.
/// </summary>
public sealed record EnphaseReading(
    DateTimeOffset Timestamp,
    double SolarW,
    double ConsumptionW,
    double GridW,
    double SolarLifetimeWh,
    double ConsumptionLifetimeWh,
    double GridNetLifetimeWh)
{
    public static EnphaseReading From(ProductionResponse r)
    {
        var prod = Find(r.Production, "production");
        var total = Find(r.Consumption, "total-consumption");
        var net = Find(r.Consumption, "net-consumption");

        return new EnphaseReading(
            DateTimeOffset.FromUnixTimeSeconds(prod.ReadingTime),
            Math.Max(0, prod.WNow), // CT reads a few watts negative at night (inverter standby)
            total.WNow,
            net.WNow,
            prod.WhLifetime,
            total.WhLifetime,
            net.WhLifetime);
    }

    private static Meter Find(List<Meter> meters, string measurementType) =>
        meters.FirstOrDefault(m => m.Type == "eim" && m.MeasurementType == measurementType)
        ?? throw new InvalidDataException($"No '{measurementType}' CT meter in gateway response.");
}
