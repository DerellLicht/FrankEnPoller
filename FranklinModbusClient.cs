using System.Text;

namespace FrankEnPoller;

/// <summary>One aGate reading. A null field means this unit or firmware doesn't provide it.</summary>
public sealed record FranklinReading(
    double? SocPct,      // SunSpec 713 SoC
    double? BatteryW,    // SunSpec 714 DCW: + discharging, − charging
    double? HomeLoadW,   // FranklinWH extension register 16000 (fallback 15506)
    double? GridW,       // aGate's grid-side power. Modbus: SunSpec 701 W (meaning unverified). Cloud: p_uti
    double? CapacityWh,  // SunSpec 713 WHRtg
    double? HomeTodayKwh = null,   // home energy used today. Cloud: kwh_load (matches the FranklinWH app). Not on Modbus
    double? SolarTodayKwh = null); // solar energy today. Cloud: kwh_sun (the aGate meters the Enphase PV). Not on Modbus

/// <summary>
/// Reads the FranklinWH aGate over Modbus TCP. The aGate exposes a SunSpec map starting
/// at register 0 (newer firmware mirrors it at 40000). Model addresses are discovered
/// by walking the SunSpec chain, not hard-coded. Read-only: nothing here writes.
///
/// Register knowledge comes from the SunSpec model definitions plus the community
/// franklinwh-modbus project; FranklinWH publishes no official map. Use the tray
/// menu's "aGate diagnostics" to dump everything and check it against reality.
/// </summary>
public sealed class FranklinModbusClient : IFranklinSource
{
    private static readonly int[] SunSpecBases = [0, 40000];
    private static readonly int[] HomeLoadRegisters = [16000, 15506]; // ~1 W, then ~100 W resolution

    private readonly ModbusTcpClient _modbus;
    private readonly string _endpoint;
    private List<SunSpecModel>? _models;
    private int _homeLoadIndex;

    private sealed record SunSpecModel(int Id, int Address, int Length);

    public FranklinModbusClient(string host, int port)
    {
        _modbus = new ModbusTcpClient(host, port, unitId: 1); // aGate ignores unit id
        _endpoint = $"{host}:{port}";
    }

    public string Description => $"Modbus TCP {_endpoint}";
    public TimeSpan MinInterval => TimeSpan.Zero;
    public Task<string> DiagnoseAsync(CancellationToken ct) => ScanAsync(ct);

    public async Task<FranklinReading> GetReadingAsync(CancellationToken ct)
    {
        _models ??= await DiscoverAsync(ct);
        try
        {
            var (soc, capacity) = await ReadStorageCapacityAsync(ct);
            var batteryW = await ReadBatteryPowerAsync(ct);
            var acW = await ReadAcPowerAsync(ct);
            var homeW = await ReadHomeLoadAsync(ct);
            return new FranklinReading(soc, batteryW, homeW, acW, capacity);
        }
        catch (ModbusException)
        {
            _models = null; // the map may have moved (e.g. firmware update): rediscover next poll
            throw;
        }
    }

    private SunSpecModel? Find(int id) => _models?.FirstOrDefault(m => m.Id == id);

    private async Task<(double? Soc, double? CapacityWh)> ReadStorageCapacityAsync(CancellationToken ct)
    {
        // 713: ID, L, WHRtg, WHAvail, SoC, SoH, Sta, WH_SF, Pct_SF
        if (Find(713) is not { } m) return (null, null);
        var r = await _modbus.ReadHoldingRegistersAsync(m.Address, 9, ct);
        return (Scale(U16(r[4]), r[8]), Scale(U16(r[2]), r[7]));
    }

    private async Task<double?> ReadBatteryPowerAsync(CancellationToken ct)
    {
        // 714: ID, L, PrtAlrms(2), NPrt, DCA, DCW, DCWhInj(4), DCWhAbs(4), DCA_SF, DCV_SF, DCW_SF, ...
        if (Find(714) is not { } m) return null;
        var r = await _modbus.ReadHoldingRegistersAsync(m.Address, 20, ct);
        return Scale(S16(r[6]), r[17]);
    }

    private async Task<double?> ReadAcPowerAsync(CancellationToken ct)
    {
        // 701: W at offset 10, W_SF at offset 116
        if (Find(701) is not { } m || m.Length < 115) return null;
        var w = await _modbus.ReadHoldingRegistersAsync(m.Address + 10, 1, ct);
        var sf = await _modbus.ReadHoldingRegistersAsync(m.Address + 116, 1, ct);
        return Scale(S16(w[0]), sf[0]);
    }

