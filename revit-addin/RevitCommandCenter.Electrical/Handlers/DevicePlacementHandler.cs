using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>
/// Shared skeleton for the "place N devices in a room" handlers.
///
/// Every device category resolves a room, works out placement points, drops
/// family instances, tags them with a Mark, and reports a count. Only the
/// per-category specifics differ, so those are the abstract members and
/// everything else lives here once.
/// </summary>
public abstract class DevicePlacementHandler : ICommandHandler
{
    public abstract string CommandType { get; }

    /// <summary>Result <c>kind</c>, matching the TypeScript union.</summary>
    protected abstract string ResultKind { get; }

    /// <summary>Mark prefix, e.g. "LF" for light fixtures.</summary>
    protected abstract string DeviceIdPrefix { get; }

    protected abstract BuiltInCategory Category { get; }

    /// <summary>Supabase table receiving the placed devices.</summary>
    protected abstract string TableName { get; }

    /// <summary>How many devices this command should place.</summary>
    protected abstract int ResolveCount(CommandModel command, SpatialElement room);

    /// <summary>
    /// Where they go, and what they host on.
    ///
    /// Ceiling devices return bare points; wall devices return the room-side
    /// wall face with them, which is what gets them placed on a vertical face
    /// rather than left unhosted at the right coordinates.
    /// </summary>
    protected abstract List<DevicePlacement> ResolvePlacements(
        HandlerContext context,
        CommandModel command,
        SpatialElement room,
        int count);

    /// <summary>Family type to place, when the command named none.</summary>
    protected abstract FamilySymbol? ResolveSymbol(HandlerContext context, CommandModel command);

    /// <summary>
    /// The family this command NAMED, or empty when it only hinted.
    /// </summary>
    /// <remarks>
    /// Yang membedakan keduanya bukan besar kecilnya, melainkan dari mana
    /// nilainya datang. `type=dome`, `type=smoke`, `type=receptacle` adalah
    /// terkaan sistem ini tentang apa yang dinamai sebuah kantor untuk
    /// family-nya — daftar tertutup yang menyatakan MAKSUD, dan add-in yang
    /// menerjemahkannya. Jatuh ke family pertama di kategori itu memang jawaban
    /// yang benar di situ.
    ///
    /// `family` — dan, untuk armatur, `fixture_type` — adalah nama yang dibaca
    /// seseorang dari project browser atau dipilih dari daftar yang dilaporkan
    /// add-in ini sendiri lewat /model_info. Untuk itu tidak ada yang namanya
    /// hampir cocok, dan memasang family lain sebagai gantinya adalah kegagalan
    /// yang hanya muncul di gambar yang sudah jadi.
    ///
    /// Virtual, bukan satu daftar kunci di sini: kunci mana yang berarti "nama"
    /// adalah milik perintahnya masing-masing, dan sebuah daftar terpusat akan
    /// diam-diam berbeda dari katalog pada perubahan pertama.
    /// </remarks>
    protected virtual string NamedFamily(CommandModel command) =>
        command.GetString("family").Trim();

    /// <summary>
    /// Titik-titik grid plafon untuk ruangan ini, dengan yang di luar batas
    /// ruangan sudah dibuang dan dilaporkan.
    ///
    /// Satu jalan untuk ketiga perangkat plafon — armatur, detektor, speaker —
    /// supaya penyaringan batasnya tidak hanya ada di salah satunya. Sebuah
    /// ruangan berbentuk L tidak berhenti berbentuk L untuk detektor asap.
    ///
    /// Grid yang disebut dipakai apa adanya; yang tidak disebut diturunkan dari
    /// jumlahnya. Perintah yang katalognya tidak punya kolom grid tidak pernah
    /// membawanya, jadi keduanya bisa lewat sini tanpa syarat tambahan.
    /// </summary>
    protected static List<XYZ> CeilingGridFor(
        HandlerContext context,
        CommandModel command,
        SpatialElement room,
        int count,
        double mountHeightFeet)
    {
        var stated = RevitUtils.ParseGrid(command.GetString("grid"));

        var grid = stated is not null
            ? RevitUtils.GenerateCeilingGrid(room, stated.Value.Cols, stated.Value.Rows, mountHeightFeet)
            : RevitUtils.GenerateCeilingGrid(room, count, mountHeightFeet);

        context.ReportBoundary(grid.Outside, grid.BoundaryChecked);
        return grid.Points;
    }

    /// <summary>Per-instance parameters. Base implementation sets none.</summary>
    protected virtual Dictionary<string, object> InstanceParameters(
        CommandModel command,
        int index) => new();

