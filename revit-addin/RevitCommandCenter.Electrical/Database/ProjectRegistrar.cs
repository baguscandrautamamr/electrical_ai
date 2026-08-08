using Newtonsoft.Json;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Database;

/// <summary>
/// Registers the open Revit model as a project, so /project in Telegram lists
/// the models an engineer is actually working on.
///
/// Projects used to exist only if someone wrote them into the database by hand,
/// which meant the names in Telegram had nothing to do with the file open on
/// screen. The add-in already knows the model's name — it may as well say so.
///
/// The file name is the identity. Two machines opening the same central model
/// register the same project rather than two competing ones; renaming the file
/// creates a new project, which is the honest reading of a renamed file.
/// </summary>
public static class ProjectRegistrar
{
    /// <summary>
    /// Upserts a project row for <paramref name="documentTitle"/> and returns
    /// its id, or null when it could not be registered.
    ///
    /// Failure is never fatal: a Revit instance that cannot register still
    /// serves commands for projects that already exist.
    /// </summary>
    public static async Task<string?> RegisterAsync(
        SupabaseClient client,
        string documentTitle,
        CancellationToken ct = default)
    {
        var name = Clean(documentTitle);
        if (name.Length == 0) return null;

        var code = ToCode(name);

        try
        {
            await client.UpsertAsync(
                "projects",
                new[] { new { code, name } },
                "code",
                ct).ConfigureAwait(false);

            var rows = await client.SelectAsync<ProjectIdRow>(
                "projects",
                $"select=id&code=eq.{Uri.EscapeDataString(code)}&limit=1",
                ct).ConfigureAwait(false);

            var id = rows.Count > 0 ? rows[0].Id : null;
            Logger.Info(id is null
                ? $"Could not register project '{name}'."
                : $"Registered project '{name}' ({code}).");
            return id;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not register project '{name}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Strips the .rvt suffix and Revit's detached/workset noise.</summary>
    private static string Clean(string title)
    {
        var name = title.Trim();
        if (name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }
        // A workshared model opens as "Model_username"; the local suffix is not
        // part of the project's identity, but stripping it reliably needs the
        // username, which we do not have here. Left alone deliberately.
        return name;
    }

    /// <summary>
    /// A short stable key for the file name. Upper-case, alphanumerics and
    /// dashes only, because it is shown in Telegram where it has to stay
    /// readable next to the full name.
    /// </summary>
    internal static string ToCode(string name)
    {
        var chars = name
            .ToUpperInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();

        var code = new string(chars);
        while (code.Contains("--", StringComparison.Ordinal))
        {
            code = code.Replace("--", "-", StringComparison.Ordinal);
        }

        code = code.Trim('-');
        return code.Length <= 40 ? code : code[..40].TrimEnd('-');
    }

    private sealed class ProjectIdRow
    {
        [JsonProperty("id")] public string? Id { get; set; }
    }
}
