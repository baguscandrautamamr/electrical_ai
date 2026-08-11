using Autodesk.Revit.DB;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>
/// Reads anything in the model, rather than the thirteen things /query knows.
///
/// /query answers the questions this system was built around — how many
/// fixtures in this room, how many metres of tray — and its category list and
/// its columns are both fixed. That is the right shape for the questions it
/// was built for and no shape at all for the next one. "How many doors are
/// 900 wide", "what is the total volume of concrete on level 2", "which panels
/// have no circuits" are all one collector and one parameter read away, and all
/// of them were impossible.
///
/// So this is the general form, in three modes, and the order matters:
///
///   what=categories  — which categories exist here, and how many of each.
///   what=parameters  — what a category's elements can be asked about.
///   what=elements    — the rows themselves, with the columns you name.
///
/// The first two exist because the third cannot be used without them. A
/// parameter has to be named exactly to be read, and nobody — engineer or
/// assistant — can name one they have never seen. Guessing "Length" and getting
/// an empty column is indistinguishable from a model that has no lengths, which
/// is the failure this whole system keeps having to design away.
///
/// `category` and `where` both take several, comma separated: several categories
/// because "every device in this room, by family" is eight of them, and asked one
/// command at a time it is eight waits on Revit and eight tables for somebody to
/// add up by hand; several conditions because a filter naming two parameters could
/// not be written at all, and family-and-room-and-level only worked because room
/// and level happen to have arguments of their own.
///
/// Read-only, like /query: no transaction is opened and nothing is persisted, so
/// however a request is phrased it cannot change the drawing. Viewers may run it.
/// </summary>
public sealed class InspectHandler : ICommandHandler
{
    public string CommandType => "inspect";

    /// <summary>
    /// How many elements are gathered before filtering, at most.
    ///
    /// A federated model has millions; reading a parameter off each one on
    /// Revit's UI thread stops Revit for as long as it takes. The cap is
    /// reported when it bites, because a total computed from part of the model
    /// that says nothing about it is worse than no total.
    /// </summary>
    private const int MaxScanned = 20_000;

    /// <summary>Rows returned at most, whatever the limit asks for.</summary>
    private const int MaxRows = 200;

    /// <summary>Distinct values a group_by will name before it stops.</summary>
    private const int MaxGroups = 50;

    /// <summary>Columns when none are named: what identifies a row on a drawing.</summary>
    private static readonly string[] DefaultColumns = { "Id", "Mark", "Type", "Level" };

    public CommandResult Execute(HandlerContext context, CommandModel command)
    {
        var doc = context.Doc;
        var what = command.GetString("what", "elements").ToLowerInvariant();

        return what switch
        {
            "categories" => Categories(doc),
            "parameters" => Parameters(doc, command),
            "elements" => Elements(doc, command),
            _ => CommandResult.Fail(
                $"Unknown inspect target '{what}'. Use 'categories', 'parameters', or 'elements'.",
                retryable: false),
        };
    }

    // ------------------------------------------------------------ categories

