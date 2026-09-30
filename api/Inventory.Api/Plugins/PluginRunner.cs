using System.Data;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Inventory.Api.Entities;
using Inventory.Api.Queries;
using Inventory.Api.Schema;

namespace Inventory.Api.Plugins;

/// <summary>One thing a plugin's answer says, ready to display.</summary>
public sealed record Said(string Label, string? Value);

/// <summary>
/// What came back: enough to show, whether it worked or not.
///
/// A plugin that fails is not an error of this API - the entity is still readable and
/// its other plugins still work - so a failure is a result with <c>Ok</c> false and a
/// reason worth reading, never a 500.
/// </summary>
public sealed record Answer(
    string Name,
    string Label,
    string Kind,
    bool Ok,
    int Status,
    IReadOnlyList<Said> Fields,
    string? Message);

/// <summary>
/// Calling the endpoints the config declares.
///
/// The browser never calls them itself: this does, so the endpoint needs no CORS,
/// can sit inside the network, and can hold a credential the browser must not see.
/// Only urls written in the config are ever called - nothing here takes a url from a
/// request - so there is no way to point it at something it was not configured for.
///
/// <c>{natural_key}</c>, <c>{entity_type}</c> and any field on the entity's merged
/// header can be filled into a url or a body. A url substitution is percent-encoded
/// and a body one is JSON-encoded, each for where it lands, so a key with a space or
/// a quote in it cannot break out of its place.
/// </summary>
public sealed partial class PluginRunner(Config config, EntityReader reader, IHttpClientFactory clients)
{
    /// <summary>Long enough for a pipeline to accept a run, short enough not to hang a page.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(8);

    [GeneratedRegex(@"\{([A-Za-z0-9_]+)\}")]
    private static partial Regex Placeholder();

    public async Task<Answer> Run(IDbConnection db, string entityType, string naturalKey, string name)
    {
        var plugin = config.Plugin(entityType, name)
            ?? throw new NotFound($"a {config.Label(entityType).ToLowerInvariant()} has no '{name}' plugin");
        var view = reader.View(db, entityType, naturalKey);
        var known = Context(view);

        var request = new HttpRequestMessage(
            new HttpMethod(plugin.Method), Fill(plugin, plugin.Url, known, Uri.EscapeDataString));
        if (plugin.Body.Count > 0)
        {
            request.Content = JsonContent.Create(
                plugin.Body.ToDictionary(entry => entry.Key, entry => Fill(plugin, entry.Value, known, Same)));
        }

        var client = clients.CreateClient();
        client.Timeout = Patience;
        try
        {
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            var answered = Parse(body);
            return new Answer(
                plugin.Name, plugin.Label, plugin.Kind, response.IsSuccessStatusCode, (int)response.StatusCode,
                Read(plugin, answered),
                response.IsSuccessStatusCode
                    ? Message(plugin, answered) ?? $"{(int)response.StatusCode}"
                    : $"{plugin.Label} answered {(int)response.StatusCode}");
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException)
        {
            // Not reachable, or too slow. The page says so and stays usable.
            return new Answer(plugin.Name, plugin.Label, plugin.Kind, false, 0, [],
                              $"{plugin.Label} could not be reached");
        }
    }

    /// <summary>What a template may be filled from: the entity, as the graph has it.</summary>
    private static Dictionary<string, string> Context(EntityView view)
    {
        var known = new Dictionary<string, string>
        {
            ["natural_key"] = view.NaturalKey,
            ["entity_type"] = view.EntityType,
        };
        foreach (var value in view.Values)
        {
            known[value.Key] = $"{value.Trusted}";
        }
        return known;
    }

    /// <summary>
    /// Fill one template, escaping each substitution for where it lands.
    ///
    /// A placeholder nothing reports is refused rather than sent empty: a renewal
    /// pipeline given a blank thumbprint would do the wrong thing quietly, and the
    /// reason is worth saying out loud.
    /// </summary>
    private static string Fill(Plugin plugin, string template, Dictionary<string, string> known,
                               Func<string, string> escape) =>
        Placeholder().Replace(template, match =>
        {
            var wanted = match.Groups[1].Value;
            if (!known.TryGetValue(wanted, out var found) || found.Length == 0)
            {
                throw new QueryError(
                    $"{plugin.Label} needs {wanted.Replace('_', ' ')}, which nothing reports for this entity");
            }
            return escape(found);
        });

    private static string Same(string value) => value;

    /// <summary>
    /// The paths the config named. Where it named none, a box shows every top-level
    /// pair - it exists to display something - while an action shows nothing, because
    /// what an action has to say is its message.
    /// </summary>
    private static IReadOnlyList<Said> Read(Plugin plugin, JsonNode? answered)
    {
        if (answered is null)
        {
            return [];
        }
        if (plugin.Fields.Length > 0)
        {
            return [.. plugin.Fields.Select(field => new Said(field.Label, Text(At(answered, field.Path))))];
        }
        return plugin.Reads && answered is JsonObject shape
            ? [.. shape.Select(entry => new Said(entry.Key.Replace('_', ' '), Text(entry.Value)))]
            : [];
    }

    private static string? Message(Plugin plugin, JsonNode? answered) =>
        plugin.Message is null || answered is null ? null : Text(At(answered, plugin.Message));

    /// <summary>One value by dotted path, because a real endpoint nests its answer.</summary>
    private static JsonNode? At(JsonNode node, string path)
    {
        JsonNode? found = node;
        foreach (var step in path.Split('.'))
        {
            found = found is JsonObject shape && shape.TryGetPropertyValue(step, out var next) ? next : null;
            if (found is null)
            {
                return null;
            }
        }
        return found;
    }

    private static string? Text(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value => value.TryGetValue<string>(out var text) ? text : value.ToJsonString(),
        _ => node.ToJsonString(),
    };

    private static JsonNode? Parse(string body)
    {
        try
        {
            return JsonNode.Parse(body);
        }
        catch
        {
            // An endpoint that does not answer JSON is allowed to exist; there is just
            // nothing to read out of it.
            return null;
        }
    }
}
