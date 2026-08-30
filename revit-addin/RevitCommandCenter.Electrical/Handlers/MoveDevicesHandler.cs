using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>
/// Menggeser perangkat yang SUDAH terpasang ke tempat yang seharusnya.
/// </summary>
/// <remarks>
/// Ada karena satu-satunya cara membetulkan penempatan yang meleset sebelumnya
/// adalah <c>/modify_devices</c>, dan itu menghapus lalu memasang ulang. Untuk
/// tata letak yang memang salah bentuknya, itu benar. Untuk sebuah saklar yang
/// berdiri 3.570 mm dari pintu padahal seharusnya 300 mm, itu terlalu mahal:
/// yang ikut hilang bersamanya adalah Mark-nya, sirkuit yang sudah
/// menyambungnya, tag yang menempel padanya, dan setiap penyesuaian yang sudah
/// dikerjakan orang di atasnya. Perangkatnya sendiri sudah benar — yang salah
/// cuma koordinatnya.
///
/// Maka perintah ini menggeser, bukan mengganti. Elemennya tetap elemen yang
/// sama; yang berubah cuma di mana ia berdiri.
///
/// DIKERJAKAN LALU DIUKUR, bukan diperkirakan. Revit mengekang perpindahan
/// sebuah instance yang menempel pada muka dinding: geseran yang keluar dari
/// muka itu tidak terjadi seperti yang diminta, dan tidak selalu melempar.
/// Jadi jarak akhirnya dibaca ULANG dari model sesudah geseran commit, dan yang
/// dilaporkan adalah angka itu — bukan angka yang diminta. Sebuah perintah yang
/// melaporkan "300 mm" karena itu yang diminta adalah persis bentuk kegagalan
/// yang perintah ini dibuat untuk memperbaiki.
/// </remarks>
public sealed class MoveDevicesHandler : ICommandHandler
{
    public string CommandType => "move_devices";

    /// <summary>Selisih yang masih dianggap sudah benar, dalam kaki (±20 mm).</summary>
    private static readonly double ToleranceFeet = RevitUnits.MToFeet(0.02);

    /// <summary>Jarak bebas dari tepi bukaan, sama dengan yang dipakai penempatan.</summary>
    private static readonly double ClearanceFeet = RevitUnits.MToFeet(0.15);

    public CommandResult Execute(HandlerContext context, CommandModel command)
    {
        var what = command.GetString("what", "lighting_device").ToLowerInvariant();
        if (!DeleteDevicesHandler.Categories.TryGetValue(what, out var category))
        {
            return CommandResult.Fail(
                $"'{what}' bukan kategori yang bisa digeser. Pilih salah satu: "
                + string.Join(", ", DeleteDevicesHandler.Categories.Keys),
                retryable: false);
        }

        // Satu-satunya acuan yang didukung sekarang. Dinyatakan sebagai argumen,
        // bukan diandaikan, supaya acuan berikutnya (jendela, sudut ruangan,
        // panel) menambah nilai di sini alih-alih mengubah arti perintahnya.
        var to = command.GetString("to", "door").ToLowerInvariant();
        if (to != "door")
        {
            return CommandResult.Fail(
                $"'{to}' belum didukung sebagai acuan. Yang ada sekarang: door.",
                retryable: false);
        }

        var roomName = command.GetString("room");
        var lookup = RevitUtils.ResolveRoom(context.Doc, roomName);
        if (lookup.Room is null)
        {
            return CommandResult.Fail(
                lookup.Problem ?? $"Tidak ada ruangan bernama '{roomName}' di model.",
                retryable: false);
        }

        var room = lookup.Room;
        var perimeter = RoomPerimeter.Of(room);
        if (perimeter is null || perimeter.Total <= 0)
        {
            return CommandResult.Fail(
                $"Batas ruangan '{room.Name}' tidak bisa dibaca, jadi tidak ada yang bisa dijadikan acuan. "
                + "Biasanya ruangannya belum terkurung dinding.",
                retryable: false);
        }

        var doors = perimeter.Doors.ToList();
        if (doors.Count == 0)
        {
            // Sebab yang paling sering, dan yang paling bisa ditindaklanjuti:
            // pintunya ADA di gambar tapi tidak menempel di dinding batas
            // ruangan ini, jadi peta batasnya tidak melihatnya. Disebut apa
            // adanya, beserta jalan keluarnya.
            return CommandResult.Fail(
                $"Tidak ada pintu di batas ruangan '{room.Name}'. Kalau pintunya ada di gambar, "
                + "berarti ia tidak ter-host di dinding batas ruangan ini — sebutkan id-nya dengan "
                + "door_id=<id> supaya tidak perlu dicari lewat batas ruangan.",
                retryable: false);
        }

        var offsetMm = command.GetDouble("offset", 300);
        var offsetFeet = RevitUnits.MToFeet(Math.Max(offsetMm, 0) / 1000.0);

        var chosen = ChooseDoor(command, doors);
        if (chosen.Problem is { } why) return CommandResult.Fail(why, retryable: false);

        var marks = ParseMarks(command.GetString("marks"));
        var dryRun = command.GetBool("dry_run");

        var devices = new FilteredElementCollector(context.Doc)
            .OfCategory(category)
            .WhereElementIsNotElementType()
            .ToElements()
            .Where(element => marks is null
                ? DeleteDevicesHandler.InRoom(element, room)
                : marks.Contains(ParameterMapper.GetStringParameter(element, "Mark")))
            .Where(element => element.Location is LocationPoint)
            .ToList();

        if (devices.Count == 0)
        {
            // Bukan galat: ruangan yang memang belum berisi menjawab pertanyaan
            // yang diajukan, dan mengatakannya lebih baik daripada galat yang
            // harus ditafsirkan orangnya.
            return CommandResult.Ok(new MoveResultDto
            {
                Room = room.Name,
                What = what,
                To = to,
                OffsetMm = offsetMm,
                DevicesMoved = 0,
                Notes = new List<string> { "move.nothing_found" },
            });
        }

        return Move(context, room, perimeter, devices, chosen.Doors!, offsetFeet, offsetMm, what, to, dryRun);
    }

