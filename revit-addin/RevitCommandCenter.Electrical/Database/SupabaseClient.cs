using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using RevitCommandCenter.Electrical.Config;
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

    /// <summary>
    /// Key class this client authenticates with. Kept so a refused write can
    /// name the cause instead of quoting PostgREST at the user.
    /// </summary>
    public SupabaseKeyKind KeyKind { get; }

    public SupabaseClient(string baseUrl, string apiKey)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        KeyKind = SupabaseApiKey.Classify(apiKey);

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

    /// <summary>
    /// Puts a file into Supabase Storage, replacing whatever was there.
    ///
    /// Storage is how a file written on the machine running Revit reaches a
    /// phone. Telegram will fetch a document from a URL but cannot see a path
    /// on someone's C: drive, and the export folder is not on the internet.
    /// </summary>
    public async Task UploadAsync(
        string bucket,
        string key,
        byte[] bytes,
        string contentType,
        CancellationToken ct = default)
    {
        var path = $"{_baseUrl}/storage/v1/object/{Uri.EscapeDataString(bucket)}/{EscapeKey(key)}";

        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        // Without this an export run twice in one day fails on the second file
        // rather than replacing the first.
        request.Headers.Add("x-upsert", "true");

        await SendAsync(request, $"Upload {bucket}/{key}", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Where a stored object can be read from, for a public bucket.
    ///
    /// Composed rather than asked for, so a handler can put the link in its
    /// reply before the upload has actually happened — the poller performs the
    /// upload before it reports the result, so the link works by the time
    /// anyone sees it.
    /// </summary>
    public string PublicUrl(string bucket, string key) =>
        $"{_baseUrl}/storage/v1/object/public/{Uri.EscapeDataString(bucket)}/{EscapeKey(key)}";

    /// <summary>Escapes each path segment, leaving the slashes between them.</summary>
    private static string EscapeKey(string key) =>
        string.Join("/", key.Split('/').Select(Uri.EscapeDataString));

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
