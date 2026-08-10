using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json;
using RevitCommandCenter.Electrical.Config;
using RevitCommandCenter.Electrical.Database;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Ribbon;

/// <summary>One row of the <c>projects</c> table, for the picker.</summary>
internal sealed class ProjectRow
{
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;
    [JsonProperty("code")] public string Code { get; set; } = string.Empty;
    [JsonProperty("name")] public string Name { get; set; } = string.Empty;

    public string Display => string.IsNullOrWhiteSpace(Code) ? Name : $"{Code} — {Name}";
}

/// <summary>
/// Settings dialog.
///
/// Asks only for what this machine alone can know: the Supabase URL and key.
/// The project is deliberately absent — that choice belongs in Telegram, where
/// /project lists the open Revit models and a tap sets the active one. Asking
/// for it here as well meant configuring the same fact in two places that had
/// to agree.
///
/// Built in code rather than XAML: a Revit add-in loads from an arbitrary
/// folder, and resource-URI lookups for loose XAML are a common source of
/// load-time failures. There is no designer to lose.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
    private static readonly Brush Subtle = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x6E));

    private readonly AddinConfig _config;

    private readonly TextBox _url = new();
    private readonly PasswordBox _key = new();
    private readonly TextBox _pollInterval = new();
    private readonly TextBox _hangerFamily = new();
    private readonly TextBox _exportDirectory = new();
    private readonly PasswordBox _botToken = new();
    private readonly CheckBox _sendFiles = new();
    private readonly ComboBox _language = new();
    private readonly CheckBox _autoStart = new();
    private readonly TextBlock _status = new();
    private readonly Button _test = new();

    public SettingsWindow(AddinConfig config)
    {
        _config = config;

        Title = "Command Center — settings";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF7));

        Content = BuildLayout();
        LoadFromConfig();
    }

    // ---------------------------------------------------------------- layout

    private UIElement BuildLayout()
    {
        var root = new StackPanel { Margin = new Thickness(20) };

        root.Children.Add(Heading("Supabase connection"));
        root.Children.Add(Hint(
            "Supabase dashboard → Project Settings → API. Use the project URL and the "
            + "service_role key — labelled \"secret\" on newer projects, and starting "
            + "sb_secret_. Not the anon/publishable key: it is the one shown first, and "
            + "row-level security leaves it unable to see the command queue at all. This "
            + "machine is trusted; the key never leaves it."));
        root.Children.Add(Field("Project URL", _url, "https://xxxxxxxx.supabase.co"));
        root.Children.Add(Field("service_role (secret) key", _key));

        _test.Content = "Test connection and load projects";
        _test.Padding = new Thickness(14, 6, 14, 6);
        _test.HorizontalAlignment = HorizontalAlignment.Left;
        _test.Margin = new Thickness(0, 4, 0, 0);
        _test.Click += OnTestConnection;
        root.Children.Add(_test);

        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(_status);

        root.Children.Add(Heading("Behaviour"));
        root.Children.Add(Field("Poll interval (seconds)", _pollInterval, "4 — lower feels faster and costs more API calls"));
        root.Children.Add(Field("Hanger family name", _hangerFamily));
        root.Children.Add(Field("Export folder", _exportDirectory));

        _sendFiles.Content = "Send printed sheets and schedules to the Telegram chat";
        _sendFiles.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(_sendFiles);
        root.Children.Add(Field(
            "Telegram bot token",
            _botToken,
            "The same token the webhook uses. Needed only to upload files; leave blank "
            + "and exports stay on this machine."));

        _language.Items.Add("id");
        _language.Items.Add("en");
        root.Children.Add(Field("Language", _language));

        _autoStart.Content = "Start polling automatically when Revit opens";
        _autoStart.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(_autoStart);

        root.Children.Add(Hint($"Settings file: {AddinConfig.ConfigPath}"));
        root.Children.Add(Hint($"Log file: {Logger.LogPath}"));

        root.Children.Add(Buttons());
        return root;
    }

    private StackPanel Buttons()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };

        var cancel = new Button { Content = "Cancel", Padding = new Thickness(18, 6, 18, 6), IsCancel = true };
        cancel.Click += (_, _) => DialogResult = false;

        var save = new Button
        {
            Content = "Save",
            Padding = new Thickness(24, 6, 24, 6),
            Margin = new Thickness(10, 0, 0, 0),
            IsDefault = true,
        };
        save.Click += OnSave;

        row.Children.Add(cancel);
        row.Children.Add(save);
        return row;
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        FontSize = 14,
        Margin = new Thickness(0, 18, 0, 6),
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        Foreground = Subtle,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 6),
    };

    private static UIElement Field(string label, Control input, string? hint = null)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        stack.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 3) });

        input.Padding = new Thickness(6, 4, 6, 4);
        stack.Children.Add(input);

        if (hint is not null) stack.Children.Add(Hint(hint));
        return stack;
    }

    // ------------------------------------------------------------------ load

    private void LoadFromConfig()
    {
        _url.Text = _config.SupabaseUrl;
        _key.Password = _config.SupabaseKey;
        _pollInterval.Text = _config.PollingIntervalSeconds.ToString();
        _hangerFamily.Text = _config.HangerFamilyName;
        _exportDirectory.Text = _config.ExportDirectory;
        _botToken.Password = _config.TelegramBotToken;
        _sendFiles.IsChecked = _config.SendFilesToTelegram;
        _language.SelectedItem = _config.Language == "en" ? "en" : "id";
        _autoStart.IsChecked = _config.StartPollingOnLaunch;

        // The template values are placeholders, not settings. Presenting them as
        // if they were real is what sent the first attempt at "your-project".
        if (_url.Text.Contains("YOUR-PROJECT", StringComparison.OrdinalIgnoreCase)) _url.Text = string.Empty;
        if (_key.Password.StartsWith("YOUR-", StringComparison.OrdinalIgnoreCase)) _key.Password = string.Empty;

        if (!_config.IsUsable)
        {
            Say("Not configured yet. Fill in the URL and key, then test the connection.", Subtle);
        }
    }

    // ------------------------------------------------------------------- test

    private async void OnTestConnection(object sender, RoutedEventArgs e)
    {
        var url = _url.Text.Trim().TrimEnd('/');
        var key = _key.Password.Trim();

        if (url.Length == 0 || key.Length == 0)
        {
            Say("Enter the project URL and the service_role key first.", Bad);
            return;
        }
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Say("The project URL must start with https://", Bad);
            return;
        }
        // Checked before Supabase is called, because Supabase would accept it:
        // the anon key authenticates, the ping passes, and the project list
        // comes back empty because row-level security filtered it — which is
        // indistinguishable from a database that has no projects yet.
        if (SupabaseApiKey.Classify(key) == SupabaseKeyKind.Anon)
        {
            Say(SupabaseApiKey.AnonKeyAdvice, Bad);
            return;
        }

        _test.IsEnabled = false;
        Say("Connecting…", Subtle);

        try
        {
            using var client = new SupabaseClient(url, key);

            if (!await client.PingAsync().ConfigureAwait(true))
            {
                Say("Supabase did not respond. Check the URL, the key, and your network.", Bad);
                return;
            }

            // Reads the table to prove the key really works — a URL that
            // resolves and a key that is rejected both "ping" the same.
            var projects = await client
                .SelectAsync<ProjectRow>("projects", "select=id,code,name&order=code")
                .ConfigureAwait(true);

            // Zero projects is normal on a fresh database — Connect registers the
            // open model — but it is also exactly what a key without the rights
            // to read them looks like, so do not call that "connected" outright.
            Say(
                projects.Count == 0
                    ? "Connected, but no projects are visible. That is expected on a new "
                      + "database: Connect registers the open Revit model. If projects do "
                      + "exist, this key cannot read them — use the service_role (secret) key."
                    : $"Connected. {projects.Count} project(s) visible. Save, then Connect — "
                      + "pick the project in Telegram with /project.",
                projects.Count == 0 ? Subtle : Ok);
        }
        catch (SupabaseException ex)
        {
            // A 401 here is the key; anything else is usually the URL or RLS.
            Say($"Supabase refused the request ({ex.StatusCode}): {ex.Message}", Bad);
            Logger.Warn($"Settings test failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            Say(ex.Message, Bad);
            Logger.Warn($"Settings test failed: {ex.Message}");
        }
        finally
        {
            _test.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------------- save

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var url = _url.Text.Trim().TrimEnd('/');
        var key = _key.Password.Trim();

        if (url.Length == 0 || key.Length == 0)
        {
            // projectId stays optional: empty means serve every project.
            Say("The URL and key are both required before this can connect.", Bad);
            return;
        }

        // Saving it would only move the failure to the next Connect, where it
        // reads as a silent, idle queue rather than as this.
        if (SupabaseApiKey.Classify(key) == SupabaseKeyKind.Anon)
        {
            Say($"Not saved. {SupabaseApiKey.AnonKeyAdvice}", Bad);
            return;
        }

        if (!int.TryParse(_pollInterval.Text.Trim(), out var interval))
        {
            Say("Poll interval must be a whole number of seconds.", Bad);
            return;
        }

        // Baca ulang berkasnya sebelum menimpanya.
        //
        // Save() menulis seluruh objek, dan jendela ini hanya punya field untuk
        // sebagian isinya. Kunci Cloudinary — yang memang diisi dengan tangan,
        // karena tidak ada tempatnya di sini — hilang jadi string kosong setiap
        // kali tombol ini ditekan setelah berkasnya diedit di luar. Gejalanya
        // yang paling menyesatkan: file yang barusan diisi terlihat benar, lalu
        // add-in melaporkan konfigurasi yang tidak ada, dan yang dicurigai
        // adalah ketikannya.
        //
        // Di sini, bukan di Save(): yang tahu field mana milik jendela ini
        // hanyalah jendela ini, dan nilai form ditulis setelahnya supaya tetap
        // menang atas isi berkas.
        _config.ApplyFrom(AddinConfig.Load());

        _config.SupabaseUrl = url;
        _config.SupabaseKey = key;
        _config.PollingIntervalSeconds = Math.Clamp(interval, 2, 300);
        _config.HangerFamilyName = _hangerFamily.Text.Trim();
        _config.ExportDirectory = _exportDirectory.Text.Trim();
        _config.TelegramBotToken = _botToken.Password.Trim();
        _config.SendFilesToTelegram = _sendFiles.IsChecked == true;
        _config.Language = (_language.SelectedItem as string) ?? "id";
        _config.StartPollingOnLaunch = _autoStart.IsChecked == true;

        try
        {
            _config.Save();
            Logger.Info($"Settings saved to {AddinConfig.ConfigPath}");
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Say($"Could not write {AddinConfig.ConfigPath}: {ex.Message}", Bad);
            Logger.Error("Saving settings failed", ex);
        }
    }

    private void Say(string message, Brush colour)
    {
        _status.Text = message;
        _status.Foreground = colour;
    }
}