    private async Task<double?> ReadHomeLoadAsync(CancellationToken ct)
    {
        while (_homeLoadIndex < HomeLoadRegisters.Length)
        {
            var register = HomeLoadRegisters[_homeLoadIndex];
            try
            {
                var r = await _modbus.ReadHoldingRegistersAsync(register, 1, ct);
                return U16(r[0]);
            }
            catch (ModbusException ex)
            {
                Log.Info($"aGate home-load register {register} not readable ({ex.Message})");
                _homeLoadIndex++;
            }
        }
        return null;
    }

    private async Task<List<SunSpecModel>> DiscoverAsync(CancellationToken ct)
    {
        foreach (var baseAddress in SunSpecBases)
        {
            ushort[] marker;
            try { marker = await _modbus.ReadHoldingRegistersAsync(baseAddress, 2, ct); }
            catch (ModbusException) { continue; }
            if (marker[0] != 0x5375 || marker[1] != 0x6E53) continue; // "SunS"

            var models = new List<SunSpecModel>();
            var address = baseAddress + 2;
            for (var i = 0; i < 64; i++)
            {
                var header = await _modbus.ReadHoldingRegistersAsync(address, 2, ct);
                if (header[0] == 0xFFFF) break; // end of chain
                models.Add(new SunSpecModel(header[0], address, header[1]));
                address += 2 + header[1];
            }
            Log.Info($"aGate SunSpec map at {baseAddress}: models {string.Join(", ", models.Select(m => m.Id))}");
            return models;
        }
        throw new InvalidDataException($"No SunSpec register map found on {_endpoint}.");
    }

    /// <summary>Human-readable dump of everything we know how to find, for checking the register map.</summary>
    public async Task<string> ScanAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"FranklinWH aGate Modbus scan of {_endpoint}, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        try
        {
            _models = await DiscoverAsync(ct);
            sb.AppendLine("SunSpec models (id @ address, length):");
            foreach (var m in _models)
                sb.AppendLine($"  {m.Id,4} @ {m.Address,5}  L={m.Length}");

            if (Find(1) is { } common)
            {
                var r = await _modbus.ReadHoldingRegistersAsync(common.Address, 66, ct);
                sb.AppendLine();
                sb.AppendLine($"Manufacturer: {Str(r, 2, 16)}");
                sb.AppendLine($"Model:        {Str(r, 18, 16)}");
                sb.AppendLine($"Version:      {Str(r, 42, 8)}");
                sb.AppendLine($"Serial:       {Str(r, 50, 16)}");
            }

            var reading = await GetReadingAsync(ct);
            sb.AppendLine();
            sb.AppendLine("Decoded reading:");
            sb.AppendLine($"  SoC          {reading.SocPct?.ToString("0.#") ?? "n/a"} %");
            sb.AppendLine($"  Battery      {reading.BatteryW?.ToString("0") ?? "n/a"} W  (+ discharging, - charging)");
            sb.AppendLine($"  Home load    {reading.HomeLoadW?.ToString("0") ?? "n/a"} W  (register {(_homeLoadIndex < HomeLoadRegisters.Length ? HomeLoadRegisters[_homeLoadIndex] : "none")})");
            sb.AppendLine($"  M701 W       {reading.GridW?.ToString("0") ?? "n/a"} W");
            sb.AppendLine($"  Capacity     {reading.CapacityWh?.ToString("0") ?? "n/a"} Wh");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sb.AppendLine($"SunSpec read failed: {ex.Message}");
        }

        foreach (var (start, count) in new[] { (15000, 44), (15500, 14), (16000, 3) })
        {
            sb.AppendLine();
            sb.AppendLine($"Extension registers {start}-{start + count - 1} (uint16 / int16):");
            try
            {
                var r = await _modbus.ReadHoldingRegistersAsync(start, count, ct);
                for (var i = 0; i < count; i++)
                    sb.AppendLine($"  {start + i}: {r[i],6} / {(short)r[i],6}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sb.AppendLine($"  read failed: {ex.Message}");
            }
        }
        return sb.ToString();
    }

    // SunSpec "not implemented" sentinels: uint16 0xFFFF, int16 / scale factor 0x8000.
    private static double? U16(ushort v) => v == 0xFFFF ? null : v;
    private static double? S16(ushort v) => v == 0x8000 ? null : (short)v;

    private static double? Scale(double? value, ushort scaleFactor) =>
        value * Math.Pow(10, scaleFactor == 0x8000 ? 0 : (short)scaleFactor);

    private static string Str(ushort[] r, int offset, int length)
    {
        var bytes = new byte[length * 2];
        for (var i = 0; i < length; i++)
        {
            bytes[i * 2] = (byte)(r[offset + i] >> 8);
            bytes[i * 2 + 1] = (byte)r[offset + i];
        }
        return Encoding.ASCII.GetString(bytes).TrimEnd('\0', ' ');
    }

    public void Dispose() => _modbus.Dispose();
}
