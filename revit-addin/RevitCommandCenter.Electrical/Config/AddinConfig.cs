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

    /// <summary>
    /// Send generated files to the chat that asked for them.
    ///
    /// On by default: without it a printed drawing reaches the chat as a path on
    /// a Windows machine nobody reading the message is sitting at. Turn it off
    /// for a site that will not have drawings leave the building, and the reply
    /// falls back to ExportBaseUrl and then to the local path.
    /// </summary>
    [JsonProperty("send_files_to_telegram")]
    public bool SendFilesToTelegram { get; set; } = true;

    /// <summary>
    /// The bot's token, needed to upload a file to the chat.
    ///
    /// The same token the webhook uses — from BotFather, or from the Vercel
    /// environment where it is already set. Only this machine and the
    /// deployment hold it; treat it like the Supabase key beside it.
    /// </summary>
    [JsonProperty("telegram_bot_token")]
    public string TelegramBotToken { get; set; } = string.Empty;

    /// <summary>
    /// Cloudinary account that generated files are uploaded to.
    ///
    /// This is what makes an export reach the website. A command from the
    /// website carries no chat_id, so there is no chat to push a file into;
    /// without an upload the file only ever exists on this machine and the
    /// reply names a path nobody else can open. Fill these in and the export
    /// shows up on the website's history page as a download instead.
    ///
    /// Leave them empty and nothing changes: files stay local, as before.
    /// </summary>
    [JsonProperty("cloudinary_cloud_name")]
    public string CloudinaryCloudName { get; set; } = string.Empty;

    [JsonProperty("cloudinary_api_key")]
    public string CloudinaryApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Treat like the Supabase key above: it signs uploads to your account and
    /// belongs only in this file, on this machine.
    /// </summary>
    [JsonProperty("cloudinary_api_secret")]
    public string CloudinaryApiSecret { get; set; } = string.Empty;

    /// <summary>Folder inside the Cloudinary account to keep exports in.</summary>
    [JsonProperty("cloudinary_folder")]
    public string CloudinaryFolder { get; set; } = "electrical-ai/exports";

    /// <summary>True when the three Cloudinary credentials are all present.</summary>
    // JsonIgnore because this is derived, not configured. Without it, Save()
    // writes `"HasCloudinary": true` into the file a person is meant to edit —
    // a line that looks like a switch, does nothing when changed, and sends
    // whoever is debugging their setup looking in the wrong place.
    [JsonIgnore]
    public bool HasCloudinary =>
        !string.IsNullOrWhiteSpace(CloudinaryCloudName)
        && !string.IsNullOrWhiteSpace(CloudinaryApiKey)
        && !string.IsNullOrWhiteSpace(CloudinaryApiSecret);

    [JsonProperty("language")]
    public string Language { get; set; } = "id";

    [JsonProperty("start_polling_on_launch")]
    public bool StartPollingOnLaunch { get; set; } = false;

    /// <summary>
    /// ProjectId is deliberately not required: an instance with none serves
    /// every project.
    /// </summary>
    [JsonIgnore]
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

    /// <summary>
    /// Copies every configured value from another instance into this one.
    ///
    /// In place rather than by replacement because this object is shared: the
    /// poller, the queue worker, and every handler hold the same reference, and
    /// handing a new instance to one of them would leave the rest reading the
    /// values the add-in started with. That is exactly the failure this exists
    /// to remove — Cloudinary keys filled in while Revit is open, and exports
    /// still coming back as local paths because the code doing the upload never
    /// saw them.
    /// </summary>
    public void ApplyFrom(AddinConfig other)
    {
        SupabaseUrl = other.SupabaseUrl;
        SupabaseKey = other.SupabaseKey;
        ProjectId = other.ProjectId;
        PollingIntervalSeconds = other.PollingIntervalSeconds;
        CommandTimeoutSeconds = other.CommandTimeoutSeconds;
        HangerFamilyName = other.HangerFamilyName;
        ExportDirectory = other.ExportDirectory;
        ExportBaseUrl = other.ExportBaseUrl;
        SendFilesToTelegram = other.SendFilesToTelegram;
        TelegramBotToken = other.TelegramBotToken;
        CloudinaryCloudName = other.CloudinaryCloudName;
        CloudinaryApiKey = other.CloudinaryApiKey;
        CloudinaryApiSecret = other.CloudinaryApiSecret;
        CloudinaryFolder = other.CloudinaryFolder;
        Language = other.Language;
        StartPollingOnLaunch = other.StartPollingOnLaunch;
    }

    /// <summary>When config.json was last written, or null when it is missing.</summary>
    public static DateTime? LastWrittenAt()
    {
        try
        {
            return File.Exists(ConfigPath) ? File.GetLastWriteTimeUtc(ConfigPath) : null;
        }
        catch (Exception ex)
        {
            Utils.Logger.Debug($"Could not stat {ConfigPath}: {ex.Message}");
            return null;
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