    /// <summary>
    /// Every category that has at least one element in this model.
    ///
    /// Counted in a single pass rather than one collector per category: Revit
    /// knows about some six hundred categories and all but a few dozen are
    /// empty in any real project, so asking each one separately spends most of
    /// its time proving that nothing is there.
    ///
    /// Only categories with something in them are reported. A list of six
    /// hundred names, nearly all of them zero, is a list nobody reads — and the
    /// point of this mode is to be read before the next question is asked.
    /// </summary>
    private static CommandResult Categories(Document doc)
    {
        var counts = new Dictionary<long, int>();

        foreach (var element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
        {
            var category = element.Category;
            if (category is null) continue;

            var id = category.Id.Value;
            counts[id] = counts.TryGetValue(id, out var seen) ? seen + 1 : 1;
        }

        var rows = new List<InspectCategoryDto>();

        foreach (var (id, count) in counts)
        {
            var category = Category.GetCategory(doc, new ElementId(id));
            if (category is null) continue;

            rows.Add(new InspectCategoryDto
            {
                Name = category.Name,
                BuiltIn = BuiltInNameOf(id),
                Count = count,
            });
        }

        return CommandResult.Ok(new InspectResultDto
        {
            What = "categories",
            Total = rows.Count,
            CategoryList = rows
                .OrderByDescending(row => row.Count)
                .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        });
    }

    /// <summary>`OST_Doors` for the categories Revit names, null for the rest.</summary>
    private static string? BuiltInNameOf(long id)
    {
        var value = (BuiltInCategory)id;
        return Enum.IsDefined(typeof(BuiltInCategory), value) ? value.ToString() : null;
    }

    // ------------------------------------------------------------ parameters

    /// <summary>
    /// What a category's elements can be asked about, with a value from a real
    /// element beside each name.
    ///
    /// The sample is the point. A parameter list is a list of words; "Width —
    /// 900 mm" is an answer to "is this the one I mean", and it also settles the
    /// unit question before a total is ever asked for. Instance and type
    /// parameters are both listed and marked, because "Width" living on the type
    /// is exactly the sort of thing that makes a column come back empty.
    /// </summary>
    private static CommandResult Parameters(Document doc, CommandModel command)
    {
        var categoryName = command.GetString("category");
        if (string.IsNullOrWhiteSpace(categoryName))
        {
            return CommandResult.Fail(
                "inspect what=parameters needs a category. Run what=categories first to see them.",
                retryable: false);
        }

        var category = ResolveCategory(doc, categoryName);
        if (category is null) return UnknownCategory(doc, categoryName);

        var elements = Collect(doc, new List<Category> { category }, out _);
        var sample = elements.FirstOrDefault();

        if (sample is null)
        {
            return CommandResult.Fail(
                $"'{category.Name}' has no elements in this model, so it has nothing to describe.",
                retryable: false);
        }

        var rows = new List<InspectParameterDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in VirtualColumns)
        {
            if (!seen.Add(name)) continue;
            rows.Add(new InspectParameterDto
            {
                Name = name,
                Scope = "built-in",
                Storage = "String",
                Sample = Read(doc, sample, name).Text,
            });
        }

        Describe(sample, "instance", rows, seen);

        if (doc.GetElement(sample.GetTypeId()) is { } type) Describe(type, "type", rows, seen);

        return CommandResult.Ok(new InspectResultDto
        {
            What = "parameters",
            Category = category.Name,
            Total = rows.Count,
            ParameterList = rows,
        });
    }

    private static void Describe(
        Element element,
        string scope,
        List<InspectParameterDto> rows,
        HashSet<string> seen)
    {
        foreach (var parameter in element.GetOrderedParameters())
        {
            var name = parameter.Definition?.Name;
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;

            rows.Add(new InspectParameterDto
            {
                Name = name,
                Scope = scope,
                Storage = parameter.StorageType.ToString(),
                Unit = UnitLabel(parameter),
                Sample = Display(parameter),
            });
        }
    }

    // -------------------------------------------------------------- elements

