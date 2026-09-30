using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Inventory.Api;
using Inventory.Api.Entities;
using Inventory.Api.Plugins;
using Inventory.Api.Queries;
using Inventory.Api.Schema;

// The API over the inventory graph.
//
// Reads, and one write: what a person says about an entity. Everything else is
// written by the syncs (ADF), not here.

var builder = WebApplication.CreateBuilder(args);
var root = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", ".."));

// The API describes itself. The document is generated from the endpoints below, so
// it cannot drift from them; what it cannot know is written on each one as a summary.
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, context, token) =>
{
    document.Info = new()
    {
        Title = "Inventory graph",
        Version = "1",
        Description =
            "A read-only view over inventory the syncs deliver, plus the one thing a person writes. "
            + "Read `/api/schema` first: it says which types exist, which shapes of relation are in the "
            + "data, what may be asked of each kind of field, and what each type's plugins are - so a "
            + "client can build a query without hard-coding any of it.",
    };
    return Task.CompletedTask;
}));

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ISchemaSource>(
    new FileSchemaSource(builder.Configuration["Schema"] ?? Path.Combine(root, "schema.yaml")));
builder.Services.AddSingleton(provider => Config.Load(provider.GetRequiredService<ISchemaSource>()));
builder.Services.AddSingleton(new Db(builder.Configuration["Database"] ?? Path.Combine(root, "inventory.db")));
builder.Services.AddSingleton<Options>();
builder.Services.AddSingleton<EntityReader>();
builder.Services.AddSingleton<ManualWriter>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<PluginRunner>();
builder.Services.AddSingleton<Runner>();
builder.Services.AddScoped<IDbConnection>(provider => provider.GetRequiredService<Db>().Open());
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
// The Angular dev server lives on another port; nothing here is a secret.
builder.Services.AddCors(options => options.AddDefaultPolicy(
    policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

// The document at /openapi/v1.json, and something to read it with at /docs - which is
// where the application's own "API" link points.
app.MapOpenApi();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", "Inventory graph");
    options.RoutePrefix = "docs";
    options.DocumentTitle = "Inventory graph API";
});

// A query that cannot be built as asked is the caller's mistake, not a failure.
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (QueryError error)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { detail = error.Message });
    }
    catch (NotFound missing)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsJsonAsync(new { detail = missing.Message });
    }
});

app.MapGet("/health", () => new { status = "ok" })
    .WithTags("Health")
    .WithSummary("Whether the API is up. Nothing else to read into it.");

// The shapes in the data, each read from both ends, and every field a condition can
// be about - enough to build a query without guessing. The config is YAML on disk
// and JSON here, which is the only form the application ever sees it in.
app.MapGet("/api/schema", (IDbConnection db, Config config, Options options) => new
{
    shapes = options.Shapes(db).Select(shape => new
    {
        source_type = shape.SourceType,
        relation_type = shape.RelationType,
        target_type = shape.TargetType,
        count = shape.Count,
        standing_on_source = config.Reads(shape.SourceType, shape.RelationType, shape.TargetType, Config.Outgoing),
        standing_on_target = config.Reads(shape.TargetType, shape.RelationType, shape.SourceType, Config.Incoming),
    }),
    operators = Config.Operators,
    field_types = config.FieldTypes,
    // The source a person writes as, and the fields only they ever set.
    manual = new
    {
        source = config.Manual.Source,
        source_type = config.Manual.SourceType,
        fields = config.Manual.Fields.Select(field => new { key = field.Key, label = field.Label }),
    },
    labels = config.Labels,
    matches = Stage.Matches,
    types = options.TypeCounts(db).Select(item => new
    {
        type = item.Type,
        label = config.Label(item.Type),
        count = item.Count,
    }),
    entity_types = config.EntityTypes.Select(item => new
    {
        type = item.Name,
        label = item.Label,
        filters = item.Filters,
        // Every field the header carries, so a page can offer one a person has not
        // filled in yet.
        unified = item.Unified.Select(field => new { key = field.Key, label = field.Label }),
    }),
    relations = config.Relations.Select(item => new
    {
        source_type = item.SourceType,
        relation_type = item.RelationType,
        target_type = item.TargetType,
        identity = item.Identity,
        attributes = item.Attributes,
        filters = item.Filters,
        description = item.Description,
    }),
})
    .WithTags("Schema")
    .WithSummary("Everything a query can be built out of: the shapes in the data read from both ends, what may be asked of each kind of field, the labels, the manual source and the plugins. The config is YAML on disk and JSON here.");

