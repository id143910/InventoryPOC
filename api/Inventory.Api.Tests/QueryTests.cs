using System.Data;
using System.Text.Json.Nodes;
using Inventory.Api;
using Inventory.Api.Entities;
using Inventory.Api.Plugins;
using Inventory.Api.Queries;
using Inventory.Api.Schema;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;

namespace Inventory.Api.Tests;

/// <summary>
/// The seeded database and the config beside it, shared by every test.
///
/// The real `inventory.db`, not a fixture built here: it is committed with the
/// repository precisely so that these assert against the data the application shows.
/// Read from a copy, though, because some tests write: they clear up after
/// themselves, but the committed file would still come back changed.
/// </summary>
public sealed class Graph : IDisposable
{
    private static readonly string Root = FindRoot();

    private readonly string copy = Path.Combine(Path.GetTempPath(), $"inventory-{Guid.NewGuid():N}.db");

    public Graph()
    {
        File.Copy(Path.Combine(Root, "inventory.db"), copy);
        Config = Config.Load(new FileSchemaSource(Path.Combine(Root, "schema.yaml")));
        Connection = new Db(copy).Open();
        Options = new Options(Config, new MemoryCache(new MemoryCacheOptions()));
        Ask.Config = Config;
        Entities = new EntityReader(Config);
        Runner = new Runner(Config);
    }

    public Config Config { get; }
    public IDbConnection Connection { get; }
    public Options Options { get; }
    public EntityReader Entities { get; }
    public Runner Runner { get; }

    public void Dispose()
    {
        Connection.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { copy, $"{copy}-wal", $"{copy}-shm" })
        {
            File.Delete(file);
        }
    }

    private static string FindRoot()
    {
        var here = new DirectoryInfo(AppContext.BaseDirectory);
        while (here is not null && !File.Exists(Path.Combine(here.FullName, "schema.yaml")))
        {
            here = here.Parent;
        }
        return here?.FullName ?? throw new InvalidOperationException("no schema.yaml above the test binary");
    }
}

public static class Ask
{
    /// <summary>The config the helpers build conditions against.</summary>
    public static Config Config { get; set; } = default!;

    public static Condition Where(string field, string value, string @operator = Condition.Is) =>
        Condition.Build(Config, field, @operator, value);

    public static Stage Start(string match = Stage.All, params Condition[] conditions) =>
        Stage.Of(null, match, conditions);

    public static Stage Hop(string key, string match = Stage.All, params Condition[] conditions) =>
        Stage.Of(Queries.Hop.Parse(key), match, conditions);

    public static Query Query(string type, string? key = null, params Stage[] stages) =>
        new(type, key, stages.Length == 0 ? [Start()] : stages, PageSize: 200);

    public const string InstalledOn = "outgoing:installed_on:server";
    public const string ManagesCerts = "incoming:managed_by:certificate";
    public const string Cert = "service-0003.example.com";
    public const string Team = "Team 02";
}

public sealed class ReadingARelation(Graph graph) : IClassFixture<Graph>
{
    [Theory]
    // The sentence is always "source relation target"; the end you are standing on
    // is the page heading already, so the table takes what is left.
    [InlineData("server", "managed_by", "team", Config.Outgoing, "managed by team")]
    [InlineData("team", "managed_by", "server", Config.Incoming, "server managed by")]
    [InlineData("server", "installed_on", "package", Config.Incoming, "package installed on")]
    [InlineData("package", "installed_on", "server", Config.Outgoing, "installed on server")]
    // A label nobody configured still reads both ways.
    [InlineData("team", "backed_up_by", "server", Config.Incoming, "server backed up by")]
    public void TheTypeYouStandOnDropsOut(string own, string relation, string other, string direction, string expected) =>
        Assert.Equal(expected, graph.Config.Reads(own, relation, other, direction));

    [Fact]
    public void BothNamesRebuildTheWholeSentence()
    {
        var onSource = graph.Config.Reads("server", "managed_by", "team", Config.Outgoing);
        var onTarget = graph.Config.Reads("team", "managed_by", "server", Config.Incoming);
        Assert.Equal("server managed by team", $"server {onSource}");
        Assert.Equal("server managed by team", $"{onTarget} team");
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("sideways:installed_on:server")]
    [InlineData("outgoing:installed_on")]
    public void AMalformedHopIsRejected(string value) => Assert.Throws<QueryError>(() => Hop.Parse(value));

    [Fact]
    public void AHopSurvivesTheRoundTripThroughItsKey()
    {
        var picked = new Hop(Config.Outgoing, "installed_on", "server");
        Assert.Equal(picked, Hop.Parse(picked.Key));
    }

    [Fact]
    public void AQueryReadsAsOneSentence()
    {
        var asked = Ask.Query("team", Ask.Team, Ask.Start(),
            Ask.Hop(Ask.ManagesCerts, Stage.All, Ask.Where("entity:issuer", "DigiCert Global G2")),
            Ask.Hop(Ask.InstalledOn, Stage.All, Ask.Where("link:location", "My")));
        Assert.Equal(
            "Team 02 → certificate managed by where issuer is DigiCert Global G2"
            + " → installed on server where location is My",
            asked.Reads(graph.Config));
    }
}

public sealed class NoHops(Graph graph) : IClassFixture<Graph>
{
    private int Total(Query query) => graph.Runner.Keys(graph.Connection, query).Total;

    [Fact]
    public void TheRowsAreEntities()
    {
        var page = graph.Runner.Keys(graph.Connection, Ask.Query("server"));
        Assert.True(page.Total > 0);
        Assert.All(page.Items, item => Assert.Equal("server", item.Type));
    }

