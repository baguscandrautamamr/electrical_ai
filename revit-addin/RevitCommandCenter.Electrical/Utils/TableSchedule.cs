using Autodesk.Revit.DB;

namespace RevitCommandCenter.Electrical.Utils;

/// <summary>
/// A spreadsheet as a real Schedules/Quantities view.
///
/// The other two targets of /import_table DRAW the table — detail lines and
/// text notes on a drafting view or a legend. That reproduces Excel exactly, and
/// what it produces is a picture: it cannot be filtered, sorted, or grouped, and
/// it does not follow the model.
///
/// A Revit schedule is not a table with cells. It is a query: rows are elements,
/// columns are their parameters. There is no API for "write this text in cell
/// B4", and there never will be — the value in a schedule cell belongs to
/// something in the model. So thirty rows of spreadsheet can only become thirty
/// rows of schedule if thirty elements exist to carry them.
///
/// That is what this does, and it is the same shape TableGen (DiRootsOne) uses:
///
///   1. One shared parameter per column, bound to Generic Models. Shared, not
///      project, parameters — only shared parameters can be scheduled fields,
///      and only they survive being read by another document.
///   2. One geometry-less DirectShape per spreadsheet row, carrying that row's
///      values. DirectShape rather than a family instance because it needs no
///      .rft template and no family file: the elements can be made from nothing
///      but the API, in any model, with nothing to install first.
///   3. A schedule of Generic Models, filtered to this table's own rows and
///      sorted back into the spreadsheet's row order.
///
/// The costs are real and are stated in the reply rather than discovered later:
/// the rows become elements that exist in the model (they appear in a Generic
/// Models schedule, in quantity takeoffs, and in IFC exports), the shared
/// parameters are added to the project, and Excel's own formatting — merged
/// cells, column widths, colours — is gone, because a schedule has formatting of
/// its own. Anyone who wants the table to look like the spreadsheet wants
/// target=schedule instead.
///
/// Nothing here is multi-category. A multi-category schedule would only restrict
/// what can be shown — its fields must all be shared parameters, which these
/// already are — and buys nothing while every row is a Generic Model. Where
/// multi-category earns its keep is a schedule over real devices from several
/// categories at once, which reads the model rather than a spreadsheet, and is a
/// different command.
/// </summary>
public static class TableSchedule
{
    /// <summary>
    /// Parameter names are namespaced so a column called "Nama" cannot collide
    /// with a project parameter of the same name that means something else. The
    /// schedule's own column heading is set back to the spreadsheet's wording,
    /// so what a reader sees is the header they wrote.
    /// </summary>
    private const string Prefix = "RCC ";

    /// <summary>Marks which imported table a row belongs to; the schedule filters on it.</summary>
    private const string TableParameter = Prefix + "Tabel";

    /// <summary>The spreadsheet's row number, so the schedule can be sorted back into order.</summary>
    private const string RowParameter = Prefix + "Baris";

    /// <summary>
    /// Beyond this the import is refused rather than run.
    ///
    /// Every row is an element and a transaction that creates ten thousand of
    /// them takes long enough that Revit looks hung. The number is far above any
    /// schedule anyone types by hand and far below where Revit starts to hurt.
    /// </summary>
    public const int MaxRows = 5_000;

    /// <summary>Columns past this are more than a printable schedule can carry.</summary>
    public const int MaxColumns = 60;

    public sealed record Result(string ViewName, int Rows, int Columns, int ReplacedRows);

    /// <summary>
    /// Builds (or rebuilds) the schedule. Must be called inside an open
    /// transaction — the caller owns it, so a failure here rolls back the
    /// elements too and cannot leave half a table behind.
    /// </summary>
    /// <param name="headers">Column headings, in spreadsheet order.</param>
    /// <param name="rows">Cell values, already trimmed to the header count.</param>
    /// <param name="name">Name for the schedule, and the marker on its rows.</param>
    public static Result Build(
        Document doc,
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> rows,
        string name)
    {
        if (headers.Count == 0)
        {
            throw new InvalidOperationException(
                "The first row of the sheet is used as the column headings, and it is empty. "
                + "Put a header row on the sheet, or import it as a drawn table instead.");
        }

        if (headers.Count > MaxColumns)
        {
            throw new InvalidOperationException(
                $"That sheet has {headers.Count} columns. A schedule takes at most {MaxColumns}; "
                + "import it as a drawn table instead, which has no such limit.");
        }

        if (rows.Count > MaxRows)
        {
            throw new InvalidOperationException(
                $"That sheet has {rows.Count} data rows. A schedule takes at most {MaxRows}, "
                + "because each row becomes an element in the model.");
        }

        var columnNames = UniqueParameterNames(headers);

        // Shared parameters first: the elements below cannot carry a value for a
        // parameter that is not bound to their category yet.
        EnsureParameters(doc, columnNames);

        var replaced = DeletePrevious(doc, name);

        var created = CreateRows(doc, rows, columnNames, name);

        var schedule = CreateSchedule(doc, name, headers, columnNames);

        Logger.Info(
            $"import_table: schedule '{schedule.Name}' with {created} row(s) "
            + $"x {headers.Count} column(s); {replaced} row(s) from a previous import removed");

        return new Result(schedule.Name, created, headers.Count, replaced);
    }

