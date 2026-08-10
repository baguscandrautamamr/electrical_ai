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
            FamilyTypes = FamilyTypesByCategory(doc),
            Rooms = RoomNames(doc),
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
    /// <summary>
    /// Kategori yang punya field "tipe" di form website, dan nama yang dipakai
    /// katalog perintah untuk masing-masing.
    /// </summary>
    private static readonly (string Key, BuiltInCategory Category)[] TypeCategories =
    {
        ("lighting", BuiltInCategory.OST_LightingFixtures),
        ("lighting_device", BuiltInCategory.OST_LightingDevices),
        ("receptacle", BuiltInCategory.OST_ElectricalFixtures),
        ("fire_alarm", BuiltInCategory.OST_FireAlarmDevices),
        ("telephone", BuiltInCategory.OST_TelephoneDevices),
        ("lan", BuiltInCategory.OST_DataDevices),
        ("security", BuiltInCategory.OST_SecurityDevices),
        ("communication", BuiltInCategory.OST_CommunicationDevices),
        ("cable_tray", BuiltInCategory.OST_CableTray),
    };

    /// <summary>
    /// Nama tipe family yang benar-benar ada di model, per kategori.
    ///
    /// Field seperti "Tipe armatur" sebelumnya kolom teks kosong: orangnya
    /// mengetik nama family dari ingatan, dan salah satu huruf membuat
    /// perintahnya gagal setelah menunggu Revit menjawab. Nama-nama itu ada di
    /// model dan tidak ada di tempat lain — hanya add-in yang bisa membacanya.
    ///
    /// Format "Family: Type", sama dengan yang terlihat di Project Browser dan
    /// di Type Selector, supaya yang dipilih di website terbaca sama dengan
    /// yang dilihat di Revit.
    /// </summary>
    private static Dictionary<string, List<string>> FamilyTypesByCategory(Document doc)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, category) in TypeCategories)
        {
            var names = new FilteredElementCollector(doc)
                .OfCategory(category)
                .WhereElementIsElementType()
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Select(symbol => $"{symbol.FamilyName}: {symbol.Name}")
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (names.Count > 0) result[key] = names;
        }

        return result;
    }

    /// <summary>
    /// Nama ruangan di model.
    ///
    /// Ikut di sini, bukan menunggu perintah query terpisah: setiap perintah
    /// adalah satu putaran antrean, dan nama ruangan dibutuhkan pada form yang
    /// sama dengan nama tipe. Ruangan tak bernama dibuang — ia tidak bisa
    /// disebut dalam sebuah perintah, jadi menawarkannya cuma memberi pilihan
    /// yang pasti gagal.
    /// </summary>
    private static List<string> RoomNames(Document doc) =>
        new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .ToElements()
            .Select(room => room.Name?.Trim() ?? string.Empty)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .ToList();

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
