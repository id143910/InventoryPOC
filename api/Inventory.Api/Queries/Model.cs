using System.Text.RegularExpressions;
using Inventory.Api.Schema;

namespace Inventory.Api.Queries;

/// <summary>A query that cannot be built as asked.</summary>
public sealed class QueryError(string message) : Exception(message);

/// <summary>
/// One stage's relation, named from the entity you are standing on.
///
/// <c>Direction</c> is that entity's role: outgoing means it is the source.
/// Nothing asks for it - a hop is picked as a finished sentence and split back
/// into its parts here.
/// </summary>
public sealed record Hop(string Direction, string RelationType, string OtherType)
{
    public string Key => $"{Direction}:{RelationType}:{OtherType}";

    public static Hop Parse(string? value)
    {
        var parts = (value ?? "").Split(':');
        if (parts.Length != 3 || parts.Any(string.IsNullOrEmpty) ||
            (parts[0] != Config.Outgoing && parts[0] != Config.Incoming))
        {
            throw new QueryError($"invalid hop '{value}'");
        }
        return new Hop(parts[0], parts[1], parts[2]);
    }

    public string Reads(Config config, string ownType) =>
        config.Reads(ownType, RelationType, OtherType, Direction);

    /// <summary>The shape this hop crosses, from the end you are standing on.</summary>
    public RelationShape Shape(Config config, string ownType) =>
        Direction == Config.Outgoing
            ? config.Relation(ownType, RelationType, OtherType)
            : config.Relation(OtherType, RelationType, ownType);
}

/// <summary>One test, on the link a stage crosses or on the entity it lands on.</summary>
public sealed record Condition(string Field, string Operator, string Value)
{
    // The vocabulary lives with the field types that decide it, in Config.
    public const string Is = Config.Is;
    public const string IsNot = Config.IsNot;
    public const string Contains = Config.Contains;

    // A field name reaches SQL as a JSON path, so it is validated here rather
    // than quoted there. Nothing downstream has to wonder.
    internal static readonly Regex SafeName = new(@"^[A-Za-z0-9_][A-Za-z0-9_.-]*$", RegexOptions.Compiled);

    public string Kind => Field.Split(':', 2)[0];

    public string Name => Field.Contains(':') ? Field.Split(':', 2)[1] : "";

    public bool AboutLink => Kind == Config.Link;

    /// <summary>
    /// True where the condition denies something rather than asserting it.
    ///
    /// It matters because an entity has a row per source. A claim holds when any
    /// source makes it; a denial holds when none of them does - otherwise one
    /// system's silence would overturn another's assertion.
    /// </summary>
    public bool Denies => Operator is Config.IsNot or Config.Excludes;

    /// <summary>The same condition as the claim it is the denial of.</summary>
    public Condition Claim => Operator switch
    {
        Config.IsNot => this with { Operator = Config.Is },
        Config.Excludes => this with { Operator = Config.Includes },
        _ => this,
    };

    /// <summary>A condition with no field or no value is a blank row, not a filter.</summary>
    public bool Active => Field.Length > 0 && Value.Length > 0;

    public string Reads() => $"{Name.Replace('_', ' ')} {Operator} {Value}";

    /// <summary>How this condition's field reads, and whether it asks for a threshold.</summary>
    public string FieldType(Config config) => config.FieldType(Name);

    public bool Ordering => Config.Ordering.Contains(Operator);

    public static Condition Build(Config config, string? field, string? @operator, string? value)
    {
        field = (field ?? "").Trim();
        @operator = string.IsNullOrWhiteSpace(@operator) ? Is : @operator.Trim();
        value = (value ?? "").Trim();
        if (field.Length > 0)
        {
            var parts = field.Split(':', 2);
            if (parts.Length != 2 || (parts[0] != Config.Link && parts[0] != Config.Entity) ||
                !SafeName.IsMatch(parts[1]))
            {
                throw new QueryError($"invalid field '{field}'");
            }
        }
        // An operator has to make sense of the field it is asked about: you cannot
        // ask whether an expiry contains something, nor whether an issuer is before
        // something.
        var name = field.Contains(':') ? field.Split(':', 2)[1] : "";
        var allowed = field.Length > 0 ? config.OperatorsFor(name) : Config.Operators[Config.Text];
        if (!allowed.Contains(@operator))
        {
            throw new QueryError(
                $"'{@operator}' cannot be asked of {(field.Length > 0 ? field : "a field")}; "
                + $"it reads as {config.FieldType(name)} and takes {string.Join(", ", allowed)}");
        }
        return new Condition(field, @operator, value);
    }
}

/// <summary>
/// The column a table is ordered by. Written <c>name</c> or <c>-name</c> on the wire,
/// the minus meaning descending, so it travels in a URL as one short parameter.
/// </summary>
public sealed record Sort(string Column, bool Descending)
{
    /// <summary>The ends of an occurrence rather than fields of it.</summary>
    public const string Entity = "entity";
    public const string From = "from";
    public const string Origin = "origin";
    public const string Source = "source";

