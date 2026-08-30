using Newtonsoft.Json.Linq;
using RevitCommandCenter.Electrical.Handlers;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Queue;

/// <summary>
/// Routes a command to its handler.
///
/// The registry is built once and shared; handlers are stateless apart from
/// per-execution locals, so a single instance each is fine.
/// </summary>
public sealed class CommandProcessor
{
    private readonly Dictionary<string, ICommandHandler> _handlers;

    public CommandProcessor()
    {
        var handlers = new List<ICommandHandler>
        {
            new LightingHandler(),
            new LightingDeviceHandler(),
            new ReceptacleHandler(),
            new CableTrayHandler(),
            new AddHangersHandler(),
            new FireAlarmHandler(),
            new TelephoneHandler(),
            new LANHandler(),
            new SecurityHandler(),
            new CommunicationHandler(),
            new ExportHandler(),
            new ExportCadHandler(),
            new PrintPdfHandler(),
            new DeleteDevicesHandler(),
            new QueryHandler(),
            new InspectHandler(),
            new ModelInfoHandler(),
            new ImportExcelHandler(),
            new ImportTableHandler(),
            new ShowElementHandler(),
            new ElectricalLoadsHandler(),
            new PanelScheduleHandler(),
            new CircuitBalanceHandler(),
            new SectionBoxHandler(),
            new ConnectCircuitHandler(),
        };

        _handlers = handlers.ToDictionary(
            handler => handler.CommandType,
            StringComparer.OrdinalIgnoreCase);

        // /list_sheets is the same read as /query what=sheet, under the name
        // people actually ask it by.
        _handlers[QueryHandler.SheetListCommandType] =
            handlers.OfType<QueryHandler>().First();

        // These orchestrate the others, so they are registered last with a
        // read-only view of the registry built so far.
        var orchestrated = new EquipRoomHandler(_handlers);
        _handlers[orchestrated.CommandType] = orchestrated;

        var modify = new ModifyDevicesHandler(_handlers);
        _handlers[modify.CommandType] = modify;
    }

    public IReadOnlyDictionary<string, ICommandHandler> Handlers => _handlers;

    public ICommandHandler? Resolve(string commandType) =>
        _handlers.TryGetValue(commandType, out var handler) ? handler : null;

    /// <summary>
    /// Menempelkan nama dokumen yang benar-benar dikerjakan ke sebuah hasil.
    /// </summary>
    /// <remarks>
    /// Di sini, sekali, bukan di dua puluh lima handler. Yang dibutuhkan
    /// website cuma satu medan, dan sebuah medan yang harus diingat setiap kali
    /// sebuah handler baru ditulis adalah medan yang akan hilang dari salah
    /// satunya.
    ///
    /// Apa gunanya: panel perintah menampilkan nama file .rvt yang sedang
    /// dibuka Revit, dan berganti file terjadi DI REVIT — tidak ada satu pun
    /// kejadian di sisi website yang menandainya. Sebelum ini, satu-satunya
    /// cara mengetahuinya adalah bertanya lagi lewat /model_info, yaitu satu
    /// baris antrean lagi yang harus diambil add-in. Medan ini menjawab
    /// pertanyaan yang sama tanpa satu baris pun tambahan: hasil perintah yang
    /// sudah diminta orangnya ikut menyebutkan dokumen tempat ia dikerjakan,
    /// dan begitu nama itu berbeda dari yang tampil, yang tampil sudah pasti
    /// salah — bukan mungkin salah, dan bukan salah semenit lagi.
    ///
    /// <c>doc.Title</c>, sama persis dengan yang dikirim <c>/model_info</c>
    /// sebagai <c>title</c>. Sama persis itu syaratnya: website membandingkan
    /// keduanya sebagai string, dan dua ejaan untuk satu file akan terbaca
    /// sebagai dokumen yang berganti pada setiap perintah.
    ///
    /// Bentuk hasilnya tidak diubah. Hasil yang bukan objek JSON dibiarkan apa
    /// adanya — membungkusnya demi satu medan tambahan akan memindahkan seluruh
    /// isinya satu tingkat ke dalam, dan yang membacanya di seberang sana tidak
    /// diberi tahu.
    /// </remarks>
    private static CommandResult WithDocument(CommandResult result, HandlerContext context)
    {
        // Hasil yang gagal tidak pernah ditulis ke result_json sama sekali
        // (lihat CommandQueueRepository.ReportAsync), jadi tidak ada tempat
        // untuk menaruhnya.
        if (!result.Success || result.Data is null) return result;

        try
        {
            var title = context.Doc.Title;
            if (string.IsNullOrWhiteSpace(title)) return result;

            if (JToken.FromObject(result.Data) is not JObject payload) return result;
            payload["document"] = title;

            return new CommandResult
            {
                Success = result.Success,
                Data = payload,
                Error = result.Error,
                Stack = result.Stack,
                Retryable = result.Retryable,
                ExecutionTimeMs = result.ExecutionTimeMs,
            };
        }
        catch (Exception ex)
        {
            // Sebuah nama dokumen bernilai lebih kecil daripada hasil yang
            // ditempelinya. Perintahnya berhasil; yang gagal cuma hiasannya.
            Logger.Warn($"Could not attach the document name to a result: {ex.Message}");
            return result;
        }
    }

    /// <summary>
    /// Executes a command. Must be called on Revit's main thread.
    ///
    /// Never throws: an unhandled exception here would escape into Revit's
    /// external-event pump and take the add-in down, so everything becomes a
    /// failed <see cref="CommandResult"/> instead.
    /// </summary>
    public CommandResult Execute(HandlerContext context, CommandModel command)
    {
        var handler = Resolve(command.CommandType);
        if (handler is null)
        {
            return CommandResult.Fail(
                $"No handler registered for command type '{command.CommandType}'.",
                retryable: false);
        }

        var startedAt = DateTime.UtcNow;

        try
        {
            Logger.Info($"Executing {command.CommandType} ({command.Id})");
            var result = WithDocument(handler.Execute(context, command), context);
            result.ExecutionTimeMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            return result;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException ex)
        {
            // Revit API errors are usually about model state, so retrying the
            // same command against the same model will fail the same way.
            Logger.Error($"Revit API error in {command.CommandType}", ex);
            return new CommandResult
            {
                Success = false,
                Error = $"Revit API error: {ex.Message}",
                Stack = ex.ToString(),
                Retryable = false,
                ExecutionTimeMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds,
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Unhandled error in {command.CommandType}", ex);
            return new CommandResult
            {
                Success = false,
                Error = ex.Message,
                Stack = ex.ToString(),
                Retryable = true,
                ExecutionTimeMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds,
            };
        }
    }
}
