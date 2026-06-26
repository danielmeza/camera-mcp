using System.Globalization;
using System.Text.Json;
using CameraMcp.Server.Models;
using CameraMcp.Server.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CameraMcp.Server;

/// <summary>Maps the device-facing side-channel routes onto the shared Kestrel host.</summary>
internal static class WebEndpoints
{
    public static void MapDeviceEndpoints(this WebApplication app)
    {
        // A device triggers a capture (still or rapid-fire burst). Token in ?token= or X-Session-Token.
        app.MapPost("/sessions/{sessionId}/trigger",
            async (string sessionId, HttpRequest http, ICaptureSessionService sessions, CancellationToken ct) =>
            {
                var token = ExtractToken(http);
                var request = await ReadTriggerRequestAsync(http, ct).ConfigureAwait(false);
                var result = await sessions.TriggerAsync(sessionId, token, request, ct).ConfigureAwait(false);
                return result.Outcome switch
                {
                    SessionOutcome.Ok => Results.Json(new
                    {
                        seq = result.Seq,
                        name = result.Name,
                        description = result.Description,
                        frameCount = result.FrameCount,
                        kind = result.IsBurst ? "burst" : "still",
                    }),
                    SessionOutcome.Unauthorized => Results.Json(new { error = "invalid or missing token" }, statusCode: 401),
                    SessionOutcome.NotFound => Results.Json(new { error = "no such session" }, statusCode: 404),
                    _ => Results.Json(new { error = result.Error ?? "capture failed" }, statusCode: 500),
                };
            });

        // A device discovers / health-checks its session.
        app.MapGet("/sessions/{sessionId}",
            (string sessionId, HttpRequest http, ICaptureSessionService sessions) =>
            {
                var descriptor = sessions.Describe(sessionId, ExtractToken(http));
                return descriptor.Outcome switch
                {
                    SessionOutcome.Ok => Results.Json(new { sessionId = descriptor.SessionId, device = descriptor.Device }),
                    SessionOutcome.Unauthorized => Results.Json(new { error = "invalid or missing token" }, statusCode: 401),
                    _ => Results.Json(new { error = "no such session" }, statusCode: 404),
                };
            });
    }

    private static string? ExtractToken(HttpRequest http) =>
        http.Query["token"].FirstOrDefault() ?? http.Headers["X-Session-Token"].FirstOrDefault();

    /// <summary>Reads optional name/description/count/interval overrides from the query string and/or a JSON body.</summary>
    private static async Task<TriggerRequest> ReadTriggerRequestAsync(HttpRequest http, CancellationToken ct)
    {
        string? name = http.Query["name"].FirstOrDefault();
        string? description = http.Query["description"].FirstOrDefault();
        int? count = TryInt(http.Query["count"].FirstOrDefault());
        double? interval = TryDouble(http.Query["interval"].FirstOrDefault());

        if (http.ContentLength is > 0 && (http.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            try
            {
                using var doc = await JsonDocument.ParseAsync(http.Body, cancellationToken: ct).ConfigureAwait(false);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    name ??= GetJsonString(root, "name");
                    description ??= GetJsonString(root, "description");
                    count ??= GetJsonInt(root, "count");
                    interval ??= GetJsonDouble(root, "interval");
                }
            }
            catch (Exception) { /* ignore a malformed body — query params still apply */ }
        }

        return new TriggerRequest { Name = name, Description = description, Count = count, IntervalSeconds = interval };
    }

    private static int? TryInt(string? s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static double? TryDouble(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string? GetJsonString(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetJsonInt(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static double? GetJsonDouble(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
}
