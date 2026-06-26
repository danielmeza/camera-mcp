using CameraMcp.Server.Models;
using CameraMcp.Server.Services;
using ModelContextProtocol.Protocol;

namespace CameraMcp.Server.Tools;

/// <summary>
/// Shapes a capture result into MCP content blocks (metadata text + inline image(s) + resource link),
/// shared by the synchronous capture tools and the queue's get_capture so both return the same thing.
/// </summary>
internal static class CaptureRendering
{
    public static List<ContentBlock> Image(ImageCaptureResult result, ICaptureStore store)
    {
        var resourceUri = store.ToResourceUri(result.FilePath);
        var metadata = CameraJson.Serialize(new
        {
            device = result.DeviceName,
            format = result.Format.Name,
            width = result.Width,
            height = result.Height,
            bytes = result.Bytes.Length,
            path = result.FilePath,
            resourceUri,
        });

        var blocks = new List<ContentBlock>
        {
            new TextContentBlock { Text = metadata },
            ImageContentBlock.FromBytes(result.Bytes, result.MimeType),
        };
        AddResourceLink(blocks, resourceUri, result.MimeType, result.Bytes.Length);
        return blocks;
    }

    public static List<ContentBlock> Video(VideoCaptureResult result, ICaptureStore store)
    {
        var resourceUri = store.ToResourceUri(result.FilePath);
        var metadata = CameraJson.Serialize(new
        {
            path = result.FilePath,
            resourceUri,
            device = result.DeviceName,
            container = result.Container.Name,
            codec = result.Codec.Name,
            width = result.Width,
            height = result.Height,
            fps = result.Fps,
            durationSeconds = result.DurationSeconds,
            fileSizeBytes = result.FileSizeBytes,
            posterIncluded = result.PosterFrame is not null,
        });

        var blocks = new List<ContentBlock> { new TextContentBlock { Text = metadata } };
        if (result.PosterFrame is { Length: > 0 } poster)
        {
            blocks.Add(ImageContentBlock.FromBytes(poster, VideoCaptureResult.PosterMimeType));
        }

        AddResourceLink(blocks, resourceUri, $"video/{result.Container.Name}", result.FileSizeBytes);
        return blocks;
    }

    public static List<ContentBlock> Scene(SceneCaptureResult result, ICaptureStore store)
    {
        var inlineCount = result.Frames.Count(f => f.Bytes is not null);
        var metadata = CameraJson.Serialize(new
        {
            device = result.DeviceName,
            format = result.Format.Name,
            frameCount = result.Frames.Count,
            inlineFrameCount = inlineCount,
            width = result.Width,
            height = result.Height,
            outputDirectory = result.OutputDirectory,
            frames = result.Frames.Select(f => new
            {
                index = f.Index,
                path = f.FilePath,
                resourceUri = store.ToResourceUri(f.FilePath),
                bytes = f.SizeBytes,
                inline = f.Bytes is not null,
            }),
        });

        var blocks = new List<ContentBlock> { new TextContentBlock { Text = metadata } };
        foreach (var frame in result.Frames)
        {
            if (frame.Bytes is { Length: > 0 } bytes)
            {
                blocks.Add(ImageContentBlock.FromBytes(bytes, result.Format.MimeType));
            }
        }

        return blocks;
    }

    /// <summary>Renders any supported capture result (used by get_capture from the job's stored result).</summary>
    public static List<ContentBlock> Render(CaptureKind kind, object result, ICaptureStore store) => kind switch
    {
        CaptureKind.Image => Image((ImageCaptureResult)result, store),
        CaptureKind.Scene => Scene((SceneCaptureResult)result, store),
        CaptureKind.Video => Video((VideoCaptureResult)result, store),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static void AddResourceLink(List<ContentBlock> blocks, string? uri, string mimeType, long size)
    {
        if (!string.IsNullOrEmpty(uri))
        {
            var name = uri[(uri.LastIndexOf('/') + 1)..];
            blocks.Add(new ResourceLinkBlock { Uri = uri, Name = name, MimeType = mimeType, Size = size });
        }
    }
}
