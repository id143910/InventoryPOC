using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Inventory.Api.Schema;

/// <summary>Where the config comes from. Today a file; tomorrow a gitops revision.</summary>
public interface ISchemaSource
{
    string Read();
}

/// <summary>The config as it sits in the repository.</summary>
public sealed class FileSchemaSource(string path) : ISchemaSource
{
    public string Path { get; } = path;

    public string Read() => File.ReadAllText(Path);
}

/// <summary>One field on the merged header.</summary>
public sealed record UnifiedField(string Key, string Label);

/// <summary>
/// One kind of entity: how its sources are ranked, what to merge onto its header,
/// and what may be filtered on.
/// </summary>
public sealed record EntityType(
    string Name,
    string Label,
    string[] Authority,
    UnifiedField[] Unified,
    string[] Filters);

/// <summary>
/// The one source that is a person rather than a sync.
///
/// It outranks every declared authority, because a person correcting a field is
/// saying something the systems do not know yet. <c>Fields</c> are the keys only a
/// person ever sets - a comment is nobody's inventory - and they are merged,
/// displayed and filtered exactly like any other field.
/// </summary>
public sealed record ManualSource(string Source, string SourceType, UnifiedField[] Fields);

/// <summary>One path worth reading out of a plugin's answer.</summary>
public sealed record PluginField(string Path, string Label);

/// <summary>
/// Something else that can be done with an entity: a REST endpoint and a name.
///
/// A <c>box</c> is read when the page opens and shown beside the neighbourhood; an
/// <c>action</c> is a button, called only when pressed. Everything else about a
/// plugin is the same either way, which is why one record covers both.
/// </summary>
public sealed record Plugin(
    string EntityType,
    string Name,
    string Label,
    string Kind,
    string Method,
    string Url,
    IReadOnlyDictionary<string, string> Body,
    PluginField[] Fields,
    string? Message)
{
    public const string Box = "box";
    public const string Action = "action";

    public bool Reads => Kind == Box;
}

/// <summary>
/// One shape of relation, and what its metadata means. <c>Identity</c> is what
/// makes an occurrence distinct - a certificate installed twice on one host
/// differs only there. <c>Attributes</c> merely describe it.
/// </summary>
public sealed record RelationShape(
    string SourceType,
    string RelationType,
    string TargetType,
    string[] Identity,
    string[] Attributes,
    string[] Filters,
    string? Description)
{
    /// <summary>The columns a table of these occurrences shows, identity first.</summary>
    public string[] Columns => [.. Identity, .. Attributes];
}

/// <summary>
/// The inventory graph's configuration: what the rows cannot say about themselves.
///
/// YAML on disk because people edit it and git reviews it; plain data in here, and
/// JSON on the wire. Everything else - the shapes, the counts, the vocabulary, the
/// values behind every filter - is read back out of the data instead.
/// </summary>
public sealed class Config
{
    /// <summary>Your role in a relation: the source of it, or its target.</summary>
    public const string Outgoing = "outgoing";
    public const string Incoming = "incoming";

    /// <summary>A condition is about the link being crossed, or the entity it lands on.</summary>
    public const string Link = "link";
    public const string Entity = "entity";

    /// <summary>Every entity can be filtered on these, whatever its type declares.</summary>
    public static readonly string[] BuiltInFilters = ["natural_key", "external_id"];

    /// <summary>How a metadata key is read, and therefore what can be asked of it.</summary>
    public const string Text = "text";
    public const string Date = "date";
    public const string Number = "number";
    public const string Tags = "tags";

    public const string Is = "is";
    public const string IsNot = "is not";
    public const string Contains = "contains";
    public const string Before = "before";
    public const string After = "after";
    public const string Greater = "greater than";
    public const string Less = "less than";
    public const string Includes = "includes";
    public const string Excludes = "excludes";

    /// <summary>
    /// What a condition on a field of each type may ask. Ordering is offered only
    /// where the values order, and <c>contains</c> only where they are prose - so an
    /// expiry gets "before" and "after" while an issuer gets "contains", and neither
    /// is offered the other's nonsense.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> Operators =
        new Dictionary<string, string[]>
        {
            [Text] = [Is, IsNot, Contains],
            [Date] = [Is, IsNot, Before, After],
            [Number] = [Is, IsNot, Greater, Less],
            // A list is not equal to one of its members, so neither `is` nor
            // `contains` is offered: "tags includes audit" is the only true sentence.
            [Tags] = [Includes, Excludes],
        };