    [Fact]
    public void AConditionNarrowsTheList()
    {
        var production = Total(Ask.Query("server", null,
            Ask.Start(Stage.All, Ask.Where("entity:environment", "production"))));
        Assert.InRange(production, 1, Total(Ask.Query("server")) - 1);
    }

    [Fact]
    public void ContainsMatchesAFragment()
    {
        var page = graph.Runner.Keys(graph.Connection, Ask.Query("server", null,
            Ask.Start(Stage.All, Ask.Where("entity:natural_key", "000", Condition.Contains))));
        Assert.True(page.Total > 0);
        Assert.All(page.Items, item => Assert.Contains("000", item.Key));
    }

    [Fact]
    public void IsNotExcludes()
    {
        var all = Total(Ask.Query("server"));
        var production = Total(Ask.Query("server", null, Ask.Start(Stage.All, Ask.Where("entity:environment", "production"))));
        var rest = Total(Ask.Query("server", null,
            Ask.Start(Stage.All, Ask.Where("entity:environment", "production", Condition.IsNot))));
        Assert.Equal(all, production + rest);
    }

    [Fact]
    public void SeveralConditionsCanBeRequiredTogether()
    {
        var one = Total(Ask.Query("server", null, Ask.Start(Stage.All, Ask.Where("entity:environment", "production"))));
        var both = Total(Ask.Query("server", null, Ask.Start(Stage.All,
            Ask.Where("entity:environment", "production"), Ask.Where("entity:region", "eu-west-1"))));
        Assert.InRange(both, 1, one - 1);
    }

    [Fact]
    public void SeveralConditionsCanBeSatisfiedByAny()
    {
        var staging = Total(Ask.Query("server", null, Ask.Start(Stage.All, Ask.Where("entity:environment", "staging"))));
        var development = Total(Ask.Query("server", null, Ask.Start(Stage.All, Ask.Where("entity:environment", "development"))));
        var either = Total(Ask.Query("server", null, Ask.Start(Stage.Any,
            Ask.Where("entity:environment", "staging"), Ask.Where("entity:environment", "development"))));
        Assert.Equal(staging + development, either);
    }

    [Fact]
    public void AConditionMatchesIfAnySourceSaysSo()
    {
        // Two systems describe every server and disagree about the OS version on
        // every ninth one; either account is enough to match.
        var page = graph.Runner.Keys(graph.Connection, Ask.Query("server", null,
            Ask.Start(Stage.All, Ask.Where("entity:os_version", "Windows Server 2019"))));
        Assert.True(page.Total > 0);
        foreach (var key in page.Items.Take(20))
        {
            var view = graph.Entities.View(graph.Connection, "server", key.Key);
            Assert.Contains("Windows Server 2019",
                view.Facets.Select(facet => facet.Metadata.GetValueOrDefault("os_version")?.ToString()));
        }
    }
}

public sealed class WithHops(Graph graph) : IClassFixture<Graph>
{
    private int Total(Query query) => graph.Runner.Occurrences(graph.Connection, query).Total;

    [Fact]
    public void AHopKeepsOnlyTheTypeItNames()
    {
        // "the packages installed on this server" is not `installed_on` - that label
        // also reaches certificates.
        var page = graph.Runner.Occurrences(graph.Connection,
            Ask.Query("server", "prod-server-0007", Ask.Start(), Ask.Hop("incoming:installed_on:package")));
        Assert.True(page.Total > 0);
        Assert.All(page.Items, row => Assert.Equal("package", row.ToType));
    }

    [Fact]
    public void TheRowsAreOccurrencesNotPairs()
    {
        var page = graph.Runner.Occurrences(graph.Connection,
            Ask.Query("certificate", Ask.Cert, Ask.Start(), Ask.Hop(Ask.InstalledOn)));
        var byHost = page.Items.GroupBy(row => row.ToKey).ToList();
        Assert.Contains(byHost, group => group.Count() > 1);
    }

    [Fact]
    public void EveryStageCarriesItsOwnConditions()
    {
        var plain = Total(Ask.Query("team", Ask.Team, Ask.Start(), Ask.Hop(Ask.ManagesCerts), Ask.Hop(Ask.InstalledOn)));
        var first = Total(Ask.Query("team", Ask.Team, Ask.Start(),
            Ask.Hop(Ask.ManagesCerts, Stage.All, Ask.Where("entity:issuer", "DigiCert Global G2")),
            Ask.Hop(Ask.InstalledOn)));
        var both = Total(Ask.Query("team", Ask.Team, Ask.Start(),
            Ask.Hop(Ask.ManagesCerts, Stage.All, Ask.Where("entity:issuer", "DigiCert Global G2")),
            Ask.Hop(Ask.InstalledOn, Stage.All, Ask.Where("link:location", "My"))));
        Assert.InRange(both, 1, first - 1);
        Assert.InRange(first, 1, plain - 1);
    }

    [Fact]
    public void AConditionCanBeAboutTheLinkOrTheEntity()
    {
        var onLink = graph.Runner.Occurrences(graph.Connection, Ask.Query("team", Ask.Team, Ask.Start(),
            Ask.Hop(Ask.ManagesCerts), Ask.Hop(Ask.InstalledOn, Stage.All, Ask.Where("link:location_type", "keystore"))));
        Assert.True(onLink.Total > 0);
        Assert.All(onLink.Items, row => Assert.Equal("keystore", row.Metadata["location_type"]?.ToString()));

        var onEntity = Total(Ask.Query("team", Ask.Team, Ask.Start(), Ask.Hop(Ask.ManagesCerts),
            Ask.Hop(Ask.InstalledOn, Stage.All, Ask.Where("entity:environment", "production"))));
        Assert.True(onEntity > 0);
    }

