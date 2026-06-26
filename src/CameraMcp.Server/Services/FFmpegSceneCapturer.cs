using System.Globalization;
using CameraMcp.Server.Configuration;
using CameraMcp.Server.Models;
using Microsoft.Extensions.Options;

namespace CameraMcp.Server.Services;

/// <summary>Captures a sequence of stills at an interval from a device into a directory.</summary>
public interface ISceneCapturer
{
    /// <summary>
    /// Runs a single ffmpeg pass that samples one frame every <c>options.IntervalSeconds</c> into
    /// <paramref name="outputDirectory"/>, then returns the produced frames in order. A bounded
    /// prefix of frames is read inline (per the configured caps); the rest are path-only.
    /// </summary>
    Task<IReadOnlyList<SceneFrame>> CaptureAsync(
        IReadOnlyList<string> inputArgs,
        SceneCaptureOptions options,
        string outputDirectory,
        double warmupSeconds,
        CancellationToken cancellationToken);
}

/// <summary>
/// Drives ffmpeg to write a numbered frame sequence (<c>frame-%03d.&lt;ext&gt;</c>) into a directory,
/// then reads them back in numeric order. Stale frames from a prior run into the same directory are
/// cleared first, and only a bounded prefix of frames is read into memory for the inline response.
/// </summary>
public sealed class FFmpegSceneCapturer : ISceneCapturer
{
    internal const string FramePrefix = "frame";

    private readonly IFFmpegLocator _locator;
    private readonly IProcessRunner _runner;
    private readonly CameraMcpOptions _options;

    public FFmpegSceneCapturer(IFFmpegLocator locator, IProcessRunner runner, IOptions<CameraMcpOptions> options)
    {
        _locator = locator;
        _runner = runner;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<SceneFrame>> CaptureAsync(
        IReadOnlyList<string> inputArgs,
        SceneCaptureOptions options,
        string outputDirectory,
        double warmupSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputArgs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var glob = $"{FramePrefix}-*.{options.Format.FileExtension}";

        // Clear any frames left by a previous capture into this (possibly caller-supplied) directory,
        // so the read-back collects only this run's output.
        foreach (var stale in Directory.GetFiles(outputDirectory, glob))
        {
            File.Delete(stale);
        }

        var ffmpeg = _locator.Resolve();
        var pattern = Path.Combine(outputDirectory, $"{FramePrefix}-%03d.{options.Format.FileExtension}");

        var args = options.IsNonUniform
            ? FFmpegArguments.BuildTimedSceneArgs(
                inputArgs, options.Format, options.Quality, options.ResolveTimestamps(warmupSeconds), pattern)
            : FFmpegArguments.BuildSceneCaptureArgs(
                inputArgs, options.Format, options.Quality, options.FrameCount,
                options.ResolveUniformInterval(_options.DefaultSceneIntervalSeconds), warmupSeconds, pattern);

        var span = options.SpanSeconds(_options.DefaultSceneIntervalSeconds);
        var timeout = TimeSpan.FromSeconds(span + warmupSeconds + _options.FFmpegTimeoutSeconds);

        var result = await _runner.RunAsync(ffmpeg, args, standardInput: null, timeout, cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new CaptureFailedException($"Scene capture failed. {FFmpegError.Tail(result.StandardError)}");
        }

        var ordered = OrderFrames(Directory.GetFiles(outputDirectory, glob));
        if (ordered.Count == 0)
        {
            throw new CaptureFailedException(
                "Scene capture produced no frames. The device may not be streaming, or the interval is longer than the camera kept the stream open.");
        }

        // Read bytes inline for a bounded prefix only; the rest are returned as path + size.
        var inlineFramesLeft = _options.MaxInlineSceneFrames;
        var inlineBytesLeft = _options.MaxInlineSceneBytes;

        var frames = new List<SceneFrame>(ordered.Count);
        foreach (var (index, path) in ordered)
        {
            var size = new FileInfo(path).Length;
            byte[]? bytes = null;
            if (inlineFramesLeft > 0 && size <= inlineBytesLeft)
            {
                bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                inlineFramesLeft--;
                inlineBytesLeft -= size;
            }

            frames.Add(new SceneFrame(index, path, size, bytes));
        }

        return frames;
    }

    /// <summary>
    /// Orders frame files by their parsed numeric index (robust beyond 999, where the zero-padded
    /// names stop being fixed width and an ordinal string sort would misorder them).
    /// </summary>
    internal static IReadOnlyList<(int Index, string Path)> OrderFrames(IEnumerable<string> files) =>
        files
            .Select(path => (Index: ParseFrameIndex(path), Path: path))
            .Where(x => x.Index > 0)
            .OrderBy(x => x.Index)
            .ToList();

    /// <summary>Extracts the integer index from a <c>frame-NNN.ext</c> filename, or 0 if it doesn't match.</summary>
    internal static int ParseFrameIndex(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var prefix = FramePrefix + "-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return 0;
        }

        return int.TryParse(name.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            ? index
            : 0;
    }
}