    /// <summary>Operators that ask for a threshold rather than a value, so a picker
    /// of the values present is the wrong control for them.</summary>
    public static readonly string[] Ordering = [Before, After, Greater, Less];

    private readonly Dictionary<string, EntityType> _types;
    private readonly Dictionary<(string, string, string), RelationShape> _shapes;

    private Config(Dictionary<string, EntityType> types, RelationShape[] relations,
                   Dictionary<string, string> fieldTypes, ManualSource manual,
                   Plugin[] plugins)
    {
        Plugins = plugins;
        _types = types;
        Relations = relations;
        FieldTypes = fieldTypes;
        Manual = manual;
        _shapes = relations.ToDictionary(item => (item.SourceType, item.RelationType, item.TargetType));
    }

    /// <summary>Every declared plugin, of every type.</summary>
    public IReadOnlyList<Plugin> Plugins { get; }

    /// <summary>What can be done with one kind of entity, beyond reading it.</summary>
    public IEnumerable<Plugin> PluginsOf(string entityType) =>
        Plugins.Where(item => item.EntityType == entityType);

    /// <summary>One plugin by name, or nothing where the config declares none.</summary>
    public Plugin? Plugin(string entityType, string name) =>
        Plugins.FirstOrDefault(item => item.EntityType == entityType && item.Name == name);

    /// <summary>The source a person writes as, and the fields only they set.</summary>
    public ManualSource Manual { get; }

    /// <summary>
    /// Who to believe about this type, most trusted first.
    ///
    /// A person first, then the config's declared authority. This is the whole of
    /// how an override takes precedence: it is not a special case in the merge, it is
    /// a source that happens to be ranked above the others.
    /// </summary>
    public string[] Ranking(string entityType) => [Manual.Source, .. Type(entityType).Authority];

    /// <summary>How each named metadata key reads. Anything absent is text.</summary>
    public IReadOnlyDictionary<string, string> FieldTypes { get; }

    /// <summary>How to read one metadata key. Anything the config does not name is text.</summary>
    public string FieldType(string name) => FieldTypes.TryGetValue(name, out var found) ? found : Text;

    /// <summary>What a condition on this field may ask, given how the field reads.</summary>
    public string[] OperatorsFor(string name) => Operators[FieldType(name)];

    /// <summary>The label the config gives a merged field, by key.</summary>
    public IReadOnlyDictionary<string, string> Labels =>
        _types.Values.SelectMany(type => type.Unified)
            .GroupBy(field => field.Key)
            .ToDictionary(group => group.Key, group => group.First().Label);

    public IReadOnlyCollection<EntityType> EntityTypes => _types.Values;

    public IReadOnlyList<RelationShape> Relations { get; }

    /// <summary>An undeclared type still works; it just gets no merged header.</summary>
    public EntityType Type(string name) =>
        _types.TryGetValue(name, out var found)
            ? found
            // No merged header of its own, but a person can still say something
            // about it, so the manual fields are there as they are everywhere.
            : new EntityType(name, Titled(name), [], Manual.Fields,
                             [.. BuiltInFilters, .. Manual.Fields.Select(field => field.Key)]);

    public string Label(string name) => Type(name).Label;

    /// <summary>An undeclared shape still works; its endpoints alone make it unique.</summary>
    public RelationShape Relation(string sourceType, string relationType, string targetType) =>
        _shapes.TryGetValue((sourceType, relationType, targetType), out var found)
            ? found
            : new RelationShape(sourceType, relationType, targetType, [], [], [], null);

    /// <summary>
    /// Name a relation from where you are standing.
    ///
    /// The full sentence is always <c>source_type relation_type target_type</c> -
    /// "server managed by team". Standing on one of its ends, that end is already
    /// the heading of the page, so it drops out and what is left names the table:
    /// <code>
    /// on the server       managed by team           (listing teams)
    /// on the team         server managed by         (listing servers)
    /// on the server       certificate installed on  (listing certificates)
    /// on the certificate  installed on server       (listing servers)
    /// </code>
    /// So a new relation label needs no configuration; it only has to be named as
    /// a verb reading source to target.
    /// </summary>
    public string Reads(string ownType, string relationType, string otherType, string direction)
    {
        var phrase = relationType.Replace('_', ' ');
        var other = Label(otherType).ToLowerInvariant();
        return direction == Outgoing ? $"{phrase} {other}" : $"{other} {phrase}";
    }