    [Fact]
    public void IntermediateStagesDeduplicate()
    {
        // A certificate installed twice on one host must not drag that host through
        // the next hop twice.
        var reached = graph.Runner.Occurrences(graph.Connection,
            Ask.Query("team", Ask.Team, Ask.Start(), Ask.Hop(Ask.ManagesCerts)));
        var onward = graph.Runner.Occurrences(graph.Connection,
            Ask.Query("team", Ask.Team, Ask.Start(), Ask.Hop(Ask.ManagesCerts), Ask.Hop(Ask.InstalledOn)));
        Assert.Subset(reached.Items.Select(row => row.ToKey).ToHashSet(),
                      onward.Items.Select(row => row.FromKey).ToHashSet());
    }

    [Fact]
    public void AQueryCanStartFromAWholeTypeAndTraverse()
    {
        var all = Total(Ask.Query("server", null, Ask.Start(), Ask.Hop("incoming:installed_on:certificate")));
        var production = Total(Ask.Query("server", null,
            Ask.Start(Stage.All, Ask.Where("entity:environment", "production")),
            Ask.Hop("incoming:installed_on:certificate")));
        Assert.InRange(production, 1, all - 1);
    }

    [Fact]
    public void AQueryIsBounded()
    {
        var stages = new List<Stage> { Ask.Start() };
        stages.AddRange(Enumerable.Repeat(Ask.Hop(Ask.InstalledOn), Query.MaxStages + 1));
        Assert.Throws<QueryError>(() =>
            graph.Runner.Occurrences(graph.Connection, Ask.Query("certificate", Ask.Cert, [.. stages])));
    }

    [Fact]
    public void ALinkConditionIsRejectedBeforeTheFirstHop() =>
        Assert.Throws<QueryError>(() =>
            Ask.Query("server", null, Ask.Start(Stage.All, Ask.Where("link:location", "My"))).Validate());

    [Theory]
    [InlineData("link:location'); drop table --", Condition.Is, "x")]
    [InlineData("nowhere:location", Condition.Is, "My")]
    [InlineData("link:location", "sort of", "My")]
    // An operator has to make sense of its field: prose does not order, and a date
    // does not contain.
    [InlineData("link:location", Config.Before, "My")]
    [InlineData("link:not_after", Config.Contains, "2027")]
    [InlineData("entity:renewals", Config.Contains, "1")]
    public void ABadConditionIsRejected(string field, string @operator, string value) =>
        Assert.Throws<QueryError>(() => Condition.Build(graph.Config, field, @operator, value));
}

public sealed class TypedFields(Graph graph) : IClassFixture<Graph>
{
    private int Total(Query query) => query.ListsEntities
        ? graph.Runner.Keys(graph.Connection, query).Total
        : graph.Runner.Occurrences(graph.Connection, query).Total;

    [Theory]
    [InlineData("current_not_after", Config.Date)]
    [InlineData("not_after", Config.Date)]
    [InlineData("port", Config.Number)]
    [InlineData("renewals", Config.Number)]
    [InlineData("issuer", Config.Text)]
    [InlineData("natural_key", Config.Text)]
    public void AKeyReadsAsTheConfigSays(string key, string expected) =>
        Assert.Equal(expected, graph.Config.FieldType(key));

    [Fact]
    public void TheOperatorsOfferedFollowTheType()
    {
        Assert.Equal([Config.Is, Config.IsNot, Config.Contains], graph.Config.OperatorsFor("issuer"));
        Assert.Equal([Config.Is, Config.IsNot, Config.Before, Config.After], graph.Config.OperatorsFor("not_after"));
        Assert.Equal([Config.Is, Config.IsNot, Config.Greater, Config.Less], graph.Config.OperatorsFor("port"));
    }

    [Fact]
    public void ADateOrdersOnTheLink()
    {
        // Every install has its own expiry, so the two halves must add back up.
        var all = Total(Ask.Query("certificate", null, Ask.Start(), Ask.Hop(Ask.InstalledOn)));
        var before = Total(Ask.Query("certificate", null, Ask.Start(),
            Ask.Hop(Ask.InstalledOn, Stage.All, Ask.Where("link:not_after", "2027-06-01", Config.Before))));
        var after = Total(Ask.Query("certificate", null, Ask.Start(),
            Ask.Hop(Ask.InstalledOn, Stage.All, Ask.Where("link:not_after", "2027-06-01", Config.After))));
        Assert.True(before > 0 && after > 0);
        Assert.Equal(all, before + after);
    }

    [Fact]
    public void ADateOrdersOnTheEntity()
    {
        var early = Total(Ask.Query("certificate", null,
            Ask.Start(Stage.All, Ask.Where("entity:current_not_after", "2027-01-01", Config.Before))));
        var all = Total(Ask.Query("certificate"));
        Assert.InRange(early, 1, all - 1);
    }

    [Fact]
    public void ANumberComparesAsANumberAndNotAsText()
    {
        // Ports run 8081 and 8082, so a text comparison would still order them the
        // same; renewals run 0..2, where "2" > "10" would show the difference.
        var over = Total(Ask.Query("certificate", null,
            Ask.Start(Stage.All, Ask.Where("entity:renewals", "1", Config.Greater))));
        var under = Total(Ask.Query("certificate", null,
            Ask.Start(Stage.All, Ask.Where("entity:renewals", "1", Config.Less))));
        var exact = Total(Ask.Query("certificate", null,
            Ask.Start(Stage.All, Ask.Where("entity:renewals", "1"))));
        Assert.True(over > 0 && under > 0);
        Assert.Equal(Total(Ask.Query("certificate")), over + under + exact);
    }

