using Newtonsoft.Json.Linq;

namespace RevitCommandCenter.Electrical.Utils;

/// <summary>
/// Loads the same id/en bundles the webhook uses, from
/// <c>Resources/translations/</c> next to the DLL.
///
/// The add-in's own UI is mostly English, but any text it puts into a command
/// result is read by a Telegram user in their chosen language, so it has to be
/// able to resolve the same keys.
/// </summary>
public static class Localization
{
    private const string DefaultLanguage = "id";

    private static readonly Dictionary<string, JObject> Bundles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static string CurrentLanguage { get; set; } = DefaultLanguage;

    private static JObject? Bundle(string language)
    {
        lock (Gate)
        {
            if (Bundles.TryGetValue(language, out var cached)) return cached;

            try
            {
                var directory = Path.Combine(
                    Path.GetDirectoryName(typeof(Localization).Assembly.Location) ?? string.Empty,
                    "Resources",
                    "translations");

                var path = Path.Combine(directory, $"{language}.json");
                if (!File.Exists(path))
                {
                    Logger.Warn($"Translation bundle not found: {path}");
                    return null;
                }

                var bundle = JObject.Parse(File.ReadAllText(path));
                Bundles[language] = bundle;
                return bundle;
            }
            catch (Exception ex)
            {
                Logger.Error($"Could not load '{language}' translations", ex);
                return null;
            }
        }
    }

    /// <summary>
    /// Resolves a dotted key, falling back to Indonesian and then to the key
    /// itself. A half-translated string beats an exception in a result payload.
    /// </summary>
    public static string T(string key, string? language = null)
    {
        var lang = language ?? CurrentLanguage;

        return Lookup(Bundle(lang), key)
               ?? Lookup(Bundle(DefaultLanguage), key)
               ?? key;
    }

    /// <summary>As <see cref="T"/>, with <c>{name}</c> placeholders filled in.</summary>
    public static string T(string key, IDictionary<string, object> vars, string? language = null)
    {
        var template = T(key, language);

        foreach (var (name, value) in vars)
        {
            template = template.Replace($"{{{name}}}", value?.ToString() ?? string.Empty);
        }

        return template;
    }

    private static string? Lookup(JObject? bundle, string key)
    {
        if (bundle is null) return null;

        JToken? node = bundle;
        foreach (var segment in key.Split('.'))
        {
            node = node?[segment];
            if (node is null) return null;
        }

        return node.Type == JTokenType.String ? node.Value<string>() : null;
    }
}