// One entity, merged across its sources, with the hops it has and the neighbours
// its page is made of.
app.MapGet("/api/entities/{entityType}/{naturalKey}", (
    string entityType, string naturalKey,
    IDbConnection db, Config config, EntityReader entities, Options options,
    bool frozen = false) =>
{
    var view = entities.View(db, entityType, naturalKey);
    return Results.Ok(new
    {
        type = view.EntityType,
        natural_key = view.NaturalKey,
        label = view.Label,
        known = view.Known,
        sources = view.Sources,
        values = view.Values.Select(Dto.Of),
        facets = view.Facets.Select(Dto.Of),
        hops = options.HopsOf(db, entityType, naturalKey).Select(item => new
        {
            hop = item.Hop.Key,
            reads = item.Hop.Reads(config, entityType),
            total = item.Total,
            active = item.Active,
        }),
        neighbourhood = entities.Neighbourhood(db, entityType, naturalKey, frozen)
            .Select(box => Dto.Of(box, config)),
        // What else can be done with it. The page reads the boxes and offers the
        // actions as buttons; each is fetched on its own, so a slow endpoint holds up
        // only itself.
        plugins = config.PluginsOf(entityType).Select(Dto.Of),
    });
})
    .WithTags("Entities")
    .WithSummary("One entity, merged across its sources, with each source's own account, the hops it has, the neighbourhood its page is made of, and what its plugins are.");

// Run one plugin against one entity: read a box, or do an action. GET and POST both
// map here because it is the config that decides which a plugin is, not the caller -
// and the page should not be able to press a button by reading a page.
app.MapMethods("/api/entities/{entityType}/{naturalKey}/plugins/{name}", ["GET", "POST"], async (
    string entityType, string naturalKey, string name, HttpRequest incoming,
    IDbConnection db, Config config, PluginRunner plugins) =>
{
    var plugin = config.Plugin(entityType, name);
    if (plugin is not null && plugin.Reads != HttpMethods.IsGet(incoming.Method))
    {
        return Results.BadRequest(new
        {
            detail = plugin.Reads
                ? $"{plugin.Label} is a box: read it with GET"
                : $"{plugin.Label} is an action: do it with POST",
        });
    }
    return Results.Ok(Dto.Of(await plugins.Run(db, entityType, naturalKey, name)));
})
    .WithTags("Plugins")
    .WithSummary("Run one plugin against one entity. GET reads a box, POST does an action, and the config decides which a plugin is - so reading a page cannot press a button. A plugin that fails answers with a reason rather than failing this call.");

// What a person says about one entity, merged into the manual source's row. A field
// given as null or blank is an override let go of; the last one let go of takes the
// row with it.
app.MapPut("/api/entities/{entityType}/{naturalKey}/manual", (
    string entityType, string naturalKey, ManualBody body,
    IDbConnection db, ManualWriter manual) =>
{
    manual.Set(db, entityType, naturalKey, body.Fields ?? []);
    // Nothing useful to return: the page reads the entity again, which is one call it
    // already makes and the only way to see the facets change too.
    return Results.NoContent();
})
    .WithTags("Entities")
    .WithSummary("What a person says about an entity, merged into the manual source's row. That source outranks the syncs, so this is how a field is overridden, tagged or commented on without editing what ADF owns. A field given as blank is an override let go of.");

// Run a query: a start, then a stage per hop, each narrowed by conditions. With no
// hops the rows are entities; with hops they are the occurrences of the last one.
app.MapPost("/api/query", (QueryBody body, IDbConnection db, Config config, Runner runner,
                          EntityReader entities) =>
{
    var query = body.ToQuery(config);
    if (query.ListsEntities)
    {
        var keys = runner.Keys(db, query);
        var views = entities.Views(db, keys.Items);
        return Results.Ok(new
        {
            reads = query.Reads(config),
            columns = config.Type(query.Type).Unified.Select(field => field.Key),
            data = keys.Items.Select(key => Dto.Of(views[(key.Type, key.Key)])),
            total = keys.Total,
            page = keys.PageNumber,
            pages = keys.Pages,
        });
    }
    var rows = runner.Occurrences(db, query);
    var standing = query.Types[^2];
    return Results.Ok(new
    {
        reads = query.Reads(config),
        columns = query.Last.Hop!.Shape(config, standing).Columns,
        reached = query.Last.Hop!.OtherType,
        data = rows.Items.Select(Dto.Of),
        total = rows.Total,
        page = rows.PageNumber,
        pages = rows.Pages,
    });
})
    .WithTags("Query")
    .WithSummary("A start and a chain of stages, each narrowed by up to three conditions. With no hops the rows are entities; with hops they are the occurrences of the last one. A query that cannot be built as asked comes back 400 with the reason.");

// The values one condition's field takes, for its picker. The same read the query
// uses, addressed on its own - the Python implementation computes this while
// rendering, which a JSON API cannot.
app.MapPost("/api/values", (ValuesBody body, IDbConnection db, Config config, Runner runner) =>
{
    var query = body.Query.ToQuery(config);
    if (body.Stage < 0 || body.Stage >= query.Stages.Count)
    {
        throw new QueryError($"there is no stage {body.Stage}");
    }
    if (body.Condition < 0 || body.Condition >= query.Stages[body.Stage].Conditions.Count)
    {
        throw new QueryError($"stage {body.Stage} has no condition {body.Condition}");
    }
    var found = runner.Values(db, query, body.Stage, body.Condition);
    return Results.Ok(new
    {
        data = found.Take(Query.MaxValues).Select(item => new { value = item.Value, count = item.Count }),
        truncated = found.Count > Query.MaxValues,
    });
})
    .WithTags("Query")
    .WithSummary("The values one condition's field takes, for its picker - read from the rows the stage crosses, so a choice is only ever something that would match.");

