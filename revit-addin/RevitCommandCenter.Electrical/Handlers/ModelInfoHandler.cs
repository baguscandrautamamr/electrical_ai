using Autodesk.Revit.DB;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>
/// Reports which model is open and what it can be printed and exported with.
///
/// Three questions in one command, because each one costs a queue round trip
/// and they are always asked together. The website shows the open file's name
/// so the person sending a command can see it is aimed at the model in front of
/// them; it fills the print and CAD dropdowns from the same answer.
///
/// The setups are the ones saved in the model, not a list this system invented.
/// An office that has spent years settling on how its drawings print does not
/// want a second, nearly-right set of options — it wants the ones already in
/// the Print Setup and DWG Export Setup dialogs.
///
/// Opens no transaction: everything here is a read.
/// </summary>
public sealed class ModelInfoHandler : ICommandHandler
{
    public string CommandType => "model_info";

    public CommandResult Execute(HandlerContext context, CommandModel command)
    {
        var doc = context.Doc;

        var sheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Count(sheet => !sheet.IsPlaceholder && sheet.CanBePrinted);

        var result = new ModelInfoDto
        {
            // Title is the file name without its extension, and is what the
            // title bar shows. PathName is empty until the model has been saved.
            Title = doc.Title,
            Path = string.IsNullOrWhiteSpace(doc.PathName) ? null : doc.PathName,
            IsWorkshared = doc.IsWorkshared,
            PrintableSheets = sheets,
            PrintSetups = Names<PrintSetting>(doc),
            CadSetups = Names<ExportDWGSettings>(doc),
        };

        Logger.Info(
            $"model_info: {result.Title}, {sheets} sheet(s), "
            + $"{result.PrintSetups.Count} print setup(s), {result.CadSetups.Count} CAD setup(s)");

        return CommandResult.Ok(result);
    }

    /// <summary>
    /// Names of every saved setup of one kind, in the order a person reading a
    /// dropdown would expect.
    ///
    /// A model with none is normal — Revit only creates these once somebody
    /// saves one — so an empty list is an answer, not a failure. The website
    /// falls back to Revit's own defaults when it gets one.
    /// </summary>
    private static List<string> Names<T>(Document doc) where T : Element =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(T))
            .Cast<T>()
            .Select(element => element.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
