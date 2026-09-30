using System.Text.Json.Nodes;

namespace Inventory.Api.Plugins;

/// <summary>
/// Two endpoints pretending to be somebody else's.
///
/// The plugin runner cannot tell that these are local: it makes a real HTTP request,
/// against a real url out of the config, and reads a real JSON answer. So everything
/// between the button and the far end is the finished thing, and going real means
/// changing two urls in <c>schema.yaml</c> and deleting this file.
///
/// They are deliberately shaped like the systems they stand for - Azure DevOps
/// answers a run with an id and a state, Dynatrace nests its measurements under a
/// wrapper - because a plugin that only works against a flat, friendly answer would
/// prove nothing.
/// </summary>
internal static class Mock
{
    public static void MapMocks(this WebApplication app)
    {
        // Azure DevOps: queue a pipeline run. Echoes the parameters it was sent, so
        // what the config filled in is visible in the answer.
        app.MapPost("/mock/devops/pipelines/{pipeline}/runs", (string pipeline, JsonObject? parameters) => new
        {
            id = Random.Shared.Next(4000, 9000),
            state = "queued",
            pipeline,
            url = $"https://dev.azure.com/example/_build/results?pipeline={pipeline}",
            queued_at = DateTime.UtcNow.ToString("O"),
            parameters,
        })
        .WithTags("Mocks")
        .WithSummary("Stands in for Azure DevOps: queues a pipeline run. Echoes the parameters it was sent, so what a plugin filled in is visible in the answer. Not part of this API - it is here so the plugins have something real to call.");

        // Dynatrace: what one host's connectivity looks like right now. Stable per
        // host rather than random, so a page does not change its mind on refresh.
        app.MapGet("/mock/dynatrace/connections", (string host, string? region) =>
        {
            var seed = Math.Abs(host.GetHashCode());
            var states = new[] { "healthy", "healthy", "healthy", "degraded", "unreachable" };
            return new
            {
                host,
                region,
                data = new
                {
                    status = states[seed % states.Length],
                    throughput_mbps = 80 + (seed % 920),
                    latency_ms = 4 + (seed % 60),
                    checked_at = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                },
            };
        })
        .WithTags("Mocks")
        .WithSummary("Stands in for Dynatrace: one host's connectivity, nested under a wrapper the way a real one would be. Stable per host rather than random, so a page does not change its mind on refresh.");
    }
}
