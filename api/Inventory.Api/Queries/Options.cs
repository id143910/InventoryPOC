using System.Data;
using Dapper;
using Inventory.Api.Schema;
using Microsoft.Extensions.Caching.Memory;

namespace Inventory.Api.Queries;

/// <summary>A (source type, relation, target type) triple present in the data.</summary>
public sealed record Shape(string SourceType, string RelationType, string TargetType, long Count);

/// <summary>A hop one entity actually has, with its rows counted.</summary>
public sealed record Available(Hop Hop, int Total, int Active)
{
    public int Frozen => Total - Active;
}

/// <summary>One field a condition at some stage can be about, and how it reads.</summary>
public sealed record Field(string Value, string Label, string Group, string Type);

/// <summary>
/// What a query can be built out of.
///
/// The shapes and their counts come from the data; the fields worth filtering on
/// come from the config. Between them they are everything a picker needs to offer,
/// which is why nothing in the builder is hard-coded.
/// </summary>
public sealed class Options(Config config, IMemoryCache cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    /// <summary>The graph's own schema, read back out of the relations.</summary>
    public IReadOnlyList<Shape> Shapes(IDbConnection db) =>
        cache.GetOrCreate("shapes", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Ttl;
            return db.Query<Shape>("""
                SELECT source_type AS SourceType, relation_type AS RelationType,
                       target_type AS TargetType, COUNT(*) AS Count
                FROM inventory_relation
                GROUP BY source_type, relation_type, target_type
                ORDER BY source_type, relation_type
                """).ToList();
        })!;

    /// <summary>Logical entities per type - what the type picker offers.</summary>
    public IReadOnlyList<(string Type, int Count)> TypeCounts(IDbConnection db) =>
        cache.GetOrCreate("types", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Ttl;
            return db.Query("""
                SELECT entity_type AS type, COUNT(DISTINCT natural_key) AS count
                FROM inventory_entity GROUP BY entity_type ORDER BY entity_type
                """).Select(row => ((string)row.type, (int)(long)row.count)).ToList();
        })!;

    /// <summary>Every hop a type permits - what the picker offers past a known entity.</summary>
    public IReadOnlyList<Hop> HopsFrom(IDbConnection db, string entityType)
    {
        var found = new HashSet<Hop>();
        foreach (var shape in Shapes(db))
        {
            if (shape.SourceType == entityType)
            {
                found.Add(new Hop(Config.Outgoing, shape.RelationType, shape.TargetType));
            }
            if (shape.TargetType == entityType)
            {
                found.Add(new Hop(Config.Incoming, shape.RelationType, shape.SourceType));
            }
        }
        return [.. found.OrderBy(hop => hop.Reads(config, entityType), StringComparer.Ordinal)];
    }

    /// <summary>
    /// The hops one entity actually has, counted.
    ///
    /// One statement covers both ends and counts active and frozen separately, so a
    /// hop can say "14 installs, 3 no longer reported".
    /// </summary>
    public IReadOnlyList<Available> HopsOf(IDbConnection db, string entityType, string naturalKey)
    {
        const string sql = """
            SELECT 'outgoing' AS direction, relation_type, target_type AS other_type,
                   COUNT(*) AS total,
                   SUM(CASE WHEN last_seen_at < synced_at THEN 0 ELSE 1 END) AS active
            FROM inventory_relation WHERE source_type = @type AND source_key = @key
            GROUP BY relation_type, target_type
            UNION ALL
            SELECT 'incoming', relation_type, source_type,
                   COUNT(*),
                   SUM(CASE WHEN last_seen_at < synced_at THEN 0 ELSE 1 END)
            FROM inventory_relation WHERE target_type = @type AND target_key = @key
            GROUP BY relation_type, source_type
            """;
        return [.. db.Query(sql, new { type = entityType, key = naturalKey })
            .Select(row => new Available(
                new Hop((string)row.direction, (string)row.relation_type, (string)row.other_type),
                (int)(long)row.total, (int)(long)row.active))
            .OrderBy(item => item.Hop.Reads(config, entityType), StringComparer.Ordinal)];
    }

    /// <summary>
    /// What a condition at this stage can be about.
    ///
    /// Stage 0 has no link, so only the entity's own fields; a stage with a hop
    /// offers the link's keys as well, grouped so it is obvious which is which.
    /// </summary>
    public IReadOnlyList<Field> Fields(Hop? hop, string standing)
    {
        var landed = config.Type(hop?.OtherType ?? standing);
        var found = new List<Field>();
        if (hop is not null)
        {
            found.AddRange(hop.Shape(config, standing).Filters
                .Select(key => new Field(
                    $"{Config.Link}:{key}", key.Replace('_', ' '), "on the link", config.FieldType(key))));
        }
        var group = $"on the {landed.Label.ToLowerInvariant()}";
        found.AddRange(landed.Filters
            .Select(key => new Field(
                $"{Config.Entity}:{key}", key.Replace('_', ' '), group, config.FieldType(key))));
        return found;
    }
}
