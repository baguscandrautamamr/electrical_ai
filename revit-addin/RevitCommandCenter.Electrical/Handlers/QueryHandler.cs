using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.SmartHangers;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>
/// Answers questions about what is already in the model.
///
/// The only read-only handler: it opens no transaction and calls
/// <see cref="HandlerContext.Persist"/> for nothing, so however a query is
/// phrased it cannot alter the drawing. That is what makes it safe to expose
/// to the viewer role.
/// </summary>
public sealed class QueryHandler : ICommandHandler
{
    public string CommandType => "query";

    /// <summary>
    /// The second name this handler answers to.
    ///
    /// "Which sheets are there?" is a query with one answer pinned, and asking
    /// someone to remember `what=sheet` for the question they ask most before
    /// printing is a tax on the wrong person. Registered in CommandProcessor
    /// against this same instance.
    /// </summary>
    public const string SheetListCommandType = "list_sheets";

    /// <summary>
    /// Elements a query can count, and the label each reports under. A null
    /// category means the target is matched some other way — see Collect.
    /// </summary>
    private sealed record Target(string Key, string Label, BuiltInCategory? Category);

    /// <summary>
    /// Order matters: it is the order groups appear in the reply, so the
    /// categories an engineer asks about most come first.
    /// </summary>
    private static readonly Target[] Targets =
    {
        new("lighting", "lighting.title", BuiltInCategory.OST_LightingFixtures),
        new("lighting_device", "lighting_device.title", BuiltInCategory.OST_LightingDevices),
        new("receptacle", "receptacle.title", BuiltInCategory.OST_ElectricalFixtures),
        new("cable_tray", "cable_tray.title", BuiltInCategory.OST_CableTray),
        new("hanger", "query.hangers", null),
        new("fire_alarm", "fire_alarm.title", BuiltInCategory.OST_FireAlarmDevices),
        new("telephone", "telephone.title", BuiltInCategory.OST_TelephoneDevices),
        new("lan", "lan.title", BuiltInCategory.OST_DataDevices),
        new("security", "security.title", BuiltInCategory.OST_SecurityDevices),
        new("communication", "communication.title", BuiltInCategory.OST_CommunicationDevices),
        new("panel", "query.panels", BuiltInCategory.OST_ElectricalEquipment),
        new("room", "query.rooms", BuiltInCategory.OST_Rooms),
        // Sheets are not in the model the way a device is, but "which sheets do
        // we have?" is the question standing between an engineer and
        // /print_pdf — and the only way to answer it was to open Revit.
        new("sheet", "query.sheets", BuiltInCategory.OST_Sheets),
    };

    /// <summary>
    /// Targets `what=all` leaves out.
    ///
    /// Both answer a different question from "what is installed here": rooms
    /// are the container, sheets are the drawing set. Counting them alongside
    /// the devices makes a total that means nothing.
    /// </summary>
    private static readonly string[] NotInAll = { "room", "sheet" };

