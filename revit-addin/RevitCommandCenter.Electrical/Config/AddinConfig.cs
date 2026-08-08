using Newtonsoft.Json;

namespace RevitCommandCenter.Electrical.Config;

/// <summary>
/// Add-in settings, read from
/// <c>%APPDATA%\RevitCommandCenter\config.json</c>.
///
/// A file (rather than environment variables) because Revit is launched from
/// the Start menu, where a user-set environment variable often will not be
/// visible to the process.
/// </summary>
public sealed class AddinConfig
{
    [JsonProperty("supabase_url")]
    public string SupabaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Service-role key. This machine is trusted; the key never leaves it.
    /// </summary>
    [JsonProperty("supabase_key")]
    public string SupabaseKey { get; set; } = string.Empty;

    /// <summary>
    /// Which key class <see cref="SupabaseKey"/> is. Derived, never stored —
    /// config.json holds the key, and the key already says.
    /// </summary>
    [JsonIgnore]
    public SupabaseKeyKind KeyKind => SupabaseApiKey.Classify(SupabaseKey);

    /// <summary>
    /// Optional. Blank — the normal case — means this instance drains commands
    /// for every project, and which project a command belongs to is decided in
    /// Telegram with /project. Set it only to pin one Revit instance to one site.
    /// </summary>
    [JsonProperty("project_id")]
    public string ProjectId { get; set; } = string.Empty;

    [JsonProperty("polling_interval_seconds")]
    public int PollingIntervalSeconds { get; set; } = 4;

    [JsonProperty("command_timeout_seconds")]
    public int CommandTimeoutSeconds { get; set; } = 120;

    /// <summary>Revit family that hosts cable-tray hangers.</summary>
    [JsonProperty("hanger_family_name")]
    public string HangerFamilyName { get; set; } = "Hanger";

    /// <summary>Where exported schedules are written.</summary>
    [JsonProperty("export_directory")]
    public string ExportDirectory { get; set; } = string.Empty;

    /// <summary>Optional public base URL that maps onto ExportDirectory.</summary>
    [JsonProperty("export_base_url")]
    public string ExportBaseUrl { get; set; } = string.Empty;

    [JsonProperty("language")]
    public string Language { get; set; } = "id";

    [JsonProperty("start_polling_on_launch")]
    public bool StartPollingOnLaunch { get; set; } = false;

    /// <summary>
    /// ProjectId is deliberately not required: an instance with none serves
    /// every project.
    /// </summary>
    public bool IsUsable =>
        !string.IsNullOrWhiteSpace(SupabaseUrl)
        && !string.IsNullOrWhiteSpace(SupabaseKey);

    public static string ConfigDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RevitCommandCenter");

    public static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    /// <summary>
    /// Loads config, writing a commented template on first run so the user has
    /// something concrete to edit rather than a silent failure.
    /// </summary>
    public static AddinConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                WriteTemplate();
                return new AddinConfig();
            }

            var json = File.ReadAllText(ConfigPath);
            var config = JsonConvert.DeserializeObject<AddinConfig>(json);
            if (config is null)
            {
                return new AddinConfig();
            }

            if (string.IsNullOrWhiteSpace(config.ExportDirectory))
            {
                config.ExportDirectory = Path.Combine(ConfigDirectory, "exports");
            }

            // Guard against a config that would spin the CPU or hammer Supabase.
            config.PollingIntervalSeconds = Math.Clamp(config.PollingIntervalSeconds, 2, 300);
            config.CommandTimeoutSeconds = Math.Clamp(config.CommandTimeoutSeconds, 30, 3600);

            return config;
        }
        catch (Exception ex)
        {
            Utils.Logger.Error($"Failed to read {ConfigPath}: {ex.Message}");
            return new AddinConfig();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDirectory);
        File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(this, Formatting.Indented));
    }

    private static void WriteTemplate()
    {
        Directory.CreateDirectory(ConfigDirectory);
        var template = new AddinConfig
        {
            SupabaseUrl = "https://YOUR-PROJECT.supabase.co",
            SupabaseKey = "YOUR-SERVICE-ROLE-KEY",
            ExportDirectory = Path.Combine(ConfigDirectory, "exports"),
        };
        template.Save();
        Utils.Logger.Info($"Wrote config template to {ConfigPath}. Fill it in, then press Connect.");
    }
}
