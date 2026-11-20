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
    public int? ActiveCount { get; set; }               // 0 on an "eim" entry whose CT isn't installed or enabled
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
/// One reading, preferably from the CT meters ("eim"), which update in real time.
/// Gateways without a production CT fall back to the "inverters" entry, which only
/// updates when the microinverters report (every few minutes). Without consumption
/// CTs, ConsumptionW and GridW are null.
/// The whToday / whLastSevenDays fields on current firmware just repeat the lifetime
/// totals, so they aren't used.
/// GridW: positive = importing from the grid, negative = exporting.
/// ConsumptionW includes a FranklinWH aGate that sits downstream of the consumption
/// CTs: battery charging adds to it and discharging subtracts from it.
/// </summary>
public sealed record EnphaseReading(
    DateTimeOffset Timestamp,
    double SolarW,
    double? ConsumptionW,
    double? GridW,
    double SolarLifetimeWh,
    double? ConsumptionLifetimeWh,
    double? GridNetLifetimeWh,
    bool SolarFromInverters = false)
{
    public static EnphaseReading From(ProductionResponse r)
    {
        var ct = FindCt(r.Production, "production");
        var prod = ct
                   ?? r.Production.FirstOrDefault(m => m.Type == "inverters")
                   ?? throw new InvalidDataException("No solar production data in gateway response.");
        var total = FindCt(r.Consumption, "total-consumption");
        var net = FindCt(r.Consumption, "net-consumption");

        return new EnphaseReading(
            DateTimeOffset.FromUnixTimeSeconds(prod.ReadingTime),
            Math.Max(0, prod.WNow), // CT reads a few watts negative at night (inverter standby)
            total?.WNow,
            net?.WNow,
            prod.WhLifetime,
            total?.WhLifetime,
            net?.WhLifetime,
            SolarFromInverters: ct is null);
    }

    /// <summary>The CT meter of this type, if it's installed and active.</summary>
    private static Meter? FindCt(List<Meter> meters, string measurementType) =>
        meters.FirstOrDefault(m => m.Type == "eim" && m.MeasurementType == measurementType && m.ActiveCount != 0);
}