    [Fact]
    public void AValueThatIsNotANumberIsRejected() =>
        Assert.Throws<QueryError>(() => Total(Ask.Query("certificate", null,
            Ask.Start(Stage.All, Ask.Where("entity:renewals", "lots", Config.Greater)))));

    [Fact]
    public void AFieldCarriesHowItReads()
    {
        var offered = graph.Options.Fields(Queries.Hop.Parse(Ask.InstalledOn), "certificate");
        Assert.Equal(Config.Date, offered.Single(field => field.Value == "link:not_after").Type);
        Assert.Equal(Config.Text, offered.Single(field => field.Value == "link:location").Type);
    }

    [Fact]
    public void AnEntityTypeNamesTheColumnsItsTableShows()
    {
        // The same idea as a shape declaring the columns of its occurrences.
        // The declared fields, then what a person may set - a manual field is a
        // field, not an annex.
        Assert.Equal(["environment", "region", "os_version", "lifecycle", "tags", "comment"],
            graph.Config.Type("server").Unified.Select(field => field.Key));
        Assert.Equal("Latest expiry", graph.Config.Labels["current_not_after"]);
    }
}

public sealed class TheValuesOnOffer(Graph graph) : IClassFixture<Graph>
{
    [Fact]
    public void TheyComeFromTheRows()
    {
        var asked = Ask.Query("certificate", Ask.Cert, Ask.Start(),
            Ask.Hop(Ask.InstalledOn, Stage.All, Ask.Where("link:location", "")));
        var rows = graph.Runner.Occurrences(graph.Connection,
            Ask.Query("certificate", Ask.Cert, Ask.Start(), Ask.Hop(Ask.InstalledOn)));
        var offered = graph.Runner.Values(graph.Connection, asked, 1, 0);
        Assert.Equal(
            rows.Items.Select(row => row.Metadata["location"]?.ToString()).ToHashSet(),
            offered.Select(item => item.Value).ToHashSet());
        Assert.Equal(rows.Total, offered.Sum(item => item.Count));
    }

    [Fact]
    public void TheyIgnoreTheConditionsOwnValue()
    {
        var blank = Ask.Query("certificate", Ask.Cert, Ask.Start(),
            Ask.Hop(Ask.InstalledOn, Stage.All, Ask.Where("link:location", "")));
        var chosen = Ask.Query("certificate", Ask.Cert, Ask.Start(),
            Ask.Hop(Ask.InstalledOn, Stage.All, Ask.Where("link:location", "My")));
        Assert.Equal(graph.Runner.Values(graph.Connection, blank, 1, 0),
                     graph.Runner.Values(graph.Connection, chosen, 1, 0));
    }

    [Fact]
    public void ASiblingNarrowsThemUnderAll()
    {
        var asked = Ask.Query("server", null, Ask.Start(Stage.All,
            Ask.Where("entity:environment", ""), Ask.Where("entity:region", "eu-west-1")));
        var scoped = graph.Runner.Values(graph.Connection, asked, 0, 0).Sum(item => item.Count);
        Assert.InRange(scoped, 1, graph.Runner.Keys(graph.Connection, Ask.Query("server")).Total - 1);
    }

    [Fact]
    public void ASiblingDoesNotNarrowThemUnderAny()
    {
        // Satisfying one branch is enough, so the other branch has no say over what
        // this one can offer - otherwise picking "staging" would hide "development".
        var asked = Ask.Query("server", null, Ask.Start(Stage.Any,
            Ask.Where("entity:environment", ""), Ask.Where("entity:environment", "development")));
        var offered = graph.Runner.Values(graph.Connection, asked, 0, 0);
        Assert.Superset(new HashSet<string> { "production", "staging", "development" },
                        offered.Select(item => item.Value).ToHashSet());
        Assert.Equal(graph.Runner.Keys(graph.Connection, Ask.Query("server")).Total, offered.Sum(item => item.Count));
    }

    [Fact]
    public void SeveralFieldsOnOneStageDoNotCollide()
    {
        var asked = Ask.Query("server", "prod-server-0007", Ask.Start(),
            Ask.Hop("incoming:installed_on:package", Stage.All,
                Ask.Where("link:version", ""), Ask.Where("link:install_scope", "")));
        Assert.NotEmpty(graph.Runner.Values(graph.Connection, asked, 1, 0));
        Assert.NotEmpty(graph.Runner.Values(graph.Connection, asked, 1, 1));
    }
}

public sealed class AnEntitysPage(Graph graph) : IClassFixture<Graph>
{
    [Fact]
    public void EachSourceIsItsOwnRowMergedIntoOneEntity()
    {
        var view = graph.Entities.View(graph.Connection, "server", "prod-server-0007");
        Assert.Equal(["snow", "sccm"], view.Sources);
        Assert.Equal(2, view.Facets.Count);
    }

    [Fact]
    public void ADisagreementIsFlaggedNotAveraged()
    {
        // Every fifth certificate carries a pre-renewal expiry in the CMDB.
        var view = graph.Entities.View(graph.Connection, "certificate", "service-0005.example.com");
        var expiry = view.Values.Single(value => value.Key == "current_not_after");
        Assert.True(expiry.Mismatch);
        Assert.Equal("appviewx", expiry.Source);
        Assert.NotEqual(expiry.Reported["snow"], expiry.Reported["appviewx"]);
    }

