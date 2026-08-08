using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Database;

/// <summary>
/// Thin PostgREST client over <see cref="HttpClient"/>.
///
/// Outbound HTTPS only — no inbound port, no WebSocket — which is what makes
/// the add-in work behind a corporate firewall without any IT involvement.
/// </summary>
public sealed class SupabaseClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public SupabaseClient(string baseUrl, string apiKey)
    {
        _baseUrl = baseUrl.TrimEnd('/');

        _http = new HttpClient
        {
            // Comfortably longer than a poll cycle, short enough that a hung
            // request cannot wedge the timer forever.
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Add("apikey", apiKey);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static StringContent JsonBody(object payload) =>
        new(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

    private async Task<string> SendAsync(
        HttpRequestMessage request,
        string context,
        CancellationToken ct)
    {
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new SupabaseException(
                $"{context} failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {body}",
                (int)response.StatusCode);
        }

        return body;
    }

    public async Task<List<T>> SelectAsync<T>(
        string table,
        string query,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/rest/v1/{table}?{query}");
        var body = await SendAsync(request, $"select {table}", ct).ConfigureAwait(false);
        return JsonConvert.DeserializeObject<List<T>>(body) ?? new List<T>();
    }

    public async Task<List<T>> InsertAsync<T>(
        string table,
        object values,
        bool returning = true,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/rest/v1/{table}")
        {
            Content = JsonBody(values),
        };
        request.Headers.Add("Prefer", returning ? "return=representation" : "return=minimal");

        var body = await SendAsync(request, $"insert {table}", ct).ConfigureAwait(false);
        if (!returning || string.IsNullOrWhiteSpace(body)) return new List<T>();
        return JsonConvert.DeserializeObject<List<T>>(body) ?? new List<T>();
    }

    /// <summary>Insert-or-update. <paramref name="onConflict"/> names the unique columns.</summary>
    public async Task UpsertAsync(
        string table,
        object values,
        string onConflict,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/rest/v1/{table}?on_conflict={Uri.EscapeDataString(onConflict)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonBody(values) };
        request.Headers.Add("Prefer", "return=minimal,resolution=merge-duplicates");

        await SendAsync(request, $"upsert {table}", ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(
        string table,
        string query,
        object values,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"{_baseUrl}/rest/v1/{table}?{query}")
        {
            Content = JsonBody(values),
        };
        request.Headers.Add("Prefer", "return=minimal");

        await SendAsync(request, $"update {table}", ct).ConfigureAwait(false);
    }

    public async Task<T?> RpcAsync<T>(
        string function,
        object args,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/rest/v1/rpc/{function}")
        {
            Content = JsonBody(args),
        };

        var body = await SendAsync(request, $"rpc {function}", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body)) return default;
        return JsonConvert.DeserializeObject<T>(body);
    }

    public async Task RpcAsync(string function, object args, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/rest/v1/rpc/{function}")
        {
            Content = JsonBody(args),
        };
        await SendAsync(request, $"rpc {function}", ct).ConfigureAwait(false);
    }

    /// <summary>Connectivity probe used by the ribbon's Connect button.</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            await SelectAsync<object>("projects", "select=id&limit=1", ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Supabase ping failed: {ex.Message}");
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}

public sealed class SupabaseException : Exception
{
    public int StatusCode { get; }

    public SupabaseException(string message, int statusCode) : base(message)
    {
        StatusCode = statusCode;
    }
}