// What a stage can offer: the hops available from where it stands, and the fields a
// condition on it can be about. The builder asks for one stage at a time.
app.MapGet("/api/stage", (
    string type, string? key, string? hop, IDbConnection db, Config config, Options options) =>
{
    var picked = string.IsNullOrEmpty(hop) ? null : Hop.Parse(hop);
    return Results.Ok(new
    {
        hops = (key is null
                ? options.HopsFrom(db, type).Select(item => new Available(item, 0, 0))
                : options.HopsOf(db, type, key))
            .Select(item => new
            {
                hop = item.Hop.Key,
                reads = item.Hop.Reads(config, type),
                total = item.Total,
                active = item.Active,
            }),
        fields = options.Fields(picked, type),
    });
})
    .WithTags("Query")
    .WithSummary("What one stage can offer: the hops available from where it stands, and the fields a condition on it can be about, each with how it reads.");

// Stand-ins for the systems the plugins point at. Nothing else depends on them.
app.MapMocks();

app.Run();

/// <summary>Turning the read shapes into what goes on the wire.</summary>
internal static class Dto
{
    public static object Of(Value value) => new
    {
        key = value.Key,
        label = value.Label,
        value = value.Trusted,
        source = value.Source,
        reported = value.Reported,
        mismatch = value.Mismatch,
    };

    public static object Of(Facet facet) => new
    {
        source = facet.Source,
        source_type = facet.SourceType,
        external_id = facet.ExternalId,
        discovered_at = facet.DiscoveredAt,
        last_seen_at = facet.LastSeenAt,
        synced_at = facet.SyncedAt,
        frozen = facet.Frozen,
        metadata = facet.Metadata,
    };

    public static object Of(EntityView view) => new
    {
        type = view.EntityType,
        natural_key = view.NaturalKey,
        label = view.Label,
        known = view.Known,
        sources = view.Sources,
        values = view.Values.Select(Of),
    };

    public static object Of(Occurrence row) => new
    {
        from = new { type = row.FromType, natural_key = row.FromKey },
        entity = new { type = row.ToType, natural_key = row.ToKey },
        metadata = row.Metadata,
        source = row.Source,
        last_seen_at = row.LastSeenAt,
        frozen = row.Frozen,
    };

    public static object Of(Plugin plugin) => new
    {
        name = plugin.Name,
        label = plugin.Label,
        kind = plugin.Kind,
    };

    public static object Of(Answer answer) => new
    {
        name = answer.Name,
        label = answer.Label,
        kind = answer.Kind,
        ok = answer.Ok,
        status = answer.Status,
        fields = answer.Fields.Select(item => new { label = item.Label, value = item.Value }),
        message = answer.Message,
    };

    public static object Of(Box box, Config config) => new
    {
        hop = box.Hop.Key,
        reads = box.Hop.Reads(config, box.OwnType),
        other_type = box.Hop.OtherType,
        other_label = config.Label(box.Hop.OtherType),
        neighbours = box.Neighbours,
        occurrences = box.Occurrences,
        frozen = box.Frozen,
        listed = box.Listed,
        others = box.Others,
        named = box.Named.Select(item => new
        {
            type = item.EntityType,
            natural_key = item.NaturalKey,
            occurrences = item.Occurrences,
        }),
    };
}

/// <summary>What a query looks like coming in. The same shape the Python API takes.</summary>
internal sealed record ConditionBody(string? Field, string? Operator, string? Value);

internal sealed record StageBody(string? Hop, string? Match, List<ConditionBody>? Conditions);

internal sealed record QueryBody(
    string Type,
    string? Key,
    List<StageBody>? Stages,
    bool Frozen = false,
    int Page = 1,
    int PageSize = 25)
{
    public Query ToQuery(Config config)
    {
        var stages = (Stages is null || Stages.Count == 0 ? [new StageBody(null, null, null)] : Stages)
            .Select(stage => Stage.Of(
                string.IsNullOrEmpty(stage.Hop) ? null : Inventory.Api.Queries.Hop.Parse(stage.Hop),
                string.IsNullOrWhiteSpace(stage.Match) ? Stage.All : stage.Match,
                [.. (stage.Conditions ?? []).Select(item =>
                    Condition.Build(config, item.Field, item.Operator, item.Value))]))
            .ToList();
        return new Query(Type, string.IsNullOrEmpty(Key) ? null : Key, stages, Frozen,
            Math.Max(1, Page), Math.Clamp(PageSize, 1, Query.MaxPageSize));
    }
}

internal sealed record ValuesBody(QueryBody Query, int Stage, int Condition);

/// <summary>What a person is saying about an entity: field by field, as given.</summary>
internal sealed record ManualBody(Dictionary<string, JsonNode?>? Fields);