    // ------------------------------------------------------------- parameters

    /// <summary>
    /// The shared parameters this table needs, created once and reused after.
    ///
    /// The GUIDs are derived from the parameter name rather than generated, so
    /// importing the same spreadsheet next month reuses the same parameters
    /// instead of adding a second set that schedules and filters cannot tell
    /// apart.
    ///
    /// Revit's shared parameter file is a machine-wide setting that belongs to
    /// whoever is using Revit. This points it at a file of ours only for as long
    /// as the definitions are being read, then puts it back — an add-in that
    /// silently repoints it breaks the next person's parameter dialog, and they
    /// would have no reason to look here for the cause.
    /// </summary>
    private static void EnsureParameters(Document doc, IReadOnlyList<string> columnNames)
    {
        var app = doc.Application;
        var wanted = new List<string>(columnNames) { TableParameter, RowParameter };

        var originalFile = app.SharedParametersFilename;
        try
        {
            app.SharedParametersFilename = OurParameterFile();

            var file = app.OpenSharedParameterFile()
                ?? throw new InvalidOperationException(
                    "Revit could not open the add-in's shared parameter file, so the schedule "
                    + "has no columns to write into.");

            var group = file.Groups.get_Item("RevitCommandCenter")
                ?? file.Groups.Create("RevitCommandCenter");

            var definitions = new List<ExternalDefinition>(wanted.Count);
            foreach (var parameterName in wanted) definitions.Add(Define(group, parameterName));

            Bind(doc, definitions);

            // The bindings were made a moment ago, in a transaction that has not
            // committed. GetSchedulableFields below reads what the document
            // knows now — so the document is made to catch up first, rather than
            // the schedule being built with the columns it had before.
            doc.Regenerate();
        }
        finally
        {
            // Best effort: a machine whose original setting cannot be restored
            // is still better off than one left pointing at our file silently.
            try { app.SharedParametersFilename = originalFile; }
            catch (Exception ex) { Logger.Debug($"Could not restore the shared parameter file: {ex.Message}"); }
        }
    }

    private static ExternalDefinition Define(DefinitionGroup group, string parameterName)
    {
        foreach (var existing in group.Definitions)
        {
            if (existing is ExternalDefinition external
                && string.Equals(external.Name, parameterName, StringComparison.Ordinal))
            {
                return external;
            }
        }

        // Everything is text, including numbers.
        //
        // A spreadsheet column is not typed: one cell says 12, the next says
        // "12 (tentative)", and a Number parameter would refuse the second and
        // lose it. Text keeps every cell exactly as written. The cost is that
        // sorting a numeric column sorts it as words — which is why the row
        // order the spreadsheet had is kept in its own integer parameter and
        // used as the schedule's sort.
        var spec = string.Equals(parameterName, RowParameter, StringComparison.Ordinal)
            ? SpecTypeId.Int.Integer
            : SpecTypeId.String.Text;

        var options = new ExternalDefinitionCreationOptions(parameterName, spec)
        {
            GUID = StableGuid(parameterName),
            Visible = true,
            Description = "Dibuat /import_table dari sebuah spreadsheet.",
        };

        return (ExternalDefinition)group.Definitions.Create(options);
    }

    /// <summary>Binds the definitions to Generic Models, for instances.</summary>
    private static void Bind(Document doc, IEnumerable<ExternalDefinition> definitions)
    {
        var app = doc.Application;
        var category = doc.Settings.Categories.get_Item(BuiltInCategory.OST_GenericModel);

        var set = app.Create.NewCategorySet();
        set.Insert(category);

        var binding = app.Create.NewInstanceBinding(set);
        var bindings = doc.ParameterBindings;

        foreach (var definition in definitions)
        {
            if (bindings.Contains(definition)) continue;
            bindings.Insert(definition, binding, GroupTypeId.Data);
        }
    }

