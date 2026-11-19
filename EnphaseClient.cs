using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace FrankEnPoller;

/// <summary>
/// Reads the Enphase IQ Gateway local API. Owner tokens are short-lived (1 hour as of
/// late 2026), so this fetches fresh ones from Enlighten/Entrez automatically: before
/// expiry, and again on any 401 from the gateway.
/// </summary>
public sealed class EnphaseClient : IDisposable
{
    private const string EnlightenLoginUrl = "https://enlighten.enphaseenergy.com/login/login.json";
    private const string EntrezTokenUrl = "https://entrez.enphaseenergy.com/tokens";
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _gateway;
    private readonly HttpClient _cloud = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _serial, _email, _password, _tokenCachePath;

    private static readonly TimeSpan LoginRetryDelay = TimeSpan.FromMinutes(5);

    private string? _token;
    private DateTimeOffset _tokenExpires = DateTimeOffset.MinValue;
    private DateTimeOffset _loginRetryAt = DateTimeOffset.MinValue;
    private string? _loginError;

    public EnphaseClient(string host, string serial, string email, string password, string tokenCachePath)
    {
        // The gateway serves a self-signed certificate. Validation is skipped ONLY for
        // this LAN client; the cloud client below validates normally.
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        _gateway = new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://{host}/"),
            Timeout = TimeSpan.FromSeconds(15)
        };

        _serial = serial;
        _email = email;
        _password = password;
        _tokenCachePath = tokenCachePath;
        LoadCachedToken();
    }

    public async Task<EnphaseReading> GetReadingAsync(CancellationToken ct)
    {
        var data = await GetJsonAsync<ProductionResponse>("production.json?details=1", ct);
        return EnphaseReading.From(data);
    }

    /// <summary>GET a gateway endpoint, refreshing the token once if the gateway rejects it.</summary>
    public async Task<T> GetJsonAsync<T>(string path, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await EnsureTokenAsync(force: attempt > 0, ct);

            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            using var resp = await _gateway.SendAsync(req, ct);

            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                continue; // token revoked or clock skew: get a new one and retry once

            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<T>(JsonOpts, ct)
                   ?? throw new InvalidDataException($"Empty response from {path}");
        }
    }

    private async Task EnsureTokenAsync(bool force, CancellationToken ct)
    {
        if (!force && _token != null && DateTimeOffset.UtcNow < _tokenExpires - RefreshMargin)
            return;

        // After a failed login, wait before trying again so bad credentials don't hit
        // Enlighten on every poll (and risk an account lockout).
        if (DateTimeOffset.UtcNow < _loginRetryAt)
            throw new InvalidOperationException($"{_loginError} Next login attempt at {_loginRetryAt.LocalDateTime:t}.");

        try
        {
            _token = await FetchTokenAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _loginError = ex.Message;
            _loginRetryAt = DateTimeOffset.UtcNow + LoginRetryDelay;
            throw;
        }
        _tokenExpires = ReadJwtExpiry(_token);
        File.WriteAllText(_tokenCachePath, _token);
        Log.Info($"Got new gateway token, valid until {_tokenExpires.LocalDateTime:g}");
    }

    private async Task<string> FetchTokenAsync(CancellationToken ct)
    {
        // Step 1: log in to Enlighten to get a session id.
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["user[email]"] = _email,
            ["user[password]"] = _password
        });
        using var login = await _cloud.PostAsync(EnlightenLoginUrl, form, ct);
        var loginBody = await login.Content.ReadAsStringAsync(ct);
        if (!login.IsSuccessStatusCode)
            throw new InvalidOperationException($"Enlighten login failed ({(int)login.StatusCode}). Check email/password.");

        string? sessionId = null;
        using (var doc = JsonDocument.Parse(loginBody))
        {
            if (doc.RootElement.TryGetProperty("session_id", out var sid))
                sessionId = sid.GetString();
        }
        if (string.IsNullOrEmpty(sessionId))
            throw new InvalidOperationException("Enlighten login returned no session_id. Check email/password (MFA-enabled accounts aren't supported).");

        // Step 2: exchange the session for a gateway-scoped JWT.
        using var tokenResp = await _cloud.PostAsJsonAsync(EntrezTokenUrl,
            new { session_id = sessionId, serial_num = _serial, username = _email }, ct);
        var token = (await tokenResp.Content.ReadAsStringAsync(ct)).Trim();
        if (!tokenResp.IsSuccessStatusCode || token.Count(c => c == '.') != 2)
            throw new InvalidOperationException($"Entrez token request failed ({(int)tokenResp.StatusCode}). Check ENPHASE_SERIAL.");

        return token;
    }

    private void LoadCachedToken()
    {
        if (!File.Exists(_tokenCachePath)) return;
        try
        {
            var cached = File.ReadAllText(_tokenCachePath).Trim();
            _tokenExpires = ReadJwtExpiry(cached);
            _token = cached;
        }
        catch
        {
            // Unreadable cache: ignore it and fetch a new token on first poll.
        }
    }

    private static DateTimeOffset ReadJwtExpiry(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
        return DateTimeOffset.FromUnixTimeSeconds(doc.RootElement.GetProperty("exp").GetInt64());
    }

    public void Dispose()
    {
        _gateway.Dispose();
        _cloud.Dispose();
    }
}