    [Fact]
    public void AKeyWithNeitherRowsNorRelationsIsMissing() =>
        Assert.Throws<NotFound>(() => graph.Entities.View(graph.Connection, "server", "no-such-host"));

    [Fact]
    public void AnEndpointNeedNotBeInventoried()
    {
        var view = graph.Entities.View(graph.Connection, "server", "edge-appliance-0060");
        Assert.False(view.Known);
        Assert.Empty(view.Facets);
        Assert.NotEmpty(graph.Entities.Neighbourhood(graph.Connection, "server", "edge-appliance-0060", false));
    }

    [Fact]
    public void ABoxCountsThingsNotRows()
    {
        var box = graph.Entities.Neighbourhood(graph.Connection, "certificate", Ask.Cert, false)
            .Single(item => item.Hop.RelationType == "installed_on");
        Assert.True(box.Occurrences > box.Neighbours, "the fixture is installed twice on one host");
        Assert.True(box.Listed);
        Assert.Equal(box.Neighbours, box.Named.Count);
    }

    [Fact]
    public void ABoxCountsItsNeighboursWhenThereAreMany()
    {
        var box = graph.Entities.Neighbourhood(graph.Connection, "package", "nginx", false)
            .Single(item => item.Hop.RelationType == "installed_on");
        Assert.True(box.Neighbours > EntityReader.Listed);
        Assert.Equal(EntityReader.Named, box.Named.Count);
        Assert.False(box.Listed);
        Assert.Equal(box.Neighbours - EntityReader.Named, box.Others);
    }

    [Theory]
    // Not every host carries a certificate, so the types asked for here are ones
    // these particular entities are known to reach.
    [InlineData("server", "prod-server-0022", "certificate installed on", "package installed on")]
    [InlineData("certificate", Ask.Cert, "installed on server", "managed by team")]
    [InlineData("team", Ask.Team, "server managed by", "certificate managed by")]
    public void TheBoxesAreNamedByTheConvention(string type, string key, params string[] expected)
    {
        var named = graph.Entities.Neighbourhood(graph.Connection, type, key, false)
            .Select(box => box.Hop.Reads(graph.Config, type)).ToList();
        Assert.Superset(expected.ToHashSet(), named.ToHashSet());
        // The type you are standing on is the heading already, so it drops out.
        Assert.DoesNotContain(named, name => name.Contains(graph.Config.Label(type).ToLowerInvariant()));
    }

    [Fact]
    public void AHopCountsActiveAndFrozenSeparately()
    {
        var found = graph.Options.HopsOf(graph.Connection, "certificate", Ask.Cert);
        Assert.NotEmpty(found);
        Assert.All(found, item => Assert.Equal(item.Total, item.Active + item.Frozen));
    }
}

public sealed class TheConfig(Graph graph) : IClassFixture<Graph>
{
    [Fact]
    public void ItReadsTheSameYamlThePythonSideReads()
    {
        Assert.Equal(7, graph.Config.EntityTypes.Count);
        Assert.Equal(9, graph.Config.Relations.Count);
    }

    [Fact]
    public void IdentityIsDeclaredNotGuessed()
    {
        var shape = graph.Config.Relation("certificate", "installed_on", "server");
        Assert.Equal(["thumbprint", "location"], shape.Identity);
        Assert.Contains("location_type", shape.Attributes);
        Assert.Equal(["thumbprint", "location", "location_type", "not_after", "bound_to"], shape.Columns);
        // A pair is the whole fact where no identity is declared.
        Assert.Empty(graph.Config.Relation("server", "managed_by", "team").Identity);
    }

    [Fact]
    public void EveryEntityCanBeFilteredOnItsKeyAndItsForeignId()
    {
        foreach (var type in graph.Config.EntityTypes)
        {
            Assert.Contains("natural_key", type.Filters);
            Assert.Contains("external_id", type.Filters);
        }
    }

    [Fact]
    public void AnUndeclaredTypeStillWorks()
    {
        var unknown = graph.Config.Type("widget");
        Assert.Equal("Widget", unknown.Label);
        // No merged header of its own, but a person can still annotate it, so the
        // manual fields are there as they are on every type.
        Assert.Equal([.. Config.BuiltInFilters, "tags", "comment"], unknown.Filters);
        Assert.Equal(["tags", "comment"], unknown.Unified.Select(field => field.Key));
    }

    [Fact]
    public void EveryDeclaredShapeIsInTheData()
    {
        var present = graph.Options.Shapes(graph.Connection)
            .Select(shape => (shape.SourceType, shape.RelationType, shape.TargetType)).ToHashSet();
        foreach (var shape in graph.Config.Relations)
        {
            Assert.Contains((shape.SourceType, shape.RelationType, shape.TargetType), present);
        }
    }

    [Fact]
    public void OneRelationLabelIsSharedBySeveralSourceTypes()
    {
        // Without this the type constraint on a hop would be decoration.
        var shared = graph.Options.Shapes(graph.Connection)
            .GroupBy(shape => shape.RelationType)
            .ToDictionary(group => group.Key, group => group.Select(item => item.SourceType).Distinct().Count());
        Assert.True(shared["installed_on"] > 1);
        Assert.True(shared["managed_by"] > 1);
    }
}

/// <summary>
/// Dates written relative to today.
///
/// A saved query is the reason this exists: the date has to be worked out at each
/// run, or "expiring in the next 60 days" stops being true the day after it is
/// saved. Today is passed in so these do not depend on when they are run.
/// </summary>
public sealed class RelativeDates
{
    private static readonly DateTime Today = new(2026, 9, 30);

