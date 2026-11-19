using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FrankEnPoller;

/// <summary>A way of reading the FranklinWH aGate: the FranklinWH cloud or local Modbus TCP.</summary>
public interface IFranklinSource : IDisposable
{
    string Description { get; }

    /// <summary>Shortest sensible time between polls of this source.</summary>
    TimeSpan MinInterval { get; }

    Task<FranklinReading> GetReadingAsync(CancellationToken ct);

    /// <summary>Human-readable report of what the source returns, for checking field meanings.</summary>
    Task<string> DiagnoseAsync(CancellationToken ct);
}

/// <summary>
/// Reads the aGate through the FranklinWH cloud, the same service the FranklinWH app uses,
/// logging in with the app's email and password. The API is unofficial and undocumented:
/// endpoints and field names come from the open-source franklinwh-python library and may
/// change without notice. Read-only: nothing here changes any aGate setting.
/// </summary>
public sealed class FranklinCloudClient : IFranklinSource
{
    private const string BaseUrl = "https://energy.franklinwh.com/hes-gateway/terminal/";

    // FranklinWH locks accounts after repeated bad logins, so back off hard on failure.
    private static readonly TimeSpan LoginRetryDelay = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http;
    private readonly string _email, _password;
    private string? _gatewayId;
    private string? _token;
    private DateTimeOffset _loginRetryAt = DateTimeOffset.MinValue;
    private string? _loginError;