    private static CommandResult Elements(Document doc, CommandModel command)
    {
        var categoryName = command.GetString("category");
        if (string.IsNullOrWhiteSpace(categoryName))
        {
            return CommandResult.Fail(
                "inspect needs a category. Run what=categories first to see what this model has.",
                retryable: false);
        }

        // Several categories at once, because a question rarely stops at one.
        // "Every device in this room, by family" is eight categories; asked one
        // command at a time it is eight waits on Revit and eight tables to add up
        // by hand, and the total nobody computes is the one that was asked for.
        var wanted = Split(categoryName);
        var categories = new List<Category>();

        foreach (var name in wanted)
        {
            var resolved = ResolveCategory(doc, name);
            if (resolved is null) return UnknownCategory(doc, name);

            // A model may spell two of the asked names as the same category.
            if (!categories.Any(seen => seen.Id == resolved.Id)) categories.Add(resolved);
        }

        var elements = Collect(doc, categories, out var truncated);

        // Room and level narrow the set exactly the way /query does, so the same
        // question asked either way cannot come back with two different answers.
        var roomName = command.GetString("room");
        var lookup = string.IsNullOrWhiteSpace(roomName) ? null : RevitUtils.ResolveRoom(doc, roomName);
        var room = lookup?.Room;

        var levelName = command.GetString("level");
        var level = string.IsNullOrWhiteSpace(levelName) ? null : FindLevel(doc, levelName);

        var categoryLabel = string.Join(", ", categories.Select(one => one.Name));

        var notes = new List<string>();
        if (truncated)
        {
            notes.Add(
                $"This model has more than {MaxScanned} elements in '{categoryLabel}'; "
                + "the figures below cover the first ones found, not all of them.");
        }
        if (!string.IsNullOrWhiteSpace(roomName) && room is null)
        {
            notes.Add(lookup?.Problem ?? $"No room called '{roomName}'; searched the whole model.");
        }
        if (!string.IsNullOrWhiteSpace(levelName) && level is null)
        {
            notes.Add($"Level '{levelName}' not found; searched every level.");
        }

        var raw = command.GetString("where");
        var filter = Filter.Parse(raw);

        if (filter is null && !string.IsNullOrWhiteSpace(raw))
        {
            return CommandResult.Fail(
                "`where` reads as Parameter=Value — also != for not, ~ for contains, "
                + "and > or < for numbers. Join several with a comma, all of which must "
                + "hold: where=\"Family=ACT_E_DOWNLIGHT 22WATT, Width>800\".",
                retryable: false);
        }

        var scoped = elements
            .Where(element => InRoom(element, room))
            .Where(element => level is null || RevitUtils.LevelIdOf(doc, element) == level.Id)
            .ToList();

        var matched = filter is null
            ? scoped
            : scoped.Where(element => filter.Holds(doc, element)).ToList();

        // Nothing matched, and the reason is almost never "this model has none".
        // It is a name spelled the way somebody remembers it rather than the way
        // Revit stores it — and an empty table says both of those the same way.
        if (matched.Count == 0 && filter is not null)
        {
            notes.AddRange(NearMisses(doc, scoped, filter));
        }

        var columns = Columns(command);
        var limit = Math.Clamp(command.GetInt("limit", 30), 1, MaxRows);

        var rows = matched
            .Take(limit)
            .Select(element => columns.ToDictionary(
                column => column,
                column => Read(doc, element, column).Text,
                StringComparer.Ordinal))
            .ToList();

        return CommandResult.Ok(new InspectResultDto
        {
            What = "elements",
            Category = categoryLabel,
            Room = room?.Name,
            Level = level?.Name,
            // Over everything that matched, not over the rows shown. A total
            // computed from the first thirty of two hundred is a number that
            // looks like an answer and is not one.
            Total = matched.Count,
            Shown = rows.Count,
            Columns = columns,
            Rows = rows,
            Totals = Totals(doc, matched, command.GetString("total")),
            Groups = Groups(doc, matched, command.GetString("group_by")),
            Notes = notes.Count > 0 ? notes : null,
        });
    }

    private static List<string> Columns(CommandModel command)
    {
        var asked = Split(command.GetString("params"));
        return asked.Count > 0 ? asked : DefaultColumns.ToList();
    }

