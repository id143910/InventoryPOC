using System.Data;
using System.Text.Json;
using Dapper;
using Inventory.Api.Schema;

namespace Inventory.Api.Queries;

/// <summary>A page of whatever a query landed on.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, int Total, int PageNumber, int PageSize)
{
    public int Pages => Math.Max(1, (Total + PageSize - 1) / PageSize);
}

/// <summary>One entity, as a query with no hops lands on it.</summary>
public sealed record EntityKey(string Type, string Key);

/// <summary>One value a condition's field takes, and how often.</summary>
public sealed record ValueCount(string Value, long Count);

/// <summary>One relation row: the entity reached, and what makes this row distinct.</summary>
public sealed record Occurrence(
    string FromType,
    string FromKey,
    string ToType,
    string ToKey,
    IReadOnlyDictionary<string, object?> Metadata,
    string Source,
    DateTime? LastSeenAt,
    bool Frozen);

/// <summary>
/// Running a query.
///
/// Every stage is a set of <c>(entity_type, natural_key)</c> pairs. Stage 0 comes
/// from the entity table; each stage after it joins the relation table to the pairs
/// the last one produced. So the whole chain is one statement however long it is,
/// and it touches the entity table only where a condition asks it to.
///
/// The SQL is written out rather than composed by a mapper, because the SQL *is*
/// the interesting part here - a CTE per stage, a window function for a
/// neighbourhood, <c>MAX(CASE ...)</c> where a condition has to hold for an entity
/// rather than for one of its source rows. You can read what the database will do.
/// </summary>
public sealed class Runner(Config config)
{
    private const string Active = "r.last_seen_at >= r.synced_at";

    /// <summary>
    /// The keys a query with no hops lands on - one per entity, however many
    /// systems describe it. Two statements: one to count, one for the page.
    /// </summary>
    public Page<EntityKey> Keys(IDbConnection db, Query query)
    {
        query.Validate();
        var sql = new Bag();
        return Paged(db, Start(query, sql), sql, query, "key",
            rows => db.Query<EntityKey>(rows, sql.Parameters).ToList());
    }

    /// <summary>
    /// The occurrences of a query's last hop. Two statements however long the
    /// chain, because every stage is a set operation inside one of them.
    /// </summary>
    public Page<Occurrence> Occurrences(IDbConnection db, Query query)
    {
        query.Validate();
        if (query.ListsEntities)
        {
            throw new QueryError("a query with no hops lands on entities, not occurrences");
        }
        var sql = new Bag();
        var last = query.Stages.Count - 1;
        var rows = Crossing(query, last, Reached(query, last - 1, sql), sql, OccurrenceColumns);
        return Paged(db, rows, sql, query, "to_key, from_key, relation_id",
            page => db.Query(page, sql.Parameters).Select(Read).ToList());
    }

    private Page<T> Paged<T>(IDbConnection db, string rows, Bag sql, Query query, string order,
                             Func<string, List<T>> read)
    {
        var total = db.ExecuteScalar<int>($"SELECT COUNT(*) FROM ({rows})", sql.Parameters);
        if (total == 0)
        {
            return new Page<T>([], 0, query.Page, query.PageSize);
        }
        var page = $"SELECT * FROM ({rows}) ORDER BY {order} LIMIT {sql.Of(query.PageSize)} OFFSET {sql.Of(query.Offset)}";
        return new Page<T>(read(page), total, query.Page, query.PageSize);
    }