    public CommandResult Execute(HandlerContext context, CommandModel command)
    {
        var doc = context.Doc;
        var asSheetList = command.CommandType.Equals(
            SheetListCommandType, StringComparison.OrdinalIgnoreCase);

        var what = asSheetList ? "sheet" : command.GetString("what", "all").ToLowerInvariant();
        var roomName = asSheetList ? string.Empty : command.GetString("room");
        var levelName = command.GetString("level");
        // Which family, out of the several a category holds.
        //
        // Without this, "how many 22W downlights on level 1" was answered with the
        // count of every lighting fixture on level 1 — a number that is right about
        // a question nobody asked, and indistinguishable from the answer. The one
        // way to ask it was /inspect, which needs a category name, a parameter
        // name, and a filter, and which nobody reaches for when /query is right
        // there and appears to have answered.
        var familyName = asSheetList ? string.Empty : command.GetString("family");
        // A count of sheets answers nothing — the numbers are the answer, and
        // they are what /print_pdf takes. So sheets are always listed.
        var wantsList = what == "sheet"
                        || string.Equals(command.GetString("detail", "summary"), "list",
                            StringComparison.OrdinalIgnoreCase);
        var limit = Math.Clamp(command.GetInt("limit", what == "sheet" ? 100 : 30), 1, 200);

        var selected = what == "all"
            ? Targets.Where(target => !NotInAll.Contains(target.Key)).ToArray()
            : Targets.Where(target => target.Key == what).ToArray();

        if (selected.Length == 0)
        {
            return CommandResult.Fail($"Unknown query target '{what}'.", retryable: false);
        }

        var lookup = string.IsNullOrWhiteSpace(roomName)
            ? null
            : RevitUtils.ResolveRoom(doc, roomName);
        var room = lookup?.Room;
        var level = string.IsNullOrWhiteSpace(levelName) ? null : FindLevel(doc, levelName);

        var result = new QueryResultDto
        {
            What = what,
            Room = string.IsNullOrWhiteSpace(roomName) ? null : roomName,
            // A name that matches nothing must not be answered with a confident
            // zero — the reply says so and falls back to the whole model.
            RoomMatched = string.IsNullOrWhiteSpace(roomName) || room is not null,
            Level = level?.Name ?? (string.IsNullOrWhiteSpace(levelName) ? null : levelName),
            // Reported back, not just applied. A count that was narrowed by a
            // family and does not say so is a count of the whole category as far
            // as anybody reading it can tell.
            Family = string.IsNullOrWhiteSpace(familyName) ? null : familyName,
        };

        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(levelName) && level is null)
        {
            notes.Add($"Level '{levelName}' not found; searched every level.");
        }

        // A name matching several rooms is worth saying out loud: the count
        // below covers the whole model, and the reason is not "no such room".
        if (lookup?.Problem is { } problem && room is null)
        {
            notes.Add(problem);
        }