    public static Config Load(ISchemaSource source)
    {
        var yaml = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build()
            .Deserialize<ConfigFile>(source.Read()) ?? new ConfigFile();

        var manual = new ManualSource(
            yaml.Manual?.Source ?? "Maestro",
            yaml.Manual?.SourceType ?? "manual",
            [.. (yaml.Manual?.Fields ?? []).Select(field => new UnifiedField(field.Key, field.Label))]);

        var types = yaml.EntityTypes.ToDictionary(
            entry => entry.Key,
            entry => new EntityType(
                entry.Key,
                entry.Value.Label ?? Titled(entry.Key),
                [.. entry.Value.Authority],
                // What a person may set is part of the header, not an annex to it.
                [.. entry.Value.Unified.Select(field => new UnifiedField(field.Key, field.Label)),
                 .. manual.Fields],
                // Declared filters, or the merged fields if none are named. The
                // built-ins come first either way.
                [.. BuiltInFilters,
                 .. entry.Value.Filters.Count > 0
                    ? entry.Value.Filters
                    : entry.Value.Unified.Select(field => field.Key),
                 .. manual.Fields.Select(field => field.Key)]));

        var relations = yaml.Relations
            .Select(item => new RelationShape(
                item.SourceType, item.RelationType, item.TargetType,
                [.. item.Identity], [.. item.Attributes], [.. item.Filters], item.Description))
            .ToArray();

        var plugins = yaml.Plugins.SelectMany(entry => entry.Value.Select(item => new Plugin(
            entry.Key,
            item.Name,
            item.Label ?? Titled(item.Name),
            item.Kind,
            // A box is read, an action is done, so each has an obvious method and the
            // config only says so where it differs.
            item.Method ?? (item.Kind == Schema.Plugin.Box ? "GET" : "POST"),
            item.Url,
            item.Body,
            [.. item.Fields.Select(field => new PluginField(field.Path, field.Label))],
            item.Message))).ToArray();

        return new Config(types, relations,
            yaml.FieldTypes.ToDictionary(entry => entry.Key, entry => entry.Value), manual, plugins);
    }

    private static string Titled(string name) =>
        string.Join(' ', name.Replace('_', ' ').Split(' ')
            .Select(word => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..]));

    // What the YAML looks like on the way in. Mutable and file-scoped on purpose:
    // it exists only to be projected into the immutable shapes above.
    private sealed class ConfigFile
    {
        public Dictionary<string, string> FieldTypes { get; set; } = [];
        public ManualFile? Manual { get; set; }
        public Dictionary<string, EntityTypeFile> EntityTypes { get; set; } = [];
        public Dictionary<string, List<PluginFile>> Plugins { get; set; } = [];
        public List<RelationFile> Relations { get; set; } = [];
    }

    private sealed class ManualFile
    {
        public string? Source { get; set; }
        public string? SourceType { get; set; }
        public List<UnifiedFieldFile> Fields { get; set; } = [];
    }

    private sealed class EntityTypeFile
    {
        public string? Label { get; set; }
        public List<string> Authority { get; set; } = [];
        public List<UnifiedFieldFile> Unified { get; set; } = [];
        public List<string> Filters { get; set; } = [];
    }

    private sealed class UnifiedFieldFile
    {
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
    }

    private sealed class PluginFile
    {
        public string Name { get; set; } = "";
        public string? Label { get; set; }
        public string Kind { get; set; } = Schema.Plugin.Box;
        public string? Method { get; set; }
        public string Url { get; set; } = "";
        public Dictionary<string, string> Body { get; set; } = [];
        public List<PluginFieldFile> Fields { get; set; } = [];
        public string? Message { get; set; }
    }

    private sealed class PluginFieldFile
    {
        public string Path { get; set; } = "";
        public string Label { get; set; } = "";
    }

    private sealed class RelationFile
    {
        public string SourceType { get; set; } = "";
        public string RelationType { get; set; } = "";
        public string TargetType { get; set; } = "";
        public List<string> Identity { get; set; } = [];
        public List<string> Attributes { get; set; } = [];
        public List<string> Filters { get; set; } = [];
        public string? Description { get; set; }
    }
}