    [Theory]
    [InlineData("now", "2026-09-30")]
    [InlineData("now-60d", "2026-08-01")]
    [InlineData("now+60d", "2026-11-29")]
    [InlineData("now-1w", "2026-09-23")]
    [InlineData("now+3M", "2026-12-30")]
    [InlineData("now-1y", "2025-09-30")]
    [InlineData("NOW + 7 D", "2026-10-07")]
    public void ResolvesToADate(string written, string expected)
    {
        Assert.Equal(expected, Moment.Resolve(written, Today));
    }

    [Theory]
    [InlineData("2027-06-01")]
    [InlineData("")]
    [InlineData("2027")]
    public void LeavesAnythingElseAlone(string written)
    {
        Assert.Equal(written, Moment.Resolve(written, Today));
    }

    [Theory]
    [InlineData("now-")]
    [InlineData("now-7x")]
    [InlineData("now-7d-1d")]
    // Anything starting in "now" was meant to be relative, so a typo is refused
    // rather than compared against as literal text.
    [InlineData("nowhere")]
    public void RefusesWhatItCannotWorkOut(string written)
    {
        Assert.Throws<QueryError>(() => Moment.Resolve(written, Today));
    }

    [Fact]
    public void SaysWhyLowercaseMIsNoUseToADate()
    {
        var error = Assert.Throws<QueryError>(() => Moment.Resolve("now-5m", Today));
        Assert.Contains("minutes", error.Message);
    }
}

/// <summary>A relative date is resolved where a typed one would be, and answers alike.</summary>
public sealed class RelativeDateQueries(Graph graph) : IClassFixture<Graph>
{
    [Fact]
    public void ReadTheSameAsTheDateTheyResolveTo()
    {
        var far = DateTime.UtcNow.Date.AddDays(400).ToString("yyyy-MM-dd");
        var relative = graph.Runner.Keys(graph.Connection, Expiring("now+400d"));
        var literal = graph.Runner.Keys(graph.Connection, Expiring(far));
        Assert.Equal(literal.Total, relative.Total);
        Assert.True(relative.Total > 0, "some certificate expires within 400 days");
    }

    private static Query Expiring(string value) =>
        Ask.Query("certificate", null, Ask.Start(Stage.All,
            Ask.Where("entity:current_not_after", value, Config.Before)));
}

/// <summary>
/// What a person says, as a source of its own.
///
/// These write to the seeded database and clear up after themselves, which is the
/// honest way to test a write: the row has to really land, be outranked by nothing,
/// and really go away again.
/// </summary>
public sealed class WhatAPersonSays(Graph graph) : IClassFixture<Graph>
{
    private const string Server = "prod-server-0022";

    private ManualWriter Writer => new(graph.Config, graph.Entities);

    private EntityView Set(params (string Field, JsonNode? Value)[] fields) =>
        Writer.Set(graph.Connection, "server", Server,
            fields.ToDictionary(item => item.Field, item => item.Value));

    private void Clear() => Set(("environment", null), ("comment", null));

    [Fact]
    public void OutranksEverySyncWithoutSilencingThem()
    {
        var before = graph.Entities.View(graph.Connection, "server", Server);
        var said = before.Values.Single(value => value.Key == "environment");
        try
        {
            var after = Set(("environment", JsonValue.Create("laboratory")));
            var now = after.Values.Single(value => value.Key == "environment");
            Assert.Equal("laboratory", $"{now.Trusted}");
            Assert.Equal(graph.Config.Manual.Source, now.Source);
            // The sync's value is not gone - it is a disagreement, which is exactly
            // what the merge already did for two syncs that differ.
            Assert.True(now.Mismatch);
            Assert.Contains($"{said.Trusted}", now.Disagreement);
        }
        finally
        {
            Clear();
        }
    }

    [Fact]
    public void LeavesNoRowBehindWhenTheLastFieldIsLetGoOf()
    {
        Set(("comment", JsonValue.Create("renew this before the audit")));
        var annotated = graph.Entities.View(graph.Connection, "server", Server);
        Assert.Contains(graph.Config.Manual.Source, annotated.Sources);
        Assert.Equal("renew this before the audit",
            $"{annotated.Values.Single(value => value.Key == "comment").Trusted}");

        Clear();
        var plain = graph.Entities.View(graph.Connection, "server", Server);
        Assert.DoesNotContain(graph.Config.Manual.Source, plain.Sources);
        Assert.DoesNotContain("comment", plain.Values.Select(value => value.Key));
    }

    [Fact]
    public void IsNeverStale()
    {
        try
        {
            Set(("comment", JsonValue.Create("mine")));
            var facet = graph.Entities.View(graph.Connection, "server", Server).Facets
                .Single(item => item.Source == graph.Config.Manual.Source);
            Assert.False(facet.Frozen);
            Assert.Equal(graph.Config.Manual.SourceType, facet.SourceType);
        }
        finally
        {
            Clear();
        }
    }

    [Fact]
    public void RefusesToInventAnEntityOutOfATypo()
    {
        Assert.Throws<NotFound>(() => Writer.Set(graph.Connection, "server", "prod-server-nonesuch",
            new Dictionary<string, JsonNode?> { ["comment"] = JsonValue.Create("hello") }));
    }

    [Fact]
    public void RefusesAFieldTheTypeDoesNotHave()
    {
        var error = Assert.Throws<QueryError>(() => Set(("nickname", JsonValue.Create("bob"))));
        Assert.Contains("not a field of a server", error.Message);
    }
}

/// <summary>
/// Tags: a list a person puts on an entity, and a condition that asks whether one
/// of them is in it.
/// </summary>
public sealed class Tagging(Graph graph) : IClassFixture<Graph>
{
    private const string One = "prod-server-0031";
    private const string Two = "prod-server-0032";

