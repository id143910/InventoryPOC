using System.Data;
using Dapper;
using Inventory.Api.Queries;
using Inventory.Api.Schema;

namespace Inventory.Api.Entities;

/// <summary>A key with no rows and no relations.</summary>
public sealed class NotFound(string message) : Exception(message);

/// <summary>One source's row: everything that system said about one entity.</summary>
public sealed record Facet(
    string Source,
    string SourceType,
    string? ExternalId,
    DateTime? DiscoveredAt,
    DateTime? LastSeenAt,
    DateTime? SyncedAt,
    IReadOnlyDictionary<string, object?> Metadata)
{
    public bool Frozen => LastSeenAt is not null && SyncedAt is not null && LastSeenAt < SyncedAt;
}

/// <summary>One merged header field: the trusted value, and who disagreed.</summary>
public sealed record Value(
    string Key,
    string Label,
    object? Trusted,
    string Source,
    IReadOnlyDictionary<string, object?> Reported)
{
    public bool Mismatch => Reported.Values.Select(item => $"{item}").Distinct().Count() > 1;

    public string Disagreement => string.Join(", ",
        Reported.Where(entry => entry.Key != Source).Select(entry => $"{entry.Key} says {entry.Value}"));
}

/// <summary>A logical entity: one key, every source's account of it, merged.</summary>
public sealed record EntityView(
    string EntityType,
    string NaturalKey,
    string Label,
    IReadOnlyList<Facet> Facets,
    IReadOnlyList<Value> Values)
{
    /// <summary>False for a key that exists only at the end of a relation.</summary>
    public bool Known => Facets.Count > 0;

    public IReadOnlyList<string> Sources => [.. Facets.Select(item => item.Source)];

    public IReadOnlyList<Value> Mismatches => [.. Values.Where(item => item.Mismatch)];
}

/// <summary>One entity on the other end of a relation, and how often it is there.</summary>
public sealed record Neighbour(string EntityType, string NaturalKey, int Occurrences);

/// <summary>
/// One shape an entity takes part in, counted in entities rather than in rows.
///
/// "installed on 12 servers" is the useful sentence, so <c>Neighbours</c> is what a
/// box leads with; <c>Occurrences</c> is the larger number behind it, because the
/// same certificate can be installed on one host more than once.
///
/// A box names its neighbours when there are few enough to read and counts them when
/// there are not. Either way it is a link into the query builder, which is where
/// rows, metadata and filtering live.
/// </summary>
public sealed record Box(
    string OwnType,
    Hop Hop,
    int Neighbours,
    int Occurrences,
    int Frozen,
    IReadOnlyList<Neighbour> Named)
{
    /// <summary>True when the box names every neighbour it has.</summary>
    public bool Listed => Named.Count == Neighbours;

    public int Others => Neighbours - Named.Count;
}

/// <summary>
/// Entity reads: one key, every source's account of it, merged.
///
/// <c>(entity_type, natural_key)</c> is the logical entity; the rows in
/// <c>inventory_entity</c> are what each system says about it. The merge takes each
/// declared field from the most trusted source that reports it and keeps every
/// source's value beside it, so a disagreement is shown rather than averaged away.
///
/// Relations point at keys, so a key can be reachable through them while no system
/// inventories it. That comes back flagged rather than dropped.
/// </summary>
public sealed class EntityReader(Config config)
{
    /// <summary>Neighbours a box names before it gives up and counts the rest.</summary>
    public const int Listed = 8;
    public const int Named = 3;

    /// <summary>
    /// Every source's rows for a set of logical entities, in one statement. Keys
    /// with no rows come back too, as views with no facets.
    /// </summary>
    public Dictionary<(string, string), EntityView> Views(IDbConnection db, IReadOnlyList<EntityKey> keys)
    {
        var wanted = keys.Distinct().ToList();
        var collected = wanted.ToDictionary(key => (key.Type, key.Key), _ => new List<Facet>());
        if (wanted.Count == 0)
        {
            return [];
        }

        var byType = wanted.GroupBy(key => key.Type).ToList();
        var clauses = byType.Select((group, index) =>
            $"(entity_type = @t{index} AND natural_key IN @k{index})");
        var parameters = new DynamicParameters();
        foreach (var (group, index) in byType.Select((group, index) => (group, index)))
        {
            parameters.Add($"t{index}", group.Key);
            parameters.Add($"k{index}", group.Select(item => item.Key).ToArray());
        }

        var rows = db.Query($"""
            SELECT entity_type, natural_key, source, source_type, external_id,
                   discovered_at, last_seen_at, synced_at, metadata
            FROM inventory_entity WHERE {string.Join(" OR ", clauses)}
            """, parameters);

        foreach (var row in rows)
        {
            var key = ((string)row.entity_type, (string)row.natural_key);
            if (collected.TryGetValue(key, out var bucket))
            {
                bucket.Add(new Facet(
                    (string)row.source, (string)row.source_type, (string?)row.external_id,
                    Stamp(row.discovered_at), Stamp(row.last_seen_at), Stamp(row.synced_at),
                    Runner.Metadata((string?)row.metadata)));
            }
        }

        return collected.ToDictionary(entry => entry.Key, entry => Merge(entry.Key.Item1, entry.Key.Item2, entry.Value));
    }