    /// <summary>
    /// A shared parameter file of our own, next to the add-in's config and logs.
    ///
    /// Not the user's file: writing into it would put our column names in front
    /// of everyone who opens the shared parameter dialog, in a project that has
    /// nothing to do with them.
    /// </summary>
    private static string OurParameterFile()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RevitCommandCenter");

        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "shared-parameters.txt");

        // Revit writes the header itself the first time a definition is created;
        // what it will not do is create the file.
        if (!File.Exists(path)) File.WriteAllText(path, string.Empty);

        return path;
    }

    /// <summary>
    /// The same name always yields the same GUID.
    ///
    /// A shared parameter is identified by its GUID, not its name. Generating a
    /// fresh one per import would make every re-import add a second "Nama"
    /// column that Revit considers unrelated to the first — visible as two
    /// identical headings in the field list, and as a filter that matches
    /// nothing.
    /// </summary>
    private static Guid StableGuid(string parameterName)
    {
        var seed = "RevitCommandCenter.ImportTable:" + parameterName.ToLowerInvariant();
        var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        return new Guid(hash);
    }

    /// <summary>
    /// Column headings turned into parameter names Revit accepts, kept distinct.
    ///
    /// Two columns headed the same is ordinary in a spreadsheet and impossible
    /// as two parameters, so the second is numbered. An empty heading gets a
    /// positional name rather than being dropped: its cells still hold data.
    /// </summary>
    private static List<string> UniqueParameterNames(IReadOnlyList<string> headers)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>(headers.Count);

        for (var i = 0; i < headers.Count; i++)
        {
            var cleaned = Clean(headers[i]);
            if (cleaned.Length == 0) cleaned = $"Kolom {i + 1}";

            var candidate = Prefix + cleaned;
            var suffix = 2;
            while (!used.Add(candidate))
            {
                candidate = $"{Prefix}{cleaned} ({suffix++})";
            }

            names.Add(candidate);
        }

        return names;
    }

    /// <summary>Characters Revit refuses in a parameter name, and runs of space.</summary>
    private static string Clean(string heading) =>
        string.Join(
            " ",
            (heading ?? string.Empty).Split(
                new[] { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~', '\n', '\r', '\t' },
                StringSplitOptions.RemoveEmptyEntries))
            .Trim();

    // ----------------------------------------------------------------- rows

    /// <summary>
    /// One element per spreadsheet row.
    ///
    /// A DirectShape with a single point for geometry: it has to be something,
    /// and a point is the least of it — invisible in plans and sections, no
    /// surface to cut or clash, nothing to print. What matters is that it is a
    /// real element of the Generic Models category, because that is the only
    /// thing a schedule can put in a row.
    /// </summary>
    private static int CreateRows(
        Document doc,
        IReadOnlyList<IReadOnlyList<string>> rows,
        IReadOnlyList<string> columnNames,
        string tableName)
    {
        var categoryId = new ElementId(BuiltInCategory.OST_GenericModel);
        var created = 0;

        for (var r = 0; r < rows.Count; r++)
        {
            var element = DirectShape.CreateElement(doc, categoryId);
            SetMarkerShape(element);

            Write(element, TableParameter, tableName);
            WriteInt(element, RowParameter, r + 1);

            var cells = rows[r];
            for (var c = 0; c < columnNames.Count; c++)
            {
                Write(element, columnNames[c], c < cells.Count ? cells[c] : string.Empty);
            }

            created++;
        }

        return created;
    }

    /// <summary>
    /// The smallest thing a DirectShape can be made of, tried in order.
    ///
    /// Which geometry kinds a DirectShape accepts is not worth asserting from
    /// here — it is answered by Revit, at runtime, and differently per category.
    /// So both are offered and the element keeps whichever it took. An element
    /// that took neither still schedules: geometry is what makes it visible, not
    /// what makes it exist.
    /// </summary>
    private static IEnumerable<GeometryObject> MarkerGeometries()
    {
        yield return Point.Create(XYZ.Zero);
        yield return Line.CreateBound(XYZ.Zero, new XYZ(1 / 304.8, 0, 0));
    }

    private static void SetMarkerShape(DirectShape element)
    {
        foreach (var geometry in MarkerGeometries())
        {
            try
            {
                element.SetShape(new List<GeometryObject> { geometry });
                return;
            }
            catch (Exception ex)
            {
                Logger.Debug($"import_table: marker geometry refused: {ex.Message}");
            }
        }
    }

    private static void Write(Element element, string parameterName, string value)
    {
        var parameter = element.LookupParameter(parameterName);
        if (parameter is null || parameter.IsReadOnly) return;
        parameter.Set(value ?? string.Empty);
    }

    private static void WriteInt(Element element, string parameterName, int value)
    {
        var parameter = element.LookupParameter(parameterName);
        if (parameter is null || parameter.IsReadOnly) return;
        parameter.Set(value);
    }

    // ------------------------------------------------------------- schedule

    /// <summary>
    /// Rows from a previous import of the same table, removed.
    ///
    /// Importing the same spreadsheet twice is a revision, not a mistake — and
    /// without this, the second import doubles every row while the schedule
    /// gives no hint which half is current.
    /// </summary>
    private static int DeletePrevious(Document doc, string tableName)
    {
        var stale = new FilteredElementCollector(doc)
            .OfClass(typeof(DirectShape))
            .OfCategory(BuiltInCategory.OST_GenericModel)
            .Where(element =>
                string.Equals(
                    element.LookupParameter(TableParameter)?.AsString(),
                    tableName,
                    StringComparison.Ordinal))
            .Select(element => element.Id)
            .ToList();

        if (stale.Count > 0) doc.Delete(stale);

        // The schedule itself is rebuilt rather than reused: its fields follow
        // the spreadsheet's columns, and those can change between imports.
        var previous = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSchedule))
            .Cast<ViewSchedule>()
            .Where(view => !view.IsTemplate && string.Equals(view.Name, tableName, StringComparison.Ordinal))
            .Select(view => view.Id)
            .ToList();

        if (previous.Count > 0) doc.Delete(previous);

        return stale.Count;
    }

    private static ViewSchedule CreateSchedule(
        Document doc,
        string name,
        IReadOnlyList<string> headers,
        IReadOnlyList<string> columnNames)
    {
        var schedule = ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_GenericModel));
        schedule.Name = UniqueViewName(doc, name);

        var definition = schedule.Definition;
        definition.IsItemized = true;

        var schedulable = definition.GetSchedulableFields()
            .GroupBy(field => field.GetName(doc), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        // Order matters: the columns appear in the spreadsheet's order, and the
        // two bookkeeping fields go last so that hiding them cannot leave a gap
        // in the middle.
        for (var i = 0; i < columnNames.Count; i++)
        {
            if (!schedulable.TryGetValue(columnNames[i], out var field)) continue;

            var added = definition.AddField(field);

            // The heading a reader sees is the spreadsheet's, not the
            // namespaced parameter name behind it.
            var heading = (headers[i] ?? string.Empty).Trim();
            if (heading.Length > 0) added.ColumnHeading = heading;
        }

        // Filter: this schedule shows this table's rows and nothing else. Every
        // other Generic Model in the project — and every other imported table —
        // fails the test, which is what makes two imported tables two schedules
        // rather than one of everything.
        if (schedulable.TryGetValue(TableParameter, out var tableField))
        {
            var added = definition.AddField(tableField);
            added.IsHidden = true;
            definition.AddFilter(new ScheduleFilter(added.FieldId, ScheduleFilterType.Equal, name));
        }

        // Sort: back into the spreadsheet's own row order. Without it the rows
        // come out in whatever order Revit found the elements, which is not the
        // order anybody typed them in.
        if (schedulable.TryGetValue(RowParameter, out var rowField))
        {
            var added = definition.AddField(rowField);
            added.IsHidden = true;
            definition.AddSortGroupField(
                new ScheduleSortGroupField(added.FieldId, ScheduleSortOrder.Ascending));
        }

        return schedule;
    }

    /// <summary>A view name Revit will accept, and that no other view has taken.</summary>
    private static string UniqueViewName(Document doc, string wanted)
    {
        var baseName = Clean(wanted);
        if (baseName.Length == 0) baseName = "Imported table";

        var taken = new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Select(view => view.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!taken.Contains(baseName)) return baseName;

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!taken.Contains(candidate)) return candidate;
        }

        return $"{baseName} ({Guid.NewGuid():N})";
    }
}
