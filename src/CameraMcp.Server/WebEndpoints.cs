using System.Globalization;
using System.Text.Json;
using CameraMcp.Server.Models;
using CameraMcp.Server.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CameraMcp.Server;

/// <summary>Maps the device-facing side-channel routes onto the shared Kestrel host.</summary>
internal static class WebEndpoints
{
    public static void MapDeviceEndpoints(this WebApplication app, string? corsPolicy = null)
    {
        // Group the device routes so an optional CORS policy (an explicit origin allowlist) can be applied
        // to all of them at once. Without a policy they have no CORS metadata — fine for devices/servers.
        var group = app.MapGroup(string.Empty);

        // A device triggers a capture (still or rapid-fire burst). Token in ?token= or X-Session-Token.
        group.MapPost("/sessions/{sessionId}/trigger",
            async (string sessionId, HttpRequest http, [FromServices] ICaptureSessionService sessions, CancellationToken ct) =>
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
        group.MapGet("/sessions/{sessionId}",
            (string sessionId, HttpRequest http, [FromServices] ICaptureSessionService sessions) =>
            {
                var descriptor = sessions.Describe(sessionId, ExtractToken(http));
                return descriptor.Outcome switch
                {
                    SessionOutcome.Ok => Results.Json(new { sessionId = descriptor.SessionId, device = descriptor.Device }),
                    SessionOutcome.Unauthorized => Results.Json(new { error = "invalid or missing token" }, statusCode: 401),
                    _ => Results.Json(new { error = "no such session" }, statusCode: 404),
                };
            });

        // Live preview: a viewer page and the MJPEG stream behind it (for a human in a browser).
        group.MapGet("/preview/{previewId}",
            (string previewId, HttpRequest http, HttpResponse response, [FromServices] IPreviewService preview) =>
                preview.ServePageAsync(previewId, ExtractToken(http), response));

        group.MapGet("/preview/{previewId}/stream",
            (string previewId, HttpRequest http, HttpResponse response, [FromServices] IPreviewService preview, CancellationToken ct) =>
                preview.ServeStreamAsync(previewId, ExtractToken(http), response, ct));

        if (corsPolicy is not null)
        {
            group.RequireCors(corsPolicy);
        }
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
