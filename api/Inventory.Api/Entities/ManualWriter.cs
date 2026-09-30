using System.Data;
using System.Text.Json.Nodes;
using Dapper;
using Inventory.Api.Queries;
using Inventory.Api.Schema;

namespace Inventory.Api.Entities;

/// <summary>
/// What a person says about an entity: the only write in the application.
///
/// It does not edit what the syncs own. It writes one more row of
/// <c>inventory_entity</c>, from the source the config calls manual, and that source
/// outranks the others - so the person's value is the merged one and each system's
/// value is still there beside it, flagged as a disagreement. Nothing is lost and
/// nothing is overwritten; the graph simply has one more account of the thing, and
/// the merge that already existed does the rest.
///
/// Clearing the last field deletes the row, so there are no empty manual rows and no
/// phantom source on an entity nobody has annotated. A key the graph has never heard
/// of is refused, because accepting it would invent an entity out of a typo.
/// </summary>
public sealed class ManualWriter(Config config, EntityReader reader)
{
    /// <summary>
    /// Merge these fields into the manual row, and hand back the entity as it now
    /// reads. A field given as null, blank or an empty list is an override let go of.
    /// </summary>
    public EntityView Set(
        IDbConnection db, string entityType, string naturalKey,
        IReadOnlyDictionary<string, JsonNode?> asked)
    {
        // Refuses a key nothing inventories and no relation mentions.
        reader.View(db, entityType, naturalKey);

        var known = config.Type(entityType).Unified.Select(field => field.Key).ToHashSet();
        var unknown = asked.Keys.Where(key => !known.Contains(key)).ToList();
        if (unknown.Count > 0)
        {
            throw new QueryError(
                $"{string.Join(", ", unknown)} is not a field of a {config.Label(entityType).ToLowerInvariant()}; "
                + $"it can be given {string.Join(", ", known)}");
        }

        var arguments = new { type = entityType, key = naturalKey, source = config.Manual.Source };
        // First by age, because the table has no uniqueness of its own: if two
        // annotations ever raced, the reads merge both rows and this one heals them.
        var current = db.QueryFirstOrDefault<string?>("""
            SELECT metadata FROM inventory_entity
            WHERE entity_type = @type AND natural_key = @key AND source = @source
            ORDER BY entity_id
            """, arguments);

        var metadata = (JsonNode.Parse(current ?? "{}") as JsonObject) ?? [];
        foreach (var (field, given) in asked)
        {
            var value = given is null ? null : Listed(field, given);
            if (Blank(value))
            {
                metadata.Remove(field);
            }
            else
            {
                metadata[field] = value!;
            }
        }

        // The same instant in all three: a row a person just wrote is not stale, so
        // last_seen_at is never behind synced_at and it never reads as frozen.
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.ffffff");
        var written = new
        {
            arguments.type, arguments.key, arguments.source,
            source_type = config.Manual.SourceType,
            metadata = metadata.ToJsonString(),
            now,
        };

        if (metadata.Count == 0)
        {
            // Nothing left to say, so nothing left to be a source of.
            db.Execute("""
                DELETE FROM inventory_entity
                WHERE entity_type = @type AND natural_key = @key AND source = @source
                """, arguments);
        }
        else if (db.Execute("""
            UPDATE inventory_entity SET metadata = @metadata, last_seen_at = @now, synced_at = @now
            WHERE entity_type = @type AND natural_key = @key AND source = @source
            """, written) == 0)
        {
            // No row yet. One statement, conditional, so two people annotating the
            // same entity at once cannot leave two manual rows behind - the second
            // finds the first's row and writes nothing, which is a lost annotation
            // rather than a duplicated source.
            db.Execute("""
                INSERT INTO inventory_entity
                    (entity_type, natural_key, external_id, source, source_type,
                     discovered_at, last_seen_at, synced_at, metadata)
                SELECT @type, @key, NULL, @source, @source_type, @now, @now, @now, @metadata
                WHERE NOT EXISTS (
                    SELECT 1 FROM inventory_entity
                    WHERE entity_type = @type AND natural_key = @key AND source = @source)
                """, written);
        }

        return reader.View(db, entityType, naturalKey);
    }

    /// <summary>
    /// A tags field is a list, always - given one word, or a comma-separated line, or
    /// a list. The reads depend on it: <c>json_each</c> over something that is not a
    /// list is an error rather than an empty answer.
    /// </summary>
    private JsonNode Listed(string field, JsonNode given)
    {
        if (config.FieldType(field) != Config.Tags)
        {
            return given.DeepClone();
        }
        var tags = given is JsonArray list
            ? list.Select(item => $"{item}")
            : $"{given}".Split(',');
        return new JsonArray([.. tags.Select(tag => tag.Trim()).Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(tag => (JsonNode)JsonValue.Create(tag)!)]);
    }

    /// <summary>Nothing said: an override being let go of rather than set.</summary>
    private static bool Blank(JsonNode? value) => value switch
    {
        null => true,
        JsonArray list => list.Count == 0,
        JsonValue given => given.TryGetValue<string>(out var text) && text.Trim().Length == 0,
        _ => false,
    };
}