    /// <summary>Row written to <see cref="TableName"/> for one device.</summary>
    protected abstract object BuildRow(
        HandlerContext context,
        CommandModel command,
        SpatialElement room,
        string deviceId,
        FamilyInstance instance,
        XYZ point);

    /// <summary>Hook for extra fields on the reply (load, circuits, compliance).</summary>
    protected virtual void Decorate(
        PlacementResultDto result,
        HandlerContext context,
        CommandModel command,
        SpatialElement room,
        List<FamilyInstance> placed)
    {
    }

    public virtual CommandResult Execute(HandlerContext context, CommandModel command)
    {
        // /equip_room menjalankan delapan perintah ini lewat satu context.
        context.ResetBoundary();

        var roomName = command.GetString("room");
        var lookup = RevitUtils.ResolveRoom(context.Doc, roomName);
        if (lookup.Room is null)
        {
            // A missing or ambiguous room will fail identically on every retry.
            // The lookup's own wording says which of the two it was, and what to
            // type instead — silently picking a room that merely starts with
            // what was typed is how "meeting 1" ended up equipping MEETING 2.
            return CommandResult.Fail(lookup.Problem ?? $"No room or space called '{roomName}' in the model.", retryable: false);
        }

        var room = lookup.Room;

        // A family named in the command is obeyed or refused — never quietly
        // swapped. `family` does not arrive from a guess: it is picked from the
        // list this add-in itself reported through /model_info, so a name that
        // matches nothing means the model changed or the name is wrong, and
        // placing a different family instead is the failure that only shows up
        // in the drawing. The per-category hints below (type, camera_type,
        // fixture_type) stay lenient, because those ARE guesses at what an
        // office calls its families.
        var namedFamily = NamedFamily(command);

        var symbol = namedFamily.Length > 0
            ? RevitUtils.FindNamedSymbol(context.Doc, Category, namedFamily)
            : ResolveSymbol(context, command);

        if (symbol is null)
        {
            return CommandResult.Fail(
                namedFamily.Length > 0
                    ? RevitUtils.NoSuchFamily(context.Doc, Category, namedFamily)
                    : $"No suitable family found for {CommandType} in category {Category}.",
                retryable: false);
        }

        var count = ResolveCount(command, room);
        if (count <= 0)
        {
            return CommandResult.Fail("Resolved device count is zero; nothing to place.", retryable: false);
        }

        var placements = ResolvePlacements(context, command, room, count);

        // Pertanyaan "berapa dari pintu" hanya berlaku untuk perintah yang
        // memang menempatkan relatif terhadap pintu. Untuk yang lain medannya
        // tidak dikirim sama sekali — nol di situ akan terbaca sebagai "tepat di
        // pintu", bukan sebagai "tidak ditanyakan".
        var doorAware = placements.Any(p => p.DoorDistanceFeet is not null || p.BesideDoor);
        if (placements.Count == 0)
        {
            return CommandResult.Fail(
                $"Could not work out placement points inside room '{room.Name}'.",
                retryable: false);
        }

        var level = RevitUtils.LevelOf(context.Doc, room);
        var sequence = RevitUtils.NextDeviceSequence(context.Doc, Category, DeviceIdPrefix);
        // Marks already in the model, so a hand-marked fixture cannot collide
        // with a generated id. Grows as ids are handed out, which also keeps
        // this run's own ids apart from each other.
        var takenMarks = RevitUtils.ExistingMarks(context.Doc, Category);

        var placed = new List<FamilyInstance>();
        var deviceIds = new List<string>();

        // Diminta uji coba: dijalankan sungguhan lalu dibatalkan, bukan
        // ditebak. Lihat pembatalannya di bawah.
        var dryRun = command.GetBool("dry_run");

        using var transaction = new Transaction(context.Doc, $"Place {ResultKind} in {room.Name}");
        transaction.Start();

        try
        {
            if (!symbol.IsActive)
            {
                symbol.Activate();
                context.Doc.Regenerate();
            }

            for (var i = 0; i < placements.Count; i++)
            {
                var placement = placements[i];
                var instance = Create(context.Doc, symbol, placement, level);

                var deviceId = RevitUtils.NextFreeDeviceId(DeviceIdPrefix, sequence + i, takenMarks);
                ParameterMapper.TrySetParameter(instance, "Mark", deviceId);

                var extra = InstanceParameters(command, i);
                if (extra.Count > 0) ParameterMapper.TrySetParameters(instance, extra);

                placed.Add(instance);
                deviceIds.Add(deviceId);

                context.Persist(
                    TableName,
                    BuildRow(context, command, room, deviceId, instance, placement.Point));
            }

            // Uji coba: dijalankan sungguhan, lalu dibatalkan.
            //
            // Bukan disimulasikan — yang menjawab "berapa yang muat" adalah
            // Revit, bukan perkiraan kode ini. Ruangan yang bentuknya aneh,
            // plafon yang tidak rata, family yang menolak dipasang di titik
            // tertentu: semua itu hanya terlihat kalau penempatannya benar-benar
            // terjadi. Yang dibatalkan cuma penulisannya ke model.
            if (dryRun)
            {
                transaction.RollBack();

                // Baris yang sudah disiapkan untuk Supabase ikut dibuang.
                // Menyimpannya berarti database mencatat perangkat yang tidak
                // ada di model mana pun — dan perintah berikutnya membacanya
                // sebagai kenyataan.
                context.PendingRows.Clear();

                Logger.Info($"{CommandType}: uji coba, {placed.Count} perangkat dibatalkan.");
            }
            else
            {
                transaction.Commit();
            }
        }
        catch (Exception ex)
        {
            transaction.RollBack();
            Logger.Error($"{CommandType} rolled back", ex);
            return CommandResult.FromException(ex);
        }

        var result = new PlacementResultDto
        {
            Kind = ResultKind,
            Room = room.Name,
            DevicesPlaced = placed.Count,
            DryRun = dryRun,
            // Id elemen tidak ikut dilaporkan pada uji coba: elemennya sudah
            // tidak ada setelah transaksinya dibatalkan, dan Id yang menunjuk ke
            // ketiadaan lebih buruk daripada tidak ada Id sama sekali.
            DeviceIds = dryRun ? new List<string>() : deviceIds,
            // Di sini, sekali, untuk kedelapan kategori: yang dilaporkan adalah
            // simbol yang dipakai, bukan nama yang diminta. Keduanya berbeda
            // persis pada saat perbedaannya paling mahal — ketika nama yang
            // diminta tidak ada di model dan pencariannya jatuh ke tipe pertama.
            FamilyUsed = RevitUtils.DescribeSymbol(symbol),
            // Selisih antara yang diminta dan yang berdiri, dengan sebabnya.
            // Keduanya dari context, bukan dari field di handler ini: satu
            // instance handler melayani setiap perintah yang masuk, jadi field
            // di sini akan membawa angka perintah sebelumnya.
            Requested = placed.Count == count ? null : (int?)count,
            // Diambil dari `placements`, bukan dari `placed`: yang tahu sebuah
            // titik ditentukan relatif terhadap pintu adalah yang MENENTUKAN
            // titiknya, dan itu sudah lewat pada saat instance-nya dibuat.
            BesideDoor = doorAware ? placements.Count(p => p.BesideDoor) : null,
            AwayFromDoor = doorAware ? placements.Count(p => !p.BesideDoor) : null,
            DoorDistanceMm = doorAware
                ? placements
                    .Take(placed.Count)
                    .Select(p => Math.Round(RevitUnits.FeetToMm(p.DoorDistanceFeet ?? 0), 0))
                    .ToList()
                : null,
            OutsideBoundary = context.OutsideBoundary > 0 ? context.OutsideBoundary : (int?)null,
            BoundaryChecked = context.BoundaryChecked,
        };

        // Dilewati pada uji coba: elemennya sudah tidak ada.
        //
        // Decorate membaca beban, sirkuit, dan jarak DARI instance yang barusan
        // dipasang. Setelah transaksinya dibatalkan, instance itu tidak lagi
        // valid — membacanya bukan menghasilkan angka nol, melainkan pengecualian
        // dari Revit di tengah perintah yang sebenarnya berhasil.
        //
        // Yang hilang cuma hiasannya. Jumlah yang muat — satu-satunya hal yang
        // ditanyakan sebuah uji coba — sudah dihitung sebelum pembatalan.
        if (!dryRun) Decorate(result, context, command, room, placed);

        // Penempatan berbasis pintu yang GAGAL dikatakan, bukan dibiarkan
        // terlihat seperti berhasil.
        //
        // Ini yang dilaporkan: saklar berdiri 3.570 mm dari pintu, perintahnya
        // melaporkan sukses, dan satu-satunya jejak kegagalannya adalah sebaris
        // Logger.Debug di berkas log pada PC yang menjalankan Revit. Jumlahnya
        // cocok — satu diminta, satu terpasang — jadi catatan "kurang dari yang
        // diminta" di bawah pun tidak menyala.
        if (result.AwayFromDoor is > 0)
        {
            var far = result.DoorDistanceMm?.LastOrDefault();
            var howFar = far is > 0 ? $", yang terjauh {far:F0} mm dari pintu" : "";

            result.Compliance ??= new List<ComplianceCheckDto>();
            result.Compliance.Insert(0, ComplianceCheckDto.Of(
                "compliance.beside_door",
                false,
                $"{result.AwayFromDoor} dari {placed.Count} tidak bisa ditempatkan di samping pintu "
                + $"dan disebar di keliling '{room.Name}'{howFar}"));
        }

        // Ruangan yang batasnya tidak bisa diuji dikatakan lewat daftar
        // kepatuhan, bukan lewat catatan: PlacementResultDto tidak punya
        // `notes`, dan daftar kepatuhan justru dibaca kedua sisi — panel
        // website maupun balasan Telegram.
        //
        // Bukan sekadar keterangan. Batas yang tidak menutup berarti setiap
        // armatur di perintah ini dipasang tanpa ada yang memastikan ia berada
        // di ruangan yang diminta, dan itu masalah pemodelan yang hanya bisa
        // diperbaiki orangnya — sesudah ada yang menyebutkannya.
        if (context.BoundaryChecked is false)
        {
            result.Compliance ??= new List<ComplianceCheckDto>();
            result.Compliance.Insert(0, ComplianceCheckDto.Of(
                "compliance.room_boundary",
                false,
                $"'{room.Name}' belum terkurung dinding, jadi tidak ada titik yang bisa diuji "
                + "terhadap batasnya — gridnya dipasang utuh"));
        }

        // Placing fewer than were asked for is a fact about the room, and it
        // has to be said. Reporting five where six were asked for, with nothing
        // to mark it, is how an engineer finds out from the drawing instead.
        //
        // Sebabnya disebut, bukan diterka satu untuk semua. Dulu kalimatnya
        // selalu menyalahkan dinding yang penuh pintu dan jendela — benar untuk
        // stop kontak, dan menyesatkan untuk armatur plafon, yang justru
        // berkurang karena sebagian gridnya jatuh di ruangan sebelah. Yang
        // membaca kalimat yang salah sebabnya akan memperbaiki hal yang salah.
        if (placed.Count < count)
        {
            var reason = context.OutsideBoundary > 0
                ? $"{context.OutsideBoundary} titik jatuh di luar batas '{room.Name}' dan dibuang"
                : $"no wall clear of the doors and windows in '{room.Name}' for the rest";

            result.Compliance ??= new List<ComplianceCheckDto>();
            result.Compliance.Insert(0, ComplianceCheckDto.Of(
                "compliance.requested_count",
                false,
                $"asked for {count}, placed {placed.Count} — {reason}"));
        }

        Logger.Info($"{CommandType}: placed {placed.Count} device(s) in {room.Name}");
        return CommandResult.Ok(result);
    }

