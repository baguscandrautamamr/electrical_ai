using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using RevitCommandCenter.Electrical.Config;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.UI;

/// <summary>
/// The website's own chat, inside Revit.
///
/// A browser rather than a rebuilt chat, and that is the whole design decision.
/// The prompt, the command catalogue, the id validation, the room-contents check
/// — all of it already exists on the website and all of it is one implementation.
/// A native WPF chat would be a second copy of every one of those rules, and this
/// project has been bitten more than once by two copies of a rule drifting apart
/// silently. Here there is nothing to drift: it is the same page.
///
/// The commands still travel the normal way — website to Supabase, add-in polls
/// them back. That is a round trip to the cloud to reach the machine the person
/// is sitting at, and it is deliberate: the queue is what /history reads and what
/// ai_events records, and a shortcut straight into CommandProcessor would leave
/// both blind.
/// </summary>
public partial class ChatPanel : UserControl
{
    private bool _started;

    public ChatPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => _ = StartAsync();
    }

    /// <summary>
    /// Where WebView2 keeps its profile — and therefore the login session.
    ///
    /// Load-bearing twice over. Without an explicit folder WebView2 writes beside
    /// the host executable, which here is Revit's own install directory under
    /// Program Files: not writable, so the control fails to start at all.
    ///
    /// And because the folder is stable, the Supabase session survives Revit
    /// being closed. Signing in on every launch is the kind of friction that
    /// makes a panel go unused however well it works.
    /// </summary>
    private static string ProfileDirectory =>
        Path.Combine(AddinConfig.ConfigDirectory, "webview");

    private async Task StartAsync()
    {
        if (_started) return;
        _started = true;

        var url = App.Current?.Config.ChatUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowNotice(
                "Alamat website belum diisi",
                "Buka config.json add-in, isi \"website_url\" dengan alamat Revit Command Center "
                + "(mis. https://namamu.vercel.app), lalu tutup dan buka lagi panel ini.\n\n"
                + $"Berkasnya: {AddinConfig.ConfigPath}",
                canRetry: true);
            return;
        }

        try
        {
            Directory.CreateDirectory(ProfileDirectory);

            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null, userDataFolder: ProfileDirectory);

            await Browser.EnsureCoreWebView2Async(environment);

            // Revit's own menus are the ones people expect here; a browser
            // context menu offering "view source" inside a CAD application is
            // noise at best.
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;

            // A page that fails to load must say so. Left alone, WebView2 shows
            // its own error page, which talks about DNS and proxies to somebody
            // whose actual problem is that the laptop is offline.
            Browser.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess) return;

                ShowNotice(
                    "Website tidak bisa dibuka",
                    $"Percobaan membuka {url} gagal ({e.WebErrorStatus}). Periksa koneksi internet, "
                    + "lalu tekan Coba lagi.",
                    canRetry: true);
            };

            Browser.Source = new Uri(url);
            ShowBrowser();

            Logger.Info($"Chat panel opened at {url}");
        }
        catch (Exception ex)
        {
            // The overwhelmingly common cause is the WebView2 runtime missing.
            // Naming it beats an exception message nobody can act on — and on
            // Windows 10/11 it is usually already there, which makes the rare
            // case all the more confusing when it happens.
            ShowNotice(
                "Panel chat tidak bisa dijalankan",
                "Kemungkinan besar WebView2 Runtime belum terpasang di komputer ini. "
                + "Unduh \"Microsoft Edge WebView2 Runtime\" dari situs Microsoft, pasang, "
                + "lalu buka ulang Revit.\n\n"
                + $"Pesan aslinya: {ex.Message}",
                canRetry: true);

            Logger.Error($"Chat panel could not start: {ex}");
        }
    }

    private void ShowBrowser()
    {
        Notice.Visibility = Visibility.Collapsed;
        Browser.Visibility = Visibility.Visible;
    }

    private void ShowNotice(string title, string body, bool canRetry)
    {
        NoticeTitle.Text = title;
        NoticeBody.Text = body;
        RetryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;

        Browser.Visibility = Visibility.Collapsed;
        Notice.Visibility = Visibility.Visible;
    }

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        // Config is re-read, not reused: the usual reason for pressing this is
        // that website_url was just filled in, and a retry that keeps the config
        // loaded at startup would report the same problem forever.
        App.Current?.ReloadConfig();

        _started = false;
        ShowNotice("Memuat…", string.Empty, canRetry: false);
        _ = StartAsync();
    }
}