    /// <summary>
    /// The values one condition's field actually takes - what its picker offers.
    ///
    /// Read from the rows the stage crosses, so a choice is only ever something that
    /// would match. One statement, commonest first, one past the cap so the caller
    /// can say the list is not the whole of it.
    /// </summary>
    public IReadOnlyList<ValueCount> Values(IDbConnection db, Query query, int stage, int condition)
    {
        var asked = query.Stages[stage].Conditions[condition];
        if (asked.Field.Length == 0)
        {
            return [];
        }
        var probe = query.Probing(stage, condition);
        var sql = new Bag();
        string rows;

        if (stage == 0)
        {
            // The values across the entities stage 0 selects, so this condition's
            // siblings narrow the list exactly as they narrow the table.
            var column = Display(EntityColumn("e", asked.Name));
            rows = $"""
                SELECT {column} AS value, COUNT(DISTINCT e.natural_key) AS count
                FROM inventory_entity e
                JOIN ({Start(probe, sql)}) s ON s.type = e.entity_type AND s.key = e.natural_key
                WHERE {column} IS NOT NULL
                GROUP BY value
                """;
        }
        else
        {
            var (column, counter, join) = CrossingColumn(probe, stage, asked);
            var crossing = Crossing(probe, stage, Reached(probe, stage - 1, sql), sql,
                $"{column} AS value, {counter} AS count", group: $"GROUP BY {column}", join: join);
            rows = $"SELECT * FROM ({crossing}) WHERE value IS NOT NULL";
        }

        // A tags field's value is a list; the picker wants the tags in it, counted one
        // by one. Both branches above produce (value, count), so one wrapper does it
        // whichever stage asked.
        if (config.FieldType(asked.Name) == Config.Tags)
        {
            rows = $"""
                SELECT tag.value AS value, SUM(listed.count) AS count
                FROM ({rows}) listed, json_each(listed.value) tag
                GROUP BY tag.value
                """;
        }

        return db.Query<ValueCount>(
            $"SELECT value, count FROM ({rows}) ORDER BY count DESC, value LIMIT {Query.MaxValues + 1}",
            sql.Parameters).ToList();
    }

    // ----------------------------------------------------------------- stages

    /// <summary>
    /// Stage 0: one named entity, or a filtered set of a type.
    ///
    /// Grouped by the key rather than DISTINCT, which both collapses the several
    /// sources describing one entity and gives each condition somewhere to be tested
    /// once per entity instead of once per row.
    /// </summary>
    private string Start(Query query, Bag sql)
    {
        if (query.Key is not null)
        {
            return $"SELECT {sql.Of(query.Type)} AS type, {sql.Of(query.Key)} AS key";
        }

        var stage = query.Stages[0];
        var where = new List<string> { $"entity_type = {sql.Of(query.Type)}" };

        // A condition on the key is about the group itself, so when every condition
        // has to hold it can filter rows before they are grouped, using the index as
        // it goes. Under "any" it has to be weighed with the others instead.
        var early = stage.Filters
            .Where(item => stage.Match == Stage.All && item.Name == "natural_key")
            .ToList();
        where.AddRange(early.Select(item => Compare("natural_key", item, sql)));

        // An entity is one row per source, so a condition is asked of the group: it
        // holds when any source makes the claim, and a denial holds when none of them
        // does. Asking a denial row by row would let one system's silence overturn
        // another's assertion - every server would be "not production" because SCCM
        // never mentioned its environment.
        var tests = stage.Filters.Except(early)
            .Select(item =>
                $"MAX(CASE WHEN {Compare(EntityColumn(null, item.Name), item.Claim, sql)} THEN 1 ELSE 0 END) "
                + $"= {(item.Denies ? 0 : 1)}")
            .ToList();
        var having = tests.Count == 0
            ? ""
            : $"HAVING {string.Join(stage.Match == Stage.All ? " AND " : " OR ", tests)}";

        return $"""
            SELECT entity_type AS type, natural_key AS key
            FROM inventory_entity
            WHERE {string.Join(" AND ", where)}
            GROUP BY entity_type, natural_key
            {having}
            """;
    }

    /// <summary>The pairs standing at the end of a stage, as a chain of CTEs.</summary>
    private string Reached(Query query, int upto, Bag sql)
    {
        var reached = Start(query, sql);
        for (var index = 1; index <= upto; index++)
        {
            // Every stage deduplicates, which is what stops a fan-out from
            // multiplying out and stops a certificate installed three times on one
            // host from dragging that host through three times.
            reached = Crossing(query, index, reached, sql, "DISTINCT r.{far_type} AS type, r.{far_key} AS key");
        }
        return reached;
    }