    private ManualWriter Writer => new(graph.Config, graph.Entities);

    private void Tag(string key, JsonNode? tags) =>
        Writer.Set(graph.Connection, "server", key, new Dictionary<string, JsonNode?> { ["tags"] = tags });

    private int Total(string @operator, string tag) => graph.Runner.Keys(graph.Connection,
        Ask.Query("server", null, Ask.Start(Stage.All, Ask.Where("entity:tags", tag, @operator)))).Total;

    [Fact]
    public void AsksWhetherTheListHasTheTagInIt()
    {
        try
        {
            Tag(One, new JsonArray("audit", "pci"));
            Tag(Two, new JsonArray("audit"));

            Assert.Equal(2, Total(Config.Includes, "audit"));
            Assert.Equal(1, Total(Config.Includes, "pci"));

            // Everything not tagged pci, which is every other server plus the one
            // tagged only audit: an entity with no tags excludes every tag.
            var servers = graph.Runner.Keys(graph.Connection, Ask.Query("server")).Total;
            Assert.Equal(servers - 1, Total(Config.Excludes, "pci"));
        }
        finally
        {
            Tag(One, null);
            Tag(Two, null);
        }
    }

    [Fact]
    public void OffersTheTagsThemselvesAsValues()
    {
        try
        {
            Tag(One, new JsonArray("audit", "pci"));
            Tag(Two, new JsonArray("audit"));
            var offered = graph.Runner.Values(graph.Connection,
                Ask.Query("server", null, Ask.Start(Stage.All, Ask.Where("entity:tags", "", Config.Includes))),
                0, 0);

            // The tags, not the lists they came in: "audit" twice and "pci" once.
            Assert.Equal([("audit", 2), ("pci", 1)],
                offered.Select(item => (item.Value, item.Count)));
        }
        finally
        {
            Tag(One, null);
            Tag(Two, null);
        }
    }

    [Fact]
    public void IsAlwaysAListHoweverItIsGiven()
    {
        try
        {
            // A line of words is what a text box gives; the reads need a list.
            Tag(One, JsonValue.Create(" audit , pci , audit "));
            var written = graph.Entities.View(graph.Connection, "server", One)
                .Values.Single(value => value.Key == "tags");
            Assert.Equal("[\"audit\",\"pci\"]", $"{written.Trusted}".Replace(" ", ""));
            Assert.Equal(1, Total(Config.Includes, "pci"));
        }
        finally
        {
            Tag(One, null);
        }
    }

    [Fact]
    public void IsAskedIncludesRatherThanIs()
    {
        Assert.Equal([Config.Includes, Config.Excludes], graph.Config.OperatorsFor("tags"));
        var error = Assert.Throws<QueryError>(() => Ask.Where("entity:tags", "audit", Config.Is));
        Assert.Contains("includes", error.Message);
    }
}

/// <summary>
/// A denial holds when no source makes the claim.
///
/// This is the rule that keeps `is not` honest across sources: an entity is one row
/// per system, and asking the denial row by row would let a system that never
/// mentioned the field overturn one that did.
/// </summary>
public sealed class DenyingSomething(Graph graph) : IClassFixture<Graph>
{
    private int Total(string @operator, string value) => graph.Runner.Keys(graph.Connection,
        Ask.Query("server", null, Ask.Start(Stage.All, Ask.Where("entity:environment", value, @operator)))).Total;

    [Fact]
    public void IsExactlyWhatTheClaimLeaves()
    {
        var all = graph.Runner.Keys(graph.Connection, Ask.Query("server")).Total;
        var production = Total(Config.Is, "production");
        Assert.True(production > 0, "some server is production");
        // No third answer: every server either has a source saying production or
        // has none, so the two halves are the whole.
        Assert.Equal(all, production + Total(Config.IsNot, "production"));
    }

    [Fact]
    public void SurvivesASourceThatNeverMentionedTheField()
    {
        // The manual source says nothing about environment, so adding one of its rows
        // must not turn this server into one that "is not production".
        var writer = new ManualWriter(graph.Config, graph.Entities);
        // A server the syncs really do call production - the name prefix means
        // nothing - and its own, because test classes run in parallel and two of them
        // annotating one server would clear each other's row.
        const string key = "prod-server-0001";
        try
        {
            writer.Set(graph.Connection, "server", key,
                new Dictionary<string, JsonNode?> { ["comment"] = JsonValue.Create("noted") });
            var notProduction = graph.Runner.Keys(graph.Connection, Ask.Query("server", null,
                Ask.Start(Stage.All,
                    Ask.Where("entity:natural_key", key),
                    Ask.Where("entity:environment", "production", Config.IsNot)))).Total;
            Assert.Equal(0, notProduction);
        }
        finally
        {
            writer.Set(graph.Connection, "server", key,
                new Dictionary<string, JsonNode?> { ["comment"] = null });
        }
    }
}

