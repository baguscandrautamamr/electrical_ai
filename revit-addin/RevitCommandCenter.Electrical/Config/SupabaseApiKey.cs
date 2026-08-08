using System.Text;
using Newtonsoft.Json.Linq;

namespace RevitCommandCenter.Electrical.Config;

/// <summary>Which class of Supabase API key a string is.</summary>
public enum SupabaseKeyKind
{
    /// <summary>Not recognisable — a self-hosted or custom-signed key.</summary>
    Unknown,

    /// <summary>Anon / publishable. Row-level security applies to every request.</summary>
    Anon,

    /// <summary>service_role / secret. Bypasses row-level security.</summary>
    ServiceRole,
}

/// <summary>
/// Tells the two Supabase keys apart before the database has to.
///
/// Both keys are long opaque strings from the same dashboard page, and pasting
/// the wrong one produces no honest error: the anon key authenticates fine, so
/// the URL works, the connection test works, and every project-scoped table is
/// then simply empty because row-level security filters it away. The queue
/// looks idle, the claim returns nothing, and the only hard failure is a write
/// — registering the open model — coming back as
/// <c>42501 new row violates row-level security policy</c> deep in the log.
///
/// A key carries its own class, so there is no reason to wait for that.
/// </summary>
public static class SupabaseApiKey
{
    /// <summary>
    /// Said wherever the anon key is found, so the wording does not drift
    /// between the log, the Settings dialog and the Status dialog.
    /// </summary>
    public const string AnonKeyAdvice =
        "The Supabase key configured here is the anon (publishable) key, not "
        + "service_role. Row-level security applies to it: the command queue reads as "
        + "empty, so commands sent from Telegram are never claimed, and registering the "
        + "open model is refused outright. Take the service_role key — labelled "
        + "\"secret\" on newer projects, and starting sb_secret_ — from the Supabase "
        + "dashboard under Project Settings → API, paste it into Command Center → "
        + "Settings, save, and press Connect.";

    /// <summary>
    /// Classifies <paramref name="key"/> without calling Supabase.
    ///
    /// Recognises both key formats: the current <c>sb_publishable_</c> /
    /// <c>sb_secret_</c> prefixes, and legacy JWTs, whose <c>role</c> claim
    /// says which they are. Anything else is <see cref="SupabaseKeyKind.Unknown"/>
    /// — a self-hosted instance may sign its own, and guessing at those would
    /// block a working setup.
    /// </summary>
    public static SupabaseKeyKind Classify(string? key)
    {
        var trimmed = key?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return SupabaseKeyKind.Unknown;

        if (trimmed.StartsWith("sb_secret_", StringComparison.Ordinal))
        {
            return SupabaseKeyKind.ServiceRole;
        }
        if (trimmed.StartsWith("sb_publishable_", StringComparison.Ordinal))
        {
            return SupabaseKeyKind.Anon;
        }

        return RoleClaim(trimmed) switch
        {
            "service_role" => SupabaseKeyKind.ServiceRole,
            "anon" => SupabaseKeyKind.Anon,
            _ => SupabaseKeyKind.Unknown,
        };
    }

    /// <summary>Human-readable key class, for the Status dialog.</summary>
    public static string Describe(SupabaseKeyKind kind) => kind switch
    {
        SupabaseKeyKind.ServiceRole => "service_role (correct)",
        SupabaseKeyKind.Anon => "anon — wrong key, see below",
        _ => "unrecognised",
    };

    /// <summary>
    /// The <c>role</c> claim of a JWT, or null when the string is not one.
    ///
    /// The signature is deliberately not verified: only Supabase can do that,
    /// and the claim is being read to give better advice, not to grant anything.
    /// </summary>
    private static string? RoleClaim(string key)
    {
        var parts = key.Split('.');
        if (parts.Length != 3) return null;

        try
        {
            var payload = Encoding.UTF8.GetString(FromBase64Url(parts[1]));
            return JObject.Parse(payload)["role"]?.Value<string>();
        }
        catch
        {
            // Not a JWT after all, or not JSON inside. Unknown is the answer.
            return null;
        }
    }

    private static byte[] FromBase64Url(string segment)
    {
        var padded = segment.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        return Convert.FromBase64String(padded);
    }
}