    /// <summary>The relation rows one stage crosses, joined to where the last landed.</summary>
    private string Crossing(Query query, int index, string reached, Bag sql, string columns,
                            string group = "", string? join = null)
    {
        var stage = query.Stages[index];
        var hop = stage.Hop!;
        var (ownType, ownKey, farType, farKey) = Sides(hop.Direction);
        columns = columns
            .Replace("{far_type}", farType).Replace("{far_key}", farKey)
            .Replace("{own_type}", ownType).Replace("{own_key}", ownKey);

        var where = new List<string>
        {
            $"r.relation_type = {sql.Of(hop.RelationType)}",
            $"r.{farType} = {sql.Of(hop.OtherType)}",
        };
        if (!query.Frozen)
        {
            where.Add(Active);
        }
        var test = Filter(stage, $"r.{farType}", $"r.{farKey}", sql);
        if (test is not null)
        {
            where.Add(test);
        }

        return $"""
            SELECT {columns}
            FROM inventory_relation r
            JOIN ({reached}) p ON r.{ownType} = p.type AND r.{ownKey} = p.key
            {join ?? ""}
            WHERE {string.Join(" AND ", where)}
            {group}
            """;
    }

    private const string OccurrenceColumns = """
        r.{far_type} AS to_type, r.{far_key} AS to_key,
        r.{own_type} AS from_type, r.{own_key} AS from_key,
        r.relation_source AS source, r.last_seen_at, r.synced_at,
        r.metadata, r.relation_id
        """;

    // ------------------------------------------------------------- conditions

    /// <summary>A stage's conditions, joined by whether it wants all of them or any.</summary>
    private string? Filter(Stage stage, string typeColumn, string keyColumn, Bag sql)
    {
        var tests = stage.Filters
            .Select(item => item.AboutLink
                ? Compare($"json_extract(r.metadata, '$.{item.Name}')", item, sql)
                : EntityTest(item, typeColumn, keyColumn, sql))
            .ToList();
        return tests.Count == 0
            ? null
            : "(" + string.Join(stage.Match == Stage.All ? " AND " : " OR ", tests) + ")";
    }

    /// <summary>
    /// A condition on the entity a stage lands on.
    ///
    /// An EXISTS rather than a join, so the stage still resolves from its own index
    /// and asks the entity table only about the rows that survived - and so the rule
    /// is "any source says so", which is what you want when two systems describe the
    /// same thing. A condition on the key needs no lookup at all: the key is here.
    /// </summary>
    private string EntityTest(Condition condition, string typeColumn, string keyColumn, Bag sql)
    {
        if (condition.Name == "natural_key")
        {
            return Compare(keyColumn, condition, sql);
        }
        var column = EntityColumn("c", condition.Name);
        return $"""
            EXISTS (SELECT 1 FROM inventory_entity c
                    WHERE c.entity_type = {typeColumn} AND c.natural_key = {keyColumn}
                      AND {Compare(column, condition, sql)})
            """;
    }

    /// <summary>The column one field names, on whichever entity row is in hand.</summary>
    /// <summary>
    /// The column one field names. Uncast: <see cref="Compare"/> casts it to the type
    /// the field declares, and <see cref="Display"/> casts it to text where the values
    /// are only being read. The name was validated when the condition was built, so
    /// it can be a JSON path rather than a parameter.
    /// </summary>
    private static string EntityColumn(string? alias, string name)
    {
        var prefix = alias is null ? "" : alias + ".";
        return name switch
        {
            "natural_key" => $"{prefix}natural_key",
            "external_id" => $"{prefix}external_id",
            _ => $"json_extract({prefix}metadata, '$.{name}')",
        };
    }

    /// <summary>As text, for listing the values a field takes.</summary>
    private static string Display(string column) => $"CAST({column} AS TEXT)";