    private readonly record struct DoorChoice(List<RoomPerimeter.Opening>? Doors, string? Problem);

    /// <summary>
    /// Pintu yang jadi acuan: yang disebut id-nya, atau seluruhnya.
    /// </summary>
    /// <remarks>
    /// `door_id` ada untuk keadaan yang justru paling sering bikin penempatan
    /// meleset: ruangan dengan beberapa pintu, dan yang dimaksud orangnya bukan
    /// yang terdekat. Tanpa itu satu-satunya cara menyatakannya adalah menggeser
    /// sendiri di Revit.
    /// </remarks>
    private static DoorChoice ChooseDoor(CommandModel command, List<RoomPerimeter.Opening> doors)
    {
        var raw = command.GetString("door_id").Trim();
        if (raw.Length == 0) return new DoorChoice(doors, null);

        if (!long.TryParse(raw, out var wanted))
        {
            return new DoorChoice(null, $"door_id '{raw}' bukan angka.");
        }

        var match = doors.FirstOrDefault(door => door.Id.Value == wanted);
        if (match is null)
        {
            var available = string.Join(", ", doors.Select(door => door.Id.Value));
            return new DoorChoice(
                null,
                $"Pintu {wanted} tidak ada di batas ruangan ini. Yang ada: {available}.");
        }

        return new DoorChoice(new List<RoomPerimeter.Opening> { match }, null);
    }