    public FranklinCloudClient(string email, string password, string? gatewayId, string baseUrl = BaseUrl)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(20) };
        _email = email;
        _password = password;
        _gatewayId = string.IsNullOrWhiteSpace(gatewayId) ? null : gatewayId.Trim();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FrankEnPoller/1.0");
    }

    public string Description => "FranklinWH cloud";
    public TimeSpan MinInterval => TimeSpan.FromSeconds(30);

    public async Task<FranklinReading> GetReadingAsync(CancellationToken ct) =>
        ToReading(await GetRuntimeDataAsync(ct));

    // runtimeData power fields are kW. p_fhp: − charging, + discharging (confirmed on this system,
    // 2026-11-18: discharging at night). p_uti: + import, − export.
    private static FranklinReading ToReading(JsonElement rt) => new(
        SocPct: Num(rt, "soc"),
        BatteryW: Num(rt, "p_fhp") * 1000,
        HomeLoadW: Num(rt, "p_load") * 1000,
        GridW: Num(rt, "p_uti") * 1000,
        CapacityWh: null,
        HomeTodayKwh: Num(rt, "kwh_load"),   // today's totals, reset at midnight
        SolarTodayKwh: Num(rt, "kwh_sun"));

    private async Task<JsonElement> GetRuntimeDataAsync(CancellationToken ct)
    {
        var gateway = await GetGatewayIdAsync(ct);
        var result = await GetAsync($"getDeviceCompositeInfo?gatewayId={Uri.EscapeDataString(gateway)}&refreshFlag=1&lang=en_US", ct);
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("runtimeData", out var rt) || rt.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("FranklinWH cloud returned no runtime data (is the aGate online?).");
        return rt;
    }

    private async Task<string> GetGatewayIdAsync(CancellationToken ct)
    {
        if (_gatewayId != null) return _gatewayId;

        var list = await GetAsync("getHomeGatewayList?lang=en_US", ct);
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            throw new InvalidDataException("No aGate found on this FranklinWH account.");
        if (list.GetArrayLength() > 1)
            Log.Info("FranklinWH account has several aGates; using the first. Set the gateway ID in Settings to choose another.");

        _gatewayId = list[0].TryGetProperty("id", out var id) ? id.GetString() : null;
        if (string.IsNullOrEmpty(_gatewayId))
            throw new InvalidDataException("FranklinWH gateway list had no gateway id.");
        Log.Info($"Using FranklinWH gateway {_gatewayId}");
        return _gatewayId;
    }

    /// <summary>GET an endpoint and return its "result", logging in again once if the token has expired.</summary>
    private async Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await EnsureTokenAsync(force: attempt > 0, ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("loginToken", token);
            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                continue;
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var code = Code(root);
            if (code is 401 or 10009 && attempt == 0)
                continue; // token expired: log in again and retry once

            if (root.TryGetProperty("result", out var result) && result.ValueKind != JsonValueKind.Null)
                return result.Clone();
            throw new InvalidOperationException($"FranklinWH cloud error {code?.ToString() ?? "?"}: {Message(root)}");
        }
    }

    private async Task<string> EnsureTokenAsync(bool force, CancellationToken ct)
    {
        if (!force && _token != null) return _token;

        if (DateTimeOffset.UtcNow < _loginRetryAt)
            throw new InvalidOperationException($"{_loginError} Next login attempt at {_loginRetryAt.LocalDateTime:t}.");

        try
        {
            _token = await LoginAsync(ct);
            Log.Info("Logged in to FranklinWH cloud");
            return _token;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _token = null;
            _loginError = ex.Message;
            _loginRetryAt = DateTimeOffset.UtcNow + LoginRetryDelay;
            throw;
        }
    }

    private async Task<string> LoginAsync(CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["account"] = _email,
            // The FranklinWH app sends an MD5 hex digest of the password; this is their protocol, not our choice.
            ["password"] = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(_password))).ToLowerInvariant(),
            ["lang"] = "en_US",
            ["type"] = "1"
        });
        using var response = await _http.PostAsync("initialize/appUserOrInstallerLogin", form, ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        switch (Code(root))
        {
            case 401: throw new InvalidOperationException($"FranklinWH login rejected ({Message(root)}). Check the email and password.");
            case 400: throw new InvalidOperationException($"FranklinWH says the account is locked ({Message(root)}).");
        }
        if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("token", out var token) && token.GetString() is { Length: > 0 } value)
            return value;
        throw new InvalidOperationException($"FranklinWH login failed: {Message(root)}");
    }

    public async Task<string> DiagnoseAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"FranklinWH cloud check, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        try
        {
            sb.AppendLine($"Gateway: {await GetGatewayIdAsync(ct)}");
            var rt = await GetRuntimeDataAsync(ct);
            var r = ToReading(rt);
            sb.AppendLine();
            sb.AppendLine("Decoded reading:");
            sb.AppendLine($"  SoC         {r.SocPct?.ToString("0.#") ?? "n/a"} %");
            sb.AppendLine($"  Battery     {r.BatteryW?.ToString("0") ?? "n/a"} W  (+ discharging, - charging)");
            sb.AppendLine($"  Home load   {r.HomeLoadW?.ToString("0") ?? "n/a"} W");
            sb.AppendLine($"  Home today  {r.HomeTodayKwh?.ToString("0.00") ?? "n/a"} kWh");
            sb.AppendLine($"  Solar today {r.SolarTodayKwh?.ToString("0.00") ?? "n/a"} kWh");
            sb.AppendLine($"  aGate grid  {r.GridW?.ToString("0") ?? "n/a"} W  (+ import, - export, as the aGate sees it)");

            // Energy balance as a check on the battery sign: sources must equal the home load.
            double sun = Num(rt, "p_sun") ?? 0, grid = Num(rt, "p_uti") ?? 0, gen = Num(rt, "p_gen") ?? 0,
                   fhp = Num(rt, "p_fhp") ?? 0, load = Num(rt, "p_load") ?? 0;
            sb.AppendLine();
            sb.AppendLine("Energy balance check (kW), sun + grid + gen ± battery vs load:");
            sb.AppendLine($"  battery + = discharging: mismatch {Math.Abs(sun + grid + gen + fhp - load):0.000}");
            sb.AppendLine($"  battery + = charging:    mismatch {Math.Abs(sun + grid + gen - fhp - load):0.000}");
            sb.AppendLine("  (the smaller mismatch is the right convention; only meaningful while the battery is active)");

            sb.AppendLine();
            sb.AppendLine("Raw runtimeData:");
            sb.AppendLine(JsonSerializer.Serialize(rt, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sb.AppendLine($"Failed: {ex.Message}");
        }
        return sb.ToString();
    }

    private static int? Code(JsonElement root) =>
        root.TryGetProperty("code", out var c) && c.TryGetInt32(out var v) ? v : null;

    private static string Message(JsonElement root) =>
        root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "no message";

    private static double? Num(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null
        };
    }

    public void Dispose() => _http.Dispose();
}