/// <summary>
/// Plugins: an endpoint the config names, filled in from the entity.
///
/// The endpoint is a stub rather than the mock in the application, so these assert
/// what this code does - what it sends, what it reads back, and what it says when the
/// far end is not there - without needing a server to be running.
/// </summary>
public sealed class Plugging(Graph graph) : IClassFixture<Graph>
{
    /// <summary>An endpoint that records what it was asked and answers what it is told to.</summary>
    private sealed class Stub(string body, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK)
        : HttpMessageHandler, IHttpClientFactory
    {
        public HttpRequestMessage? Asked { get; private set; }
        public string Sent { get; private set; } = "";

        public HttpClient CreateClient(string name) => new(this);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken token)
        {
            Asked = request;
            Sent = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private sealed class Inline(string yaml) : ISchemaSource
    {
        public string Read() => yaml;
    }

    /// <summary>
    /// One plugin, on one type, so a test says only what it means. The type, kind and
    /// url are put in by name rather than interpolated, which keeps the config below
    /// readable as the YAML it is.
    /// </summary>
    private const string OnePlugin = """
        entity_types:
          TYPE:
            label: TYPE
            unified:
              - { key: current_thumbprint, label: Thumbprint }
        plugins:
          TYPE:
            - name: thing
              label: Thing
              kind: KIND
              url: "URL"

        """;

    private const string SendsTheThumbprint = """
              body:
                thumbprint: "{current_thumbprint}"
              message: state
        """;

    private const string ReadsTwoPaths = """
              fields:
                - { path: data.status, label: Status }
                - { path: data.missing, label: Absent }
        """;

    private static Config Declaring(string type, string kind, string url, string extra = "") =>
        Config.Load(new Inline(
            OnePlugin.Replace("TYPE", type).Replace("KIND", kind).Replace("URL", url) + extra));

    private Answer Run(Config config, string type, string key, Stub stub) =>
        new PluginRunner(config, graph.Entities, stub).Run(graph.Connection, type, key, "thing")
            .GetAwaiter().GetResult();

    [Fact]
    public void FillsTheUrlAndTheBodyFromTheEntity()
    {
        var config = Declaring("certificate", Plugin.Action,
            "https://build.example/pipelines/{entity_type}-renewal/runs?key={natural_key}",
            SendsTheThumbprint);
        var stub = new Stub("""{"state":"queued"}""");

        var answer = Run(config, "certificate", Ask.Cert, stub);

        Assert.True(answer.Ok);
        Assert.Equal("queued", answer.Message);
        Assert.Equal("https://build.example/pipelines/certificate-renewal/runs?key=service-0003.example.com",
            stub.Asked!.RequestUri!.AbsoluteUri);
        // The thumbprint came off the merged header, not from anything hard-coded here.
        Assert.Contains("thumbprint", stub.Sent);
        Assert.DoesNotContain("{current_thumbprint}", stub.Sent);
    }

    [Fact]
    public void EscapesWhatItPutsInAUrl()
    {
        // A team's key has a space in it; a url substitution has to survive that.
        var config = Declaring("team", Plugin.Box, "https://watch.example/c?host={natural_key}");
        var stub = new Stub("{}");

        Run(config, "team", Ask.Team, stub);

        Assert.Contains("host=Team%2002", stub.Asked!.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public void ReadsTheNestedPathsTheConfigNames()
    {
        var config = Declaring("server", Plugin.Box, "https://watch.example/c", ReadsTwoPaths);
        var stub = new Stub("""{"data":{"status":"degraded","throughput_mbps":842}}""");

        var answer = Run(config, "server", "prod-server-0022", stub);

        // Named, in the order named, and a path that is not there is empty rather than
        // absent - the box keeps its shape.
        Assert.Equal([("Status", "degraded"), ("Absent", null)],
            answer.Fields.Select(field => (field.Label, field.Value)));
    }

    [Fact]
    public void ShowsEveryPairWhereNoPathsAreNamed()
    {
        var config = Declaring("server", Plugin.Box, "https://watch.example/c");
        var stub = new Stub("""{"status":"healthy","latency_ms":12}""");

        var answer = Run(config, "server", "prod-server-0022", stub);

        Assert.Equal([("status", "healthy"), ("latency ms", "12")],
            answer.Fields.Select(field => (field.Label, field.Value)));
    }

    [Fact]
    public void SaysSoRatherThanFailingWhenTheFarEndDoesNot()
    {
        var config = Declaring("server", Plugin.Box, "https://watch.example/c");
        var answer = Run(config, "server", "prod-server-0022",
                         new Stub("nope", System.Net.HttpStatusCode.BadGateway));

        // The entity is still readable and its other plugins still work, so this is a
        // result with a reason, not a 500.
        Assert.False(answer.Ok);
        Assert.Equal(502, answer.Status);
        Assert.Contains("502", answer.Message);
    }

    [Fact]
    public void RefusesToSendABlankWhereAValueWasWanted()
    {
        // Nothing reports a thumbprint for a server, and a renewal pipeline given a
        // blank one would do the wrong thing quietly.
        var config = Declaring("server", Plugin.Action, "https://build.example/runs", SendsTheThumbprint);
        var error = Assert.Throws<QueryError>(
            () => Run(config, "server", "prod-server-0022", new Stub("{}")));

        Assert.Contains("current thumbprint", error.Message);
    }

    [Fact]
    public void IsOnlyEverAUrlTheConfigNamed()
    {
        // Nothing can ask for a plugin that was not declared, which is what keeps this
        // from being a way to make the API fetch whatever a caller likes.
        Assert.Null(graph.Config.Plugin("certificate", "anything-else"));
        Assert.Throws<NotFound>(() => new PluginRunner(graph.Config, graph.Entities, new Stub("{}"))
            .Run(graph.Connection, "certificate", Ask.Cert, "anything-else").GetAwaiter().GetResult());
    }

    [Fact]
    public void TheConfigDecidesWhichKindEachIs()
    {
        var renew = graph.Config.Plugin("certificate", "renew")!;
        Assert.Equal(Plugin.Action, renew.Kind);
        Assert.Equal("POST", renew.Method);

        var connection = graph.Config.Plugin("server", "connection")!;
        Assert.True(connection.Reads);
        Assert.Equal("GET", connection.Method);
    }
}