    public static Sort? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        raw = raw.Trim();
        var descending = raw.StartsWith('-');
        var column = descending ? raw[1..] : raw;
        // A column name reaches SQL as a JSON path, like a condition's field.
        if (!Condition.SafeName.IsMatch(column))
        {
            throw new QueryError($"invalid sort '{raw}'");
        }
        return new Sort(column, descending);
    }

    public override string ToString() => (Descending ? "-" : "") + Column;
}

/// <summary>
/// One set of rows: how you got there, and what narrows it.
///
/// <c>Hop</c> is null for stage 0, the set you start from. Everything else about a
/// stage is the same wherever it sits, which is why one control renders them all.
/// </summary>
public sealed record Stage(Hop? Hop, string Match, IReadOnlyList<Condition> Conditions)
{
    public const string All = "all";
    public const string Any = "any";
    public static readonly string[] Matches = [All, Any];

    public static Stage Of(Hop? hop = null, string match = All, IReadOnlyList<Condition>? conditions = null)
    {
        if (!Matches.Contains(match))
        {
            throw new QueryError($"invalid match '{match}'");
        }
        return new Stage(hop, match, conditions ?? []);
    }

    /// <summary>The conditions that actually narrow anything.</summary>
    public IReadOnlyList<Condition> Filters => [.. Conditions.Where(item => item.Active)];

    /// <summary>A condition with no field: the row the builder offers to add another.</summary>
    private static readonly Condition Blank = new("", Condition.Is, "");

    public Stage Without(int condition)
    {
        var kept = Conditions.Select((item, index) => index == condition ? Blank : item).ToList();
        return this with { Conditions = kept };
    }

    public Stage Blanked() => this with { Conditions = [.. Conditions.Select(_ => Blank)] };

    public string Reads(Config config, string ownType)
    {
        var sentence = Hop is null ? config.Label(ownType).ToLowerInvariant() : Hop.Reads(config, ownType);
        if (Filters.Count == 0)
        {
            return sentence;
        }
        var joined = string.Join(Match == All ? " and " : " or ", Filters.Select(item => item.Reads()));
        return $"{sentence} where {joined}";
    }
}

/// <summary>
/// A query is a start and a chain of stages.
///
/// <code>
/// stage 0   the set you begin with - a type, filtered, or one named entity
/// stage n   a hop, and the rows it crosses, filtered
/// </code>
///
/// Stage 0 has no hop; that is the only thing that distinguishes it. So searching
/// for a server is the same query as following a certificate to its servers - one
/// has no hops, the other has one - and one control builds them both.
/// </summary>
public sealed record Query(
    string Type,
    string? Key,
    IReadOnlyList<Stage> Stages,
    bool Frozen = false,
    int Page = 1,
    int PageSize = 25,
    Sort? Sort = null)
{
    public const int MaxStages = 4;      // hops, on top of stage 0
    public const int MaxConditions = 3;  // per stage
    public const int MaxValues = 60;     // values a picker offers, commonest first
    public const int MaxPageSize = 100;

    /// <summary>The type standing at each stage, stage 0 first.</summary>
    public IReadOnlyList<string> Types =>
        [Type, .. Stages.Skip(1).Select(stage => stage.Hop!.OtherType)];

    /// <summary>With no hops the rows are entities; with hops they are occurrences.</summary>
    public bool ListsEntities => Stages.Count == 1;

    public Stage Last => Stages[^1];

    public int Offset => (Page - 1) * PageSize;

    public string Reads(Config config)
    {
        var parts = new List<string> { Key ?? Stages[0].Reads(config, Type) };
        for (var index = 1; index < Stages.Count; index++)
        {
            parts.Add(Stages[index].Reads(config, Types[index - 1]));
        }
        return string.Join(" → ", parts);
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Type))
        {
            throw new QueryError("a query needs a type to start from");
        }
        if (Stages.Count == 0 || Stages[0].Hop is not null)
        {
            throw new QueryError("the first stage is where you start, not a hop");
        }
        if (Stages.Count > MaxStages + 1)
        {
            throw new QueryError($"a query may have at most {MaxStages} hops");
        }
        if (Stages[0].Conditions.Any(item => item.AboutLink))
        {
            throw new QueryError("there is no link to filter before the first hop");
        }
        foreach (var stage in Stages.Skip(1))
        {
            _ = stage.Hop ?? throw new QueryError("every stage after the first needs a hop");
        }
        // Where a row began is the entity it came from only when there is one hop;
        // further back it is a set per row, which has no one value to order by.
        if (Sort?.Column == Sort.Origin && Stages.Count != 2)
        {
            throw new QueryError("rows can be ordered by where they began only across one hop");
        }
    }

    /// <summary>
    /// The query to ask by, when listing the values a condition could take.
    ///
    /// Every earlier stage still applies, so a value on offer is never a dead end.
    /// Within the stage it depends on what the stage is asking for: under
    /// <c>all</c> a condition's siblings still narrow the list, because a row has
    /// to satisfy them too; under <c>any</c> they must not, because satisfying one
    /// branch is enough and the other branches have no say over what this one can
    /// offer. Either way the condition never narrows itself out of its own options.
    /// </summary>
    public Query Probing(int stage, int condition)
    {
        var narrowed = Stages[stage].Match == Stage.Any
            ? Stages[stage].Blanked()
            : Stages[stage].Without(condition);
        return this with { Stages = [.. Stages.Take(stage), narrowed] };
    }
}