    /// <summary>
    /// Creates one instance, preferring the most attached form available.
    ///
    /// Face first — this is Revit's "Place on Vertical Face", and it is how a
    /// receptacle, switch, LAN or telephone outlet is placed by hand. It needs a
    /// face-based family, so a project whose families are not face-based falls
    /// back to hosting on the wall itself, and then to an unhosted instance at
    /// the same point. Each step down is logged: the device appears either way,
    /// and the log is what explains why one drawing's outlets move with their
    /// wall and another's do not.
    /// </summary>
    private static FamilyInstance Create(
        Document doc,
        FamilySymbol symbol,
        DevicePlacement placement,
        Level? level)
    {
        if (placement.FaceReference is not null && placement.ReferenceDirection is not null)
        {
            try
            {
                return doc.Create.NewFamilyInstance(
                    placement.FaceReference,
                    placement.Point,
                    placement.ReferenceDirection,
                    symbol);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                Logger.Debug(
                    $"'{symbol.Name}' is not face-based ({ex.Message}); hosting on the wall instead.");
            }
        }

        if (placement.Host is not null)
        {
            try
            {
                return doc.Create.NewFamilyInstance(
                    placement.Point,
                    symbol,
                    placement.Host,
                    level,
                    StructuralType.NonStructural);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                Logger.Debug($"'{symbol.Name}' would not host on the wall ({ex.Message}); placing free.");
            }
        }

        return level is not null
            ? doc.Create.NewFamilyInstance(placement.Point, symbol, level, StructuralType.NonStructural)
            : doc.Create.NewFamilyInstance(placement.Point, symbol, StructuralType.NonStructural);
    }

    /// <summary>
    /// Splits a total load across circuits at a breaker rating, keeping each
    /// circuit at or under 80% continuous load as the code requires.
    /// </summary>
    protected static int CircuitsFor(double totalLoadW, double breakerAmps, double voltage)
    {
        if (breakerAmps <= 0 || voltage <= 0) return 1;

        var usableWatts = breakerAmps * voltage * 0.8;
        return usableWatts <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(totalLoadW / usableWatts));
    }
}