    /// <summary>
    /// The comparison one condition makes, at the type its field declares.
    ///
    /// A date is an ISO-8601 string in the metadata, so it already orders correctly
    /// as text and needs no conversion. A number does not order as text ("9" > "10"),
    /// so it is read as one on both sides.
    /// </summary>
    private string Compare(string column, Condition condition, Bag sql)
    {
        var type = condition.FieldType(config);
        if (type == Config.Tags)
        {
            // One entity carries several tags, so the test is membership of the list
            // rather than a comparison with it. An entity with no list has no tag in
            // it, which makes `excludes` true of it - as it should be.
            var wanted = sql.Of(condition.Value);
            var carries = $"EXISTS (SELECT 1 FROM json_each(IFNULL({column}, '[]')) WHERE value = {wanted})";
            return condition.Operator == Config.Excludes ? $"NOT {carries}" : carries;
        }
        column = type == Config.Number ? $"CAST({column} AS REAL)" : $"CAST({column} AS TEXT)";
        var value = sql.Of(Bind(condition, type));
        return condition.Operator switch
        {
            Config.Contains => $"{column} LIKE '%' || {value} || '%'",
            // IS NOT, not <>, so a row that never reported the field still counts as
            // "not that value" rather than dropping out on a NULL comparison.
            Config.IsNot => $"{column} IS NOT {value}",
            Config.Before or Config.Less => $"{column} < {value}",
            Config.After or Config.Greater => $"{column} > {value}",
            _ => $"{column} = {value}",
        };
    }

    /// <summary>The value, as the type its field is compared at.</summary>
    private static object Bind(Condition condition, string type)
    {
        // A date may be written relative to today, which is the form worth saving.
        if (type == Config.Date)
        {
            return Moment.Resolve(condition.Value);
        }
        if (type != Config.Number)
        {
            return condition.Value;
        }
        if (!double.TryParse(condition.Value, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            throw new QueryError($"{condition.Name} reads as a number, and '{condition.Value}' is not one");
        }
        return parsed;
    }

    private (string Column, string Counter, string? Join) CrossingColumn(Query query, int stage, Condition asked)
    {
        var hop = query.Stages[stage].Hop!;
        var (_, _, farType, farKey) = Sides(hop.Direction);
        if (asked.AboutLink)
        {
            return (Display($"json_extract(r.metadata, '$.{asked.Name}')"), "COUNT(*)", null);
        }
        if (asked.Name == "natural_key")
        {
            return ($"r.{farKey}", "COUNT(*)", null);
        }
        // One relation meets several source rows for one entity, so count relations.
        return (EntityColumn("v", asked.Name), "COUNT(DISTINCT r.relation_id)",
            $"JOIN inventory_entity v ON v.entity_type = r.{farType} AND v.natural_key = r.{farKey}");
    }

    internal static (string OwnType, string OwnKey, string FarType, string FarKey) Sides(string direction) =>
        direction == Config.Outgoing
            ? ("source_type", "source_key", "target_type", "target_key")
            : ("target_type", "target_key", "source_type", "source_key");

    private static Occurrence Read(dynamic row)
    {
        DateTime? lastSeen = row.last_seen_at is null ? null : Convert.ToDateTime(row.last_seen_at);
        DateTime? synced = row.synced_at is null ? null : Convert.ToDateTime(row.synced_at);
        return new Occurrence(
            (string)row.from_type, (string)row.from_key, (string)row.to_type, (string)row.to_key,
            Metadata((string?)row.metadata), (string)row.source, lastSeen,
            lastSeen is not null && synced is not null && lastSeen < synced);
    }

    internal static IReadOnlyDictionary<string, object?> Metadata(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new Dictionary<string, object?>();
        }
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(raw) ?? [];
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>();
        }
    }

    /// <summary>
    /// The parameters a statement collects as it is built.
    ///
    /// Every value in the SQL above goes through <see cref="Of"/>, so the strings
    /// hold no data - only column names and field names that were validated when the
    /// condition was built.
    /// </summary>
    private sealed class Bag
    {
        private int _next;

        public DynamicParameters Parameters { get; } = new();

        public string Of(object? value)
        {
            var name = $"p{_next++}";
            Parameters.Add(name, value);
            return "@" + name;
        }
    }
}