    /// <summary>
    /// Sums, over every element that matched — the answer to "how many metres of
    /// tray", in one number instead of five hundred rows to add up by hand.
    ///
    /// Only what Revit stores as a number can be summed, and it is summed in the
    /// unit the project displays, not in Revit's internal feet. That conversion
    /// is the whole difference between 3.75 m and 12.3: both are the same tray,
    /// and only one of them is an answer.
    /// </summary>
    private static List<InspectTotalDto>? Totals(Document doc, List<Element> elements, string asked)
    {
        var names = Split(asked);
        if (names.Count == 0) return null;

        var totals = new List<InspectTotalDto>();

        foreach (var name in names)
        {
            var sum = 0.0;
            var counted = 0;
            string? unit = null;

            foreach (var element in elements)
            {
                var value = Read(doc, element, name);
                if (value.Number is null) continue;

                sum += value.Number.Value;
                unit ??= value.Unit;
                counted++;
            }

            totals.Add(new InspectTotalDto
            {
                Parameter = name,
                Sum = counted > 0 ? Math.Round(sum, 3) : null,
                Unit = unit,
                Counted = counted,
                // Said rather than left to be inferred from a small number: a
                // parameter that is text, or empty on most elements, produces a
                // total that is quietly about a different set than the one asked
                // about.
                Missing = elements.Count - counted,
            });
        }

        return totals;
    }

    /// <summary>How many elements share each value — "doors by type", counted.</summary>
    private static List<InspectGroupDto>? Groups(Document doc, List<Element> elements, string asked)
    {
        var name = asked.Trim();
        if (name.Length == 0) return null;

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in elements)
        {
            var value = Read(doc, element, name).Text;
            if (string.IsNullOrWhiteSpace(value)) value = "(kosong)";
            counts[value] = counts.TryGetValue(value, out var seen) ? seen + 1 : 1;
        }