    /// <summary>One entity. A key that is neither inventoried nor referenced is missing.</summary>
    public EntityView View(IDbConnection db, string entityType, string naturalKey)
    {
        var view = Views(db, [new EntityKey(entityType, naturalKey)])[(entityType, naturalKey)];
        if (view.Known)
        {
            return view;
        }
        var referenced = db.ExecuteScalar<long>("""
            SELECT EXISTS (SELECT 1 FROM inventory_relation
                           WHERE (source_type = @type AND source_key = @key)
                              OR (target_type = @type AND target_key = @key))
            """, new { type = entityType, key = naturalKey });
        if (referenced == 0)
        {
            throw new NotFound($"{entityType} '{naturalKey}' not found");
        }
        return view;
    }

    /// <summary>
    /// Every shape this entity takes part in, with a few of its neighbours named.
    ///
    /// Two statements whatever the entity: one counts the shapes, the other names the
    /// first handful of neighbours in each with a window function. So a certificate on
    /// three hosts and a package on two thousand cost the same, and neither drags
    /// every neighbour into memory to be thrown away.
    /// </summary>
    public IReadOnlyList<Box> Neighbourhood(IDbConnection db, string entityType, string naturalKey, bool frozen)
    {
        var active = frozen ? "" : "AND last_seen_at >= synced_at";
        var perNeighbour = $"""
            SELECT 'outgoing' AS direction, relation_type, target_type AS other_type, target_key AS other_key,
                   COUNT(*) AS occurrences,
                   SUM(CASE WHEN last_seen_at < synced_at THEN 1 ELSE 0 END) AS frozen
            FROM inventory_relation WHERE source_type = @type AND source_key = @key {active}
            GROUP BY relation_type, target_type, target_key
            UNION ALL
            SELECT 'incoming', relation_type, source_type, source_key,
                   COUNT(*),
                   SUM(CASE WHEN last_seen_at < synced_at THEN 1 ELSE 0 END)
            FROM inventory_relation WHERE target_type = @type AND target_key = @key {active}
            GROUP BY relation_type, source_type, source_key
            """;
        var parameters = new { type = entityType, key = naturalKey };

        var counted = db.Query($"""
            SELECT direction, relation_type, other_type,
                   COUNT(*) AS neighbours, SUM(occurrences) AS occurrences, SUM(frozen) AS frozen
            FROM ({perNeighbour}) GROUP BY direction, relation_type, other_type
            """, parameters).ToList();
        if (counted.Count == 0)
        {
            return [];
        }

        var named = db.Query($"""
            SELECT direction, relation_type, other_type, other_key, occurrences FROM (
                SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY direction, relation_type, other_type ORDER BY other_key) AS rank
                FROM ({perNeighbour})
            ) WHERE rank <= {Listed} ORDER BY rank
            """, parameters)
            .GroupBy(row => ((string)row.direction, (string)row.relation_type, (string)row.other_type))
            .ToDictionary(group => group.Key, group => group
                .Select(row => new Neighbour((string)row.other_type, (string)row.other_key, (int)(long)row.occurrences))
                .ToList());

        return [.. counted
            .Select(row =>
            {
                var hop = new Hop((string)row.direction, (string)row.relation_type, (string)row.other_type);
                var neighbours = (int)(long)row.neighbours;
                var found = named.TryGetValue((hop.Direction, hop.RelationType, hop.OtherType), out var list)
                    ? list : [];
                // Name them all while the list is short enough to read; otherwise
                // name a few and let the count carry the rest.
                return new Box(entityType, hop, neighbours, (int)(long)row.occurrences, (int)(long)row.frozen,
                    neighbours <= Listed ? found : [.. found.Take(Named)]);
            })
            .OrderBy(box => box.Hop.Reads(config, entityType), StringComparer.Ordinal)];
    }

    /// <summary>
    /// The header: each declared field from the most trusted source reporting it,
    /// with every source's value kept beside it, so a disagreement is shown rather
    /// than averaged away.
    /// </summary>
    private EntityView Merge(string entityType, string naturalKey, List<Facet> facets)
    {
        // A person first, then the config's authority - so an override wins by being
        // ranked above the syncs, not by being a special case here.
        var order = config.Ranking(entityType);
        var ranked = facets
            .OrderBy(facet => Array.IndexOf(order, facet.Source) is var at && at >= 0 ? at : order.Length)
            .ThenBy(facet => facet.Source, StringComparer.Ordinal)
            .ToList();

        var values = new List<Value>();
        foreach (var field in config.Type(entityType).Unified)
        {
            var reported = new Dictionary<string, object?>();
            foreach (var facet in ranked)
            {
                if (facet.Metadata.TryGetValue(field.Key, out var found) && found is not null && $"{found}".Length > 0)
                {
                    reported[facet.Source] = found;
                }
            }
            if (reported.Count > 0)
            {
                var trusted = reported.Keys.First();
                values.Add(new Value(field.Key, field.Label, reported[trusted], trusted, reported));
            }
        }
        return new EntityView(entityType, naturalKey, config.Label(entityType), ranked, values);
    }

    private static DateTime? Stamp(object? value) =>
        value is null ? null : Convert.ToDateTime(value);
}