        var items = wantsList ? new List<QueryItemDto>() : null;
        var matched = 0;
        // What the model DOES have, gathered as we go, in case the family named
        // matches nothing. Cheap here and impossible afterwards: the elements are
        // in hand exactly once.
        var familiesSeen = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in selected)
        {
            var inScope = Collect(context, target)
                .Where(element => InRoom(element, room))
                .Where(element => OnLevel(doc, element, level))
                .ToList();

            var elements = inScope;

            if (!string.IsNullOrWhiteSpace(familyName))
            {
                var kept = new List<Element>();

                // One pass, one read of the family name per element: it is a type
                // lookup, and doing it twice over twenty thousand elements is time
                // Revit spends frozen for no reason.
                foreach (var element in inScope)
                {
                    var family = RevitUtils.FamilyNameOf(doc, element);

                    if (!string.IsNullOrWhiteSpace(family) && familiesSeen.Count < MaxFamiliesScanned)
                    {
                        familiesSeen.Add(family);
                    }

                    if (FamilyMatches(doc, element, familyName, family)) kept.Add(element);
                }

                elements = kept;
            }

            matched += elements.Count;
            if (elements.Count == 0 && what == "all") continue;

            result.Groups.Add(new QueryGroupDto
            {
                Label = target.Label,
                Count = elements.Count,
                Detail = Describe(target, elements),
            });

            if (items is null) continue;

            foreach (var element in elements)
            {
                // The cap is per query, not per category, so a `what=all` list
                // cannot blow past Telegram's message limit one group at a time.
                if (items.Count >= limit) break;
                items.Add(DescribeItem(doc, element));
            }
        }

        // Zero for a named family is the one zero here that is usually about
        // spelling rather than about the model. An empty count cannot say which,
        // so the names that are actually there say it instead.
        if (matched == 0 && !string.IsNullOrWhiteSpace(familyName))
        {
            notes.Add(familiesSeen.Count == 0
                ? $"No element at all in scope, so nothing could carry family '{familyName}'."
                : $"No '{familyName}' here. The families in scope are: "
                  + string.Join(", ", familiesSeen.Take(MaxFamilyNames))
                  + (familiesSeen.Count > MaxFamilyNames ? ", …" : string.Empty)
                  + ".");
        }

        result.Total = matched;
        result.Items = items is { Count: > 0 } ? items : null;
        result.ItemsOmitted = items is not null && matched > items.Count ? matched - items.Count : null;
        result.Notes = notes.Count > 0 ? notes : null;

        Logger.Info(
            $"Query {what} (room='{roomName}', level='{levelName}', family='{familyName}') "
            + $"matched {matched}");
        return CommandResult.Ok(result);
    }

    // -----------------------------------------------------------------------
    // Collection
    // -----------------------------------------------------------------------

    private static IEnumerable<Element> Collect(HandlerContext context, Target target)
    {
        var doc = context.Doc;

        // Hangers are an ordinary family rather than a category of their own,
        // so they are matched by the configured family name — exactly how
        // CableTrayHandler finds the ones it placed.
        if (target.Category is null)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(instance =>
                    HangerTypeDetector.IsHangerFamily(instance, context.Config.HangerFamilyName));
        }

        // Room DAN Space MEP: keduanya "ruangan" bagi orang yang bertanya, dan
        // model MEP yang sungguhan hampir selalu punya keduanya. Satu kategori
        // saja tidak cukup, jadi jalannya tidak lewat collector di bawah. Yang
        // belum ditempatkan dibuang di dalam Enclosures — luasnya nol dan tidak
        // ada yang bisa melihatnya di gambar.
        if (target.Key == "room")
        {
            return RevitUtils.Enclosures(doc);
        }

        // Placeholder sheets carry a number but no drawing. They belong in an
        // issue register, not in a list of what can be printed.
        if (target.Key == "sheet")
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(sheet => !sheet.IsPlaceholder)
                .OrderBy(sheet => sheet.SheetNumber, StringComparer.OrdinalIgnoreCase);
        }

        return new FilteredElementCollector(doc)
            .OfCategory(target.Category.Value)
            .WhereElementIsNotElementType()
            .ToElements();
    }

    private static Level? FindLevel(Document doc, string name) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .FirstOrDefault(level => string.Equals(level.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .FirstOrDefault(level => level.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

    // -----------------------------------------------------------------------
    // Scoping
    // -----------------------------------------------------------------------

    private static bool InRoom(Element element, SpatialElement? room)
    {
        if (room is null) return true;
        if (element.Id == room.Id) return true;

        var point = LocationOf(element);
        if (point is null) return false;

        return RevitUtils.Contains(room, point);
    }

    /// <summary>
    /// On this level — asked of every way an element states which level it is on.
    ///
    /// This compared <c>LevelId</c> only, which cable tray, conduit, and hosted
    /// families leave empty; they keep their level in a parameter instead. So
    /// `level=` quietly dropped most of the tray from a tray count, and the answer
    /// was a smaller number with nothing to mark it as wrong.
    /// </summary>
    private static bool OnLevel(Document doc, Element element, Level? level)
    {
        if (level is null) return true;
        return RevitUtils.LevelIdOf(doc, element) == level.Id;
    }

    /// <summary>How many family names a "no such family" note will list.</summary>
    private const int MaxFamilyNames = 12;

    /// <summary>
    /// How many distinct names are collected for that note before it stops.
    ///
    /// Larger than the note needs, so "and more" is honest, and bounded so a model
    /// with a thousand families does not build a thousand-entry set to print twelve
    /// of them.
    /// </summary>
    private const int MaxFamiliesScanned = MaxFamilyNames * 4;

    /// <summary>
    /// Whether an element belongs to the family that was named.
    ///
    /// Exact first, then contains — the same order <see cref="RevitUtils.FindSymbol"/>
    /// uses, and for the same reason. A name picked from the list this add-in
    /// reported must match exactly and nothing else; a name somebody typed
    /// ("downlight") should find the downlight. The type name counts too, because
    /// "22WATT" is as likely to be the type as the family and the person asking has
    /// no reason to know which.
    /// </summary>
    private static bool FamilyMatches(Document doc, Element element, string wanted, string family)
    {
        var name = wanted.Trim();
        if (name.Length == 0) return true;

        var type = element is FamilyInstance instance
            ? instance.Symbol?.Name ?? string.Empty
            : doc.GetElement(element.GetTypeId())?.Name ?? string.Empty;

        // "Family: Type", the form /model_info reports and therefore the form that
        // comes back from the website and the assistant. Both halves were named, so
        // both have to hold — otherwise asking for one type out of a family counts
        // every type in it.
        var colon = name.IndexOf(':');
        if (colon > 0)
        {
            var familyPart = name[..colon].Trim();
            var typePart = name[(colon + 1)..].Trim();

            if (string.Equals(family, familyPart, StringComparison.OrdinalIgnoreCase)
                && string.Equals(type, typePart, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // The type half may have been renamed since the list was read; the
            // family half is the more stable of the two, so it gets the next try.
            if (familyPart.Length > 0) name = familyPart;
        }

        if (string.Equals(family, name, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(type, name, StringComparison.OrdinalIgnoreCase)) return true;

        return family.Contains(name, StringComparison.OrdinalIgnoreCase)
               || type.Contains(name, StringComparison.OrdinalIgnoreCase);
    }

    private static XYZ? LocationOf(Element element) => element.Location switch
    {
        LocationPoint point => point.Point,
        // A tray or conduit sits in whichever room its midpoint falls in.
        LocationCurve curve => curve.Curve.Evaluate(0.5, true),
        _ => element.get_BoundingBox(null) is { } box
            ? box.Min.Add(box.Max).Multiply(0.5)
            : null,
    };

    // -----------------------------------------------------------------------
    // Description
    // -----------------------------------------------------------------------

    /// <summary>The secondary figure on a group row — the one worth the space.</summary>
    private static string? Describe(Target target, IReadOnlyCollection<Element> elements)
    {
        if (elements.Count == 0) return null;

        switch (target.Key)
        {
            case "lighting":
            {
                var watts = elements.Sum(TotalWatts);
                return watts > 0 ? $"{watts:F0} W" : null;
            }

            case "cable_tray":
            {
                var metres = elements
                    .OfType<CableTray>()
                    .Sum(tray => tray.Location is LocationCurve curve
                        ? RevitUnits.FeetToM(curve.Curve.Length)
                        : 0);
                return metres > 0 ? $"{metres:F1} m" : null;
            }

            case "room":
            {
                var areaSqM = elements.OfType<Room>().Sum(RevitUtils.RoomAreaSqM);
                return areaSqM > 0 ? $"{areaSqM:F0} m²" : null;
            }

            default:
                return null;
        }
    }

    private static double TotalWatts(Element element)
    {
        // The same reading the placement handlers use, so counting the fixtures
        // and placing them cannot report two different totals for one room.
        // It looks at the type as well as the instance, which matters because
        // Apparent Load is a type parameter on most families.
        if (element is FamilyInstance instance)
        {
            var declared = ElectricalLoad.WattsOf(instance);
            if (declared is > 0) return declared.Value;
        }
        else
        {
            var declared = ParameterMapper.GetFirstAvailable(
                element,
                new[] { "Wattage", "Apparent Load", "Load" });
            if (declared > 0) return declared;
        }

        // Nothing declared: fall back to the wattage in the family name, the
        // same reading LightingHandler used when it placed the fixture.
        var name = (element as FamilyInstance)?.Symbol?.Name ?? element.Name;
        return LightingHandler.ExtractWattage(name);
    }

    private static QueryItemDto DescribeItem(Document doc, Element element)
    {
        // A sheet identifies itself by its number, which is also what
        // /print_pdf takes — so the list doubles as the argument for it.
        if (element is ViewSheet sheet)
        {
            return new QueryItemDto
            {
                Id = sheet.SheetNumber,
                Label = sheet.Name,
                Detail = null,
            };
        }

        var mark = ParameterMapper.GetStringParameter(element, "Mark");
        // The same reading the level filter uses: a listed item whose level column
        // is blank on a query that filtered BY level reads as a contradiction.
        var levelName = doc.GetElement(RevitUtils.LevelIdOf(doc, element))?.Name;

        return new QueryItemDto
        {
            // Mark is what an engineer reads off the drawing; the element id is
            // the fallback that always exists.
            Id = string.IsNullOrWhiteSpace(mark) ? element.Id.ToString() : mark,
            Label = element switch
            {
                SpatialElement enclosure => string.IsNullOrWhiteSpace(enclosure.Number)
                    ? enclosure.Name
                    : $"{enclosure.Name} ({enclosure.Number})",
                FamilyInstance instance => instance.Symbol?.Name ?? instance.Name,
                _ => element.Name,
            },
            Detail = string.IsNullOrWhiteSpace(levelName) ? null : levelName,
        };
    }
}