        return counts
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Take(MaxGroups)
            .Select(entry => new InspectGroupDto { Value = entry.Key, Count = entry.Value })
            .ToList();
    }

    /// <summary>Distinct values a near-miss note will name before it stops.</summary>
    private const int MaxSuggestions = 12;

    /// <summary>
    /// What the model actually has, when the filter matched none of it.
    ///
    /// An empty table answers two completely different questions the same way:
    /// "this model has no downlights" and "that is not how this model spells
    /// downlight". The second is far more common — a name is remembered, or
    /// half-remembered, or read off a drawing that was renamed since — and there
    /// was no way to tell which had happened without opening Revit.
    ///
    /// So the values that ARE there get named. Only for text: a note listing every
    /// distinct width in millimetres is not a suggestion, it is the table again.
    /// And only for the parameter that was filtered on, which is the one whose
    /// spelling is in question.
    /// </summary>
    private static List<string> NearMisses(Document doc, List<Element> scoped, Filter filter)
    {
        var notes = new List<string>();

        if (scoped.Count == 0)
        {
            notes.Add("Nothing matched, and nothing was in scope either — the room, level, or category narrowed it to zero before `where` was applied.");
            return notes;
        }

        foreach (var parameter in filter.Parameters.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var values = scoped
                .Select(element => Read(doc, element, parameter))
                // Numbers are excluded on purpose: their range is the answer, not
                // their spelling, and there is nothing to suggest.
                .Where(value => value.Number is null && !string.IsNullOrWhiteSpace(value.Text))
                .Select(value => value.Text)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(text => text, StringComparer.OrdinalIgnoreCase)
                .Take(MaxSuggestions + 1)
                .ToList();

            if (values.Count == 0) continue;

            var more = values.Count > MaxSuggestions ? ", …" : string.Empty;
            notes.Add(
                $"No element matched. '{parameter}' in scope reads: "
                + string.Join(", ", values.Take(MaxSuggestions))
                + $"{more}. Use ~ instead of = to match part of a name.");
        }

        if (notes.Count == 0)
        {
            notes.Add(
                $"No element matched, and none of the {scoped.Count} elements in scope has a value "
                + "for the parameter you filtered on — check the name against inspect what=parameters.");
        }

        return notes;
    }

    // ------------------------------------------------------------------ read

    /// <summary>A parameter's value, as text for reading and as a number for summing.</summary>
    private readonly record struct Value(string Text, double? Number, string? Unit)
    {
        public static readonly Value Empty = new(string.Empty, null, null);
    }

    /// <summary>
    /// Columns that are not parameters.
    ///
    /// An element's id, its category, and the type it was placed from are things
    /// every element has and none of them exposes as a parameter with those
    /// names. `Length` is here as well, as a fallback: a tray states its length
    /// through its geometry, and asking Revit for a parameter by that name on a
    /// model in another language finds nothing at all.
    /// </summary>
    private static readonly string[] VirtualColumns =
        { "Id", "Category", "Family", "Type", "Level", "Room", "Length" };

    private static Value Read(Document doc, Element element, string column)
    {
        switch (column.Trim().ToLowerInvariant())
        {
            case "id":
                return new Value(element.Id.ToString(), null, null);

            case "category":
                return new Value(element.Category?.Name ?? string.Empty, null, null);

            // Loadable or system — a tray has a family name too, and it is the
            // name in the project browser.
            case "family":
                return new Value(RevitUtils.FamilyNameOf(doc, element), null, null);

            case "type":
                return new Value(
                    element is FamilyInstance typed
                        ? typed.Symbol?.Name ?? string.Empty
                        : doc.GetElement(element.GetTypeId())?.Name ?? string.Empty,
                    null,
                    null);

            // The same reading the level FILTER uses. When these two disagree, a
            // row appears in a table that says it was filtered out — or worse, is
            // filtered out of a table whose Level column would have shown it.
            case "level":
                return new Value(
                    doc.GetElement(RevitUtils.LevelIdOf(doc, element))?.Name ?? string.Empty,
                    null,
                    null);

            case "room":
                return new Value(RoomOf(doc, element), null, null);

            case "name":
                return new Value(element.Name ?? string.Empty, null, null);
        }

        // A real parameter, on the instance or on its type. Instance first: where
        // both carry the same name, the instance is the one that was overridden
        // for this element, and that is the value on the drawing.
        var parameter = element.LookupParameter(column)
                        ?? doc.GetElement(element.GetTypeId())?.LookupParameter(column);

        if (parameter is not null) return Of(parameter);

        // Length, unasked for by name, still answerable from the geometry — which
        // is how a cable tray says how long it is.
        if (string.Equals(column.Trim(), "Length", StringComparison.OrdinalIgnoreCase)
            && element.Location is LocationCurve curve)
        {
            var metres = RevitUnits.FeetToM(curve.Curve.Length);
            return new Value($"{metres:F2} m", metres, "m");
        }

        return Value.Empty;
    }

    private static Value Of(Parameter parameter)
    {
        var text = Display(parameter);

        if (parameter.StorageType == StorageType.Integer)
        {
            return new Value(text, parameter.AsInteger(), null);
        }

        if (parameter.StorageType != StorageType.Double) return new Value(text, null, null);

        // Converted out of Revit's internal units into the ones the project
        // displays, so a sum of lengths is a sum of metres and not of feet.
        try
        {
            var unitId = parameter.GetUnitTypeId();
            return new Value(
                text,
                UnitUtils.ConvertFromInternalUnits(parameter.AsDouble(), unitId),
                LabelUtils.GetLabelForUnit(unitId));
        }
        catch (Exception ex)
        {
            // A parameter with no unit — a plain number. Its raw value is its
            // value; there is nothing to convert it out of.
            Logger.Debug($"inspect: no display unit for '{parameter.Definition?.Name}': {ex.Message}");
            return new Value(text, parameter.AsDouble(), null);
        }
    }

    /// <summary>
    /// What Revit itself would print in the properties palette.
    ///
    /// AsValueString carries the project's units and rounding — "900 mm", not
    /// "2.9527559055". For the columns it does not answer (text, ids) the
    /// storage type decides.
    /// </summary>
    private static string Display(Parameter parameter)
    {
        if (!parameter.HasValue) return string.Empty;

        return parameter.StorageType switch
        {
            StorageType.String => parameter.AsString() ?? string.Empty,
            StorageType.Integer => parameter.AsValueString() ?? parameter.AsInteger().ToString(),
            StorageType.Double => parameter.AsValueString() ?? parameter.AsDouble().ToString("F3"),
            StorageType.ElementId => parameter.AsValueString() ?? parameter.AsElementId().ToString(),
            _ => string.Empty,
        };
    }

    private static string? UnitLabel(Parameter parameter)
    {
        if (parameter.StorageType != StorageType.Double) return null;

        try
        {
            return LabelUtils.GetLabelForUnit(parameter.GetUnitTypeId());
        }
        catch (Exception ex)
        {
            Logger.Debug($"inspect: no unit label for '{parameter.Definition?.Name}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The room an element stands in.
    ///
    /// This used to answer only for FamilyInstance, which meant the Room column
    /// was blank for every cable tray, pipe, and wall — while the `room=` FILTER
    /// answered for all of them, because it works from the element's midpoint. So
    /// tray could be narrowed to a room and then reported as being in no room, in
    /// the same table. The filter was right; the column was the one lying.
    ///
    /// Now both work from geometry when the instance has nothing to say, and
    /// through the same helper the filter uses.
    /// </summary>
    private static string RoomOf(Document doc, Element element)
    {
        try
        {
            if (element is FamilyInstance instance)
            {
                // Keduanya turunan SpatialElement tapi bukan tipe yang sama, jadi
                // wadahnya harus dinyatakan — Room arsitektur dan Space MEP
                // sama-sama "ruangan" bagi yang bertanya, dan model MEP yang
                // sungguhan hampir selalu punya keduanya.
                SpatialElement? enclosure = instance.Room;
                enclosure ??= instance.Space;
                if (enclosure is not null) return enclosure.Name ?? string.Empty;
            }

            if (element is SpatialElement self) return self.Name ?? string.Empty;

            var point = MidpointOf(element);
            if (point is not null)
            {
                var at = RevitUtils.EnclosureAt(doc, point);
                if (at is not null) return at.Name ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            // Revit refuses this on elements whose phase has no room map. Not
            // worth failing a whole table over one blank cell.
            Logger.Debug($"inspect: room of {element.Id} unavailable: {ex.Message}");
        }

        return string.Empty;
    }

    // ---------------------------------------------------------------- filter

    /// <summary>
    /// Every condition asked for, all of which must hold.
    ///
    /// It was deliberately one condition, and the comment here said so: two joined
    /// by "and" is the next request, and a query language is a different project.
    /// The next request arrived, and it turned out not to be a language. Family and
    /// room and level happened to work together only because room and level have
    /// arguments of their own; "that family, with Mark starting LF-" had no way to
    /// be asked at all, and neither did anything else naming two parameters.
    ///
    /// A comma joins them, and AND is the only joiner. There is no OR and no
    /// bracket, because the moment there is either, this needs to explain its own
    /// syntax errors — and an engineer typing a filter deserves to be told which
    /// half of it was wrong, which is a bigger promise than it looks.
    /// </summary>
    private sealed record Filter(List<Condition> Conditions)
    {
        /// <summary>
        /// Conditions split on commas — except a comma that is part of a value.
        ///
        /// `Comments=dipasang ulang, cek lagi` is one condition containing a comma,
        /// not two conditions the second of which has no operator. Split blindly
        /// and it becomes a syntax error on a filter that reads perfectly; the
        /// piece with no operator is put back where it came from instead.
        /// </summary>
        public static Filter? Parse(string raw)
        {
            var text = (raw ?? string.Empty).Trim();
            if (text.Length == 0) return null;

            var pieces = new List<string>();

            foreach (var piece in text.Split(','))
            {
                if (pieces.Count > 0 && !Condition.HasOperator(piece))
                {
                    pieces[^1] = $"{pieces[^1]},{piece}";
                    continue;
                }

                pieces.Add(piece);
            }

            var conditions = new List<Condition>();

            foreach (var piece in pieces)
            {
                if (string.IsNullOrWhiteSpace(piece)) continue;

                var condition = Condition.Parse(piece);
                // One unparseable half fails the whole filter. Dropping it would
                // return MORE rows than asked for, under a filter that says
                // otherwise — a wrong answer that reads as a right one.
                if (condition is null) return null;

                conditions.Add(condition);
            }

            return conditions.Count == 0 ? null : new Filter(conditions);
        }

        public bool Holds(Document doc, Element element) =>
            Conditions.All(condition => condition.Holds(doc, element));

        /// <summary>The parameters this filter names, for reporting a near miss.</summary>
        public IEnumerable<string> Parameters => Conditions.Select(condition => condition.Parameter);
    }

    /// <summary>
    /// One condition, in the form an engineer would type it: `Width>800`,
    /// `Mark~LF-`, `Comments!=`.
    /// </summary>
    private sealed record Condition(string Parameter, string Operator, string Wanted)
    {
        private static readonly string[] Operators = { ">=", "<=", "!=", "~", "=", ">", "<" };

        public static bool HasOperator(string raw) =>
            Operators.Any(op => raw.IndexOf(op, StringComparison.Ordinal) > 0);

        public static Condition? Parse(string raw)
        {
            var text = (raw ?? string.Empty).Trim();
            if (text.Length == 0) return null;

            foreach (var op in Operators)
            {
                var at = text.IndexOf(op, StringComparison.Ordinal);
                if (at <= 0) continue;

                return new Condition(
                    text[..at].Trim(),
                    op,
                    text[(at + op.Length)..].Trim().Trim('"', '\''));
            }

            return null;
        }

        public bool Holds(Document doc, Element element)
        {
            var value = Read(doc, element, Parameter);

            // Numbers are compared as numbers when both sides are numbers —
            // otherwise "900" > "1000" would be true, which is what comparing
            // them as words means.
            if (double.TryParse(Wanted, out var wantedNumber) && value.Number is { } number)
            {
                return Operator switch
                {
                    ">" => number > wantedNumber,
                    "<" => number < wantedNumber,
                    ">=" => number >= wantedNumber,
                    "<=" => number <= wantedNumber,
                    "!=" => Math.Abs(number - wantedNumber) > 1e-9,
                    _ => Math.Abs(number - wantedNumber) <= 1e-9,
                };
            }

            var text = value.Text ?? string.Empty;

            return Operator switch
            {
                "~" => text.Contains(Wanted, StringComparison.OrdinalIgnoreCase),
                "!=" => !string.Equals(text, Wanted, StringComparison.OrdinalIgnoreCase),
                ">" or ">=" or "<" or "<=" => false,
                _ => string.Equals(text, Wanted, StringComparison.OrdinalIgnoreCase),
            };
        }
    }

    // ------------------------------------------------------------- gathering

    /// <summary>
    /// Everything in the asked-for categories, capped across all of them together.
    ///
    /// The cap is on the total rather than per category on purpose: it exists to
    /// bound how long Revit is stopped, and eight categories each just under the
    /// cap stops it eight times as long.
    /// </summary>
    private static List<Element> Collect(Document doc, List<Category> categories, out bool truncated)
    {
        var elements = new List<Element>();

        foreach (var category in categories)
        {
            var budget = MaxScanned + 1 - elements.Count;
            if (budget <= 0) break;

            elements.AddRange(new FilteredElementCollector(doc)
                .OfCategoryId(category.Id)
                .WhereElementIsNotElementType()
                .Take(budget));
        }

        truncated = elements.Count > MaxScanned;
        if (truncated) elements.RemoveAt(elements.Count - 1);

        return elements;
    }

    /// <summary>
    /// The category a name means — Revit's own name, its OST_ name, or one of the
    /// short keys the rest of this system uses.
    ///
    /// Three spellings because three kinds of caller: a person reading the Revit
    /// UI, an assistant reading /model_info, and this system's own commands,
    /// which have said `lighting` and `receptacle` since the beginning.
    /// </summary>
    private static Category? ResolveCategory(Document doc, string name)
    {
        var wanted = name.Trim();

        if (Aliases.TryGetValue(wanted, out var alias))
        {
            var known = Category.GetCategory(doc, alias);
            if (known is not null) return known;
        }

        Category? partial = null;

        foreach (Category category in doc.Settings.Categories)
        {
            if (string.Equals(category.Name, wanted, StringComparison.OrdinalIgnoreCase)) return category;

            if (string.Equals(BuiltInNameOf(category.Id.Value), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return category;
            }

            if (partial is null && category.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            {
                partial = category;
            }
        }

        return partial;
    }

    private static CommandResult UnknownCategory(Document doc, string name)
    {
        var have = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .Take(MaxScanned)
            .Select(element => element.Category?.Name)
            .Where(categoryName => !string.IsNullOrWhiteSpace(categoryName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(categoryName => categoryName, StringComparer.OrdinalIgnoreCase)
            .Take(15)
            .ToList();

        return CommandResult.Fail(
            $"No category called '{name}' in this model. Some it does have: {string.Join(", ", have)}. "
            + "Run inspect what=categories for the full list.",
            retryable: false);
    }

    /// <summary>The names this system's own commands use, mapped to Revit's.</summary>
    private static readonly Dictionary<string, BuiltInCategory> Aliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["lighting"] = BuiltInCategory.OST_LightingFixtures,
            ["lighting_device"] = BuiltInCategory.OST_LightingDevices,
            ["receptacle"] = BuiltInCategory.OST_ElectricalFixtures,
            ["fire_alarm"] = BuiltInCategory.OST_FireAlarmDevices,
            ["telephone"] = BuiltInCategory.OST_TelephoneDevices,
            ["lan"] = BuiltInCategory.OST_DataDevices,
            ["security"] = BuiltInCategory.OST_SecurityDevices,
            ["communication"] = BuiltInCategory.OST_CommunicationDevices,
            ["cable_tray"] = BuiltInCategory.OST_CableTray,
            ["panel"] = BuiltInCategory.OST_ElectricalEquipment,
            ["room"] = BuiltInCategory.OST_Rooms,
            ["space"] = BuiltInCategory.OST_MEPSpaces,
            ["door"] = BuiltInCategory.OST_Doors,
            ["window"] = BuiltInCategory.OST_Windows,
            ["wall"] = BuiltInCategory.OST_Walls,
            ["sheet"] = BuiltInCategory.OST_Sheets,
        };

    private static Level? FindLevel(Document doc, string name) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .FirstOrDefault(level => string.Equals(level.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .FirstOrDefault(level => level.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

    private static bool InRoom(Element element, SpatialElement? room)
    {
        if (room is null) return true;
        if (element.Id == room.Id) return true;

        var point = MidpointOf(element);
        return point is not null && RevitUtils.Contains(room, point);
    }

    /// <summary>
    /// The one point that stands for an element when asking which room it is in.
    ///
    /// Shared by the filter and the Room column deliberately: two readings of
    /// "where is this" that can disagree is what made a tray belong to a room and
    /// to no room at the same time.
    /// </summary>
    private static XYZ? MidpointOf(Element element) => element.Location switch
    {
        LocationPoint at => at.Point,
        // A tray or conduit sits in whichever room its midpoint falls in.
        LocationCurve curve => curve.Curve.Evaluate(0.5, true),
        _ => element.get_BoundingBox(null) is { } box
            ? box.Min.Add(box.Max).Multiply(0.5)
            : null,
    };

    private static List<string> Split(string raw) =>
        (raw ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToList();
}