    private static HashSet<string>? ParseMarks(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var marks = raw
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(mark => mark.Trim())
            .Where(mark => mark.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return marks.Count > 0 ? marks : null;
    }

    private static CommandResult Move(
        HandlerContext context,
        SpatialElement room,
        RoomPerimeter perimeter,
        List<Element> devices,
        List<RoomPerimeter.Opening> doors,
        double offsetFeet,
        double offsetMm,
        string what,
        string to,
        bool dryRun)
    {
        var doc = context.Doc;
        var moved = new List<string>();
        var already = new List<string>();
        var failures = new List<string>();
        var after = new List<double>();

        using var transaction = new Transaction(doc, $"Move {what} in {room.Name}");
        transaction.Start();

        try
        {
            foreach (var device in devices)
            {
                var name = ParameterMapper.GetStringParameter(device, "Mark");
                if (string.IsNullOrWhiteSpace(name)) name = device.Id.ToString();

                if (device.Location is not LocationPoint at) continue;

                // Pintu terdekat DARI PERANGKAT ITU, bukan pintu pertama:
                // ruangan dengan dua pintu punya dua saklar, dan masing-masing
                // milik pintunya sendiri.
                var target = NearestTarget(perimeter, doors, at.Point, offsetFeet);
                if (target is null)
                {
                    failures.Add(
                        $"{name}: tidak ada dinding kosong {offsetMm:F0} mm dari pintu mana pun");
                    continue;
                }

                var before = at.Point;
                // Hanya mendatar. Ketinggian pasang adalah keputusan tersendiri,
                // dan sebuah perintah yang membetulkan jarak ke pintu tidak
                // punya urusan menurunkan saklar dari 1.200 mm.
                // `target` sudah dipastikan bukan null di atas. Tidak pakai
                // `.Value`: XYZ adalah kelas, jadi `XYZ?` di sini nullable
                // REFERENCE type — bukan Nullable<T>, dan tidak punya `.Value`.
                var shift = new XYZ(target.X - before.X, target.Y - before.Y, 0);

                if (shift.GetLength() <= ToleranceFeet)
                {
                    already.Add(name);
                    after.Add(Math.Round(RevitUnits.FeetToMm(offsetFeet), 0));
                    continue;
                }

                try
                {
                    ElementTransformUtils.MoveElement(doc, device.Id, shift);
                    moved.Add(name);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    // Instance yang menempel pada muka dinding tidak bisa
                    // digeser keluar dari muka itu. Disebut per perangkat, bukan
                    // menggagalkan seluruhnya: sembilan yang berhasil tetap
                    // berhasil.
                    failures.Add($"{name}: {ex.Message}");
                }
            }

            // Diukur ULANG dari model, sesudah geserannya terjadi.
            //
            // Inilah yang membedakan perintah ini dari yang ia perbaiki. Revit
            // mengekang perpindahan instance yang ter-host, dan kekangan itu
            // tidak selalu melempar — ia diam-diam menaruh elemennya di tempat
            // lain. Melaporkan jarak yang DIMINTA berarti mengulangi kesalahan
            // yang sama dengan kalimat yang lebih meyakinkan.
            doc.Regenerate();
            after.Clear();
            foreach (var device in devices)
            {
                if (device.Location is not LocationPoint now) continue;
                var along = perimeter.NearestOn(now.Point);
                var gap = along is null ? null : perimeter.DistanceToNearestDoor(along.Value);
                after.Add(gap is null ? -1 : Math.Round(RevitUnits.FeetToMm(gap.Value), 0));
            }

            if (dryRun) transaction.RollBack();
            else transaction.Commit();
        }
        catch (Exception ex)
        {
            transaction.RollBack();
            Logger.Error("move_devices rolled back", ex);
            return CommandResult.FromException(ex);
        }

        Logger.Info($"move_devices: {moved.Count} digeser di {room.Name}, {failures.Count} gagal");

        return CommandResult.Ok(new MoveResultDto
        {
            Room = room.Name,
            What = what,
            To = to,
            OffsetMm = offsetMm,
            DevicesMoved = moved.Count,
            AlreadyCorrect = already.Count,
            DeviceIds = moved,
            DoorDistanceMm = after.Where(mm => mm >= 0).ToList(),
            Failures = failures.Count > 0 ? failures : null,
            DryRun = dryRun ? true : (bool?)null,
        });
    }

    /// <summary>Titik di samping pintu TERDEKAT dari sebuah perangkat.</summary>
    private static XYZ? NearestTarget(
        RoomPerimeter perimeter,
        List<RoomPerimeter.Opening> doors,
        XYZ from,
        double offsetFeet)
    {
        XYZ? best = null;
        var bestGap = double.MaxValue;

        foreach (var door in doors)
        {
            var at = perimeter.BesideOpening(door, offsetFeet, ClearanceFeet);
            if (at is null) continue;

            var point = perimeter.PointAt(at.Value);
            if (point is null) continue;

            // Dibandingkan mendatar saja: perangkat berdiri 1.200 mm di atas
            // lantai sementara titik batasnya di lantai, dan selisih tegak itu
            // sama untuk setiap kandidat.
            var gap = new XYZ(point.X - from.X, point.Y - from.Y, 0).GetLength();
            if (gap >= bestGap) continue;

            bestGap = gap;
            best = new XYZ(point.X, point.Y, from.Z);
        }

        return best;
    }
}
