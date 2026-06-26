using System.Collections.Concurrent;
using System.Globalization;
using CameraMcp.Server.Configuration;
using CameraMcp.Server.Models;
using FlashCap;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CameraMcp.Server.Services;

/// <summary>
/// Orchestrates capture: enumerates devices via FlashCap, selects a capture mode, serializes access
/// per physical device, and delegates encoding/recording to the ffmpeg-backed services.
/// </summary>
public sealed class CameraService : ICameraService, IDisposable
{
    private readonly IStillCapturer _stillCapturer;
    private readonly IVideoRecorder _videoRecorder;
    private readonly ISceneCapturer _sceneCapturer;
    private readonly CameraMcpOptions _options;
    private readonly ILogger<CameraService> _logger;

    // One lock per physical device so concurrent tool calls never open the same camera twice.
    // Lazy guarantees exactly one SemaphoreSlim per key even under concurrent first-access.
    private readonly ConcurrentDictionary<string, Lazy<SemaphoreSlim>> _deviceLocks = new();

    public CameraService(
        IStillCapturer stillCapturer,
        IVideoRecorder videoRecorder,
        ISceneCapturer sceneCapturer,
        IOptions<CameraMcpOptions> options,
        ILogger<CameraService> logger)
    {
        _stillCapturer = stillCapturer;
        _videoRecorder = videoRecorder;
        _sceneCapturer = sceneCapturer;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CameraDevice>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        var devices = await EnumerateAsync(cancellationToken).ConfigureAwait(false);
        return devices
            .Select(d => new CameraDevice(d.Id, d.Name, d.Platform.Name, d.DisplayFormats))
            .ToList();
    }

    public async Task<ImageCaptureResult> CaptureImageAsync(ImageCaptureOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(_options.MaxStartDelaySeconds);
        await DelayStartAsync(options.StartDelaySeconds, cancellationToken).ConfigureAwait(false);

        var devices = await EnumerateAsync(cancellationToken).ConfigureAwait(false);
        var device = ResolveDevice(devices, options.DeviceId);

        var index = FormatSelector.SelectIndex(device.SelectionFormats, options.Width, options.Height, fps: null);
        var characteristics = device.Characteristics[index];

        var outputPath = ResolveImageOutputPath(options);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var inputArgs = PlatformDeviceMapper.BuildVideoInput(
            device.Platform, device.VideoTarget, characteristics.Width, characteristics.Height, FrameRate(characteristics));

        var bytes = await WithDeviceLockAsync(device, () =>
        {
            _logger.LogInformation(
                "Capturing still from {Device} at {Width}x{Height} (warmup {Warmup} frames).",
                device.Name, characteristics.Width, characteristics.Height, _options.ImageWarmupFrames);
            return _stillCapturer.CaptureAsync(inputArgs, options, outputPath, cancellationToken);
        }, cancellationToken).ConfigureAwait(false);

        return new ImageCaptureResult(bytes, options.Format, characteristics.Width, characteristics.Height, device.Name, outputPath);
    }

    private string ResolveImageOutputPath(ImageCaptureOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.OutputPath))
        {
            return Path.GetFullPath(options.OutputPath);
        }

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        return Path.GetFullPath(Path.Combine(_options.OutputDirectory, $"image-{timestamp}.{options.Format.FileExtension}"));
    }

    public async Task<VideoCaptureResult> CaptureVideoAsync(VideoCaptureOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(_options.MaxVideoDurationSeconds, _options.MaxStartDelaySeconds);

        if (options.Audio)
        {
            // The audio plumbing exists end-to-end but is unverified per platform, so it stays gated off.
            throw new CaptureValidationException("Audio capture is not supported in this version; set audio to false.");
        }

        await DelayStartAsync(options.StartDelaySeconds, cancellationToken).ConfigureAwait(false);

        var devices = await EnumerateAsync(cancellationToken).ConfigureAwait(false);
        var device = ResolveDevice(devices, options.DeviceId);

        var index = FormatSelector.SelectIndex(device.SelectionFormats, options.Width, options.Height, options.Fps);
        var characteristics = device.Characteristics[index];

        var outputPath = ResolveOutputPath(options);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var inputArgs = PlatformDeviceMapper.BuildVideoInput(
            device.Platform, device.VideoTarget, characteristics.Width, characteristics.Height, options.Fps);

        var recorded = await WithDeviceLockAsync(device, async () =>
        {
            _logger.LogInformation(
                "Recording {Duration}s from {Device} to {Path}.", options.DurationSeconds, device.Name, outputPath);
            return await _videoRecorder.RecordAsync(inputArgs, options, outputPath, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        return new VideoCaptureResult(
            outputPath,
            options.Container,
            options.Codec,
            characteristics.Width,
            characteristics.Height,
            options.Fps,
            options.DurationSeconds,
            recorded.FileSizeBytes,
            device.Name,
            recorded.PosterFrame);
    }

    public async Task<SceneCaptureResult> CaptureSceneAsync(SceneCaptureOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(
            _options.MaxSceneFrames, _options.MaxVideoDurationSeconds,
            _options.DefaultSceneIntervalSeconds, _options.MaxStartDelaySeconds);
        await DelayStartAsync(options.StartDelaySeconds, cancellationToken).ConfigureAwait(false);

        var devices = await EnumerateAsync(cancellationToken).ConfigureAwait(false);
        var device = ResolveDevice(devices, options.DeviceId);

        var index = FormatSelector.SelectIndex(device.SelectionFormats, options.Width, options.Height, fps: null);
        var characteristics = device.Characteristics[index];

        var outputDirectory = ResolveSceneDirectory(options);
        Directory.CreateDirectory(outputDirectory);

        var frameRate = FrameRate(characteristics);
        var warmupSeconds = WarmupSeconds(frameRate);

        var inputArgs = PlatformDeviceMapper.BuildVideoInput(
            device.Platform, device.VideoTarget, characteristics.Width, characteristics.Height, frameRate);

        var frames = await WithDeviceLockAsync(device, () =>
        {
            _logger.LogInformation(
                "Capturing scene of {Count} frames ({Mode}) from {Device} into {Dir}.",
                options.ResolveFrameCount(), options.IsNonUniform ? "non-uniform" : "uniform", device.Name, outputDirectory);
            return _sceneCapturer.CaptureAsync(inputArgs, options, outputDirectory, warmupSeconds, cancellationToken);
        }, cancellationToken).ConfigureAwait(false);

        return new SceneCaptureResult(
            device.Name, options.Format, characteristics.Width, characteristics.Height, outputDirectory, frames);
    }

    private static Task DelayStartAsync(double seconds, CancellationToken cancellationToken) =>
        seconds > 0 ? Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken) : Task.CompletedTask;

    public async Task<ResolvedCaptureInput> ResolveInputAsync(
        string? deviceId, int? width, int? height, int fps, CancellationToken cancellationToken)
    {
        var devices = await EnumerateAsync(cancellationToken).ConfigureAwait(false);
        var device = ResolveDevice(devices, deviceId);
        var index = FormatSelector.SelectIndex(device.SelectionFormats, width, height, fps > 0 ? fps : null);
        var characteristics = device.Characteristics[index];
        var effectiveFps = fps > 0 ? fps : FrameRate(characteristics);

        var inputArgs = PlatformDeviceMapper.BuildVideoInput(
            device.Platform, device.VideoTarget, characteristics.Width, characteristics.Height, effectiveFps);

        return new ResolvedCaptureInput(device.Name, device.LockKey, inputArgs, characteristics.Width, characteristics.Height);
    }

    public async Task<IAsyncDisposable> AcquireDeviceLockAsync(string lockKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockKey);
        var gate = _deviceLocks.GetOrAdd(lockKey, _ => new Lazy<SemaphoreSlim>(() => new SemaphoreSlim(1, 1))).Value;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new DeviceLockReleaser(gate);
    }

    private sealed class DeviceLockReleaser(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Warmup window in seconds: the still warmup-frame count converted via the device frame rate.</summary>
    private double WarmupSeconds(int frameRate)
    {
        var fps = frameRate > 0 ? frameRate : 30;
        return Math.Round((double)_options.ImageWarmupFrames / fps, 2);
    }

    private string ResolveSceneDirectory(SceneCaptureOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.OutputDirectory))
        {
            return Path.GetFullPath(options.OutputDirectory);
        }

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        return Path.GetFullPath(Path.Combine(_options.OutputDirectory, $"scene-{timestamp}"));
    }

    private string ResolveOutputPath(VideoCaptureOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.OutputPath))
        {
            return Path.GetFullPath(options.OutputPath);
        }

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var fileName = $"capture-{timestamp}.{options.Container.FileExtension}";
        return Path.GetFullPath(Path.Combine(_options.OutputDirectory, fileName));
    }

    private async Task<T> WithDeviceLockAsync<T>(ResolvedCameraDevice device, Func<Task<T>> action, CancellationToken cancellationToken)
    {
        var gate = _deviceLocks.GetOrAdd(device.LockKey, _ => new Lazy<SemaphoreSlim>(() => new SemaphoreSlim(1, 1))).Value;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static ResolvedCameraDevice ResolveDevice(IReadOnlyList<ResolvedCameraDevice> devices, string? deviceId)
    {
        if (devices.Count == 0)
        {
            throw new CaptureFailedException("No cameras were found on this host.");
        }

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return devices[0];
        }

        var id = deviceId.Trim();

        var match =
            devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)) ??
            devices.FirstOrDefault(d => string.Equals(d.Name, id, StringComparison.OrdinalIgnoreCase)) ??
            devices.FirstOrDefault(d => string.Equals(d.VideoTarget, id, StringComparison.Ordinal));

        if (match is null && int.TryParse(id, out var numericIndex) && numericIndex >= 0 && numericIndex < devices.Count)
        {
            match = devices[numericIndex];
        }

        return match ?? throw new CaptureValidationException(
            $"No camera matches '{deviceId}'. Available ids: {string.Join(", ", devices.Select(d => d.Id))}.");
    }

    private Task<IReadOnlyList<ResolvedCameraDevice>> EnumerateAsync(CancellationToken cancellationToken)
    {
        // FlashCap enumeration is synchronous and touches hardware; keep it off the dispatcher thread.
        return Task.Run<IReadOnlyList<ResolvedCameraDevice>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var descriptors = new CaptureDevices().EnumerateDescriptors()
                .Where(d => d.Characteristics.Length > 0)
                .OrderBy(d => FlashCapDeviceMapping.Priority(d.DeviceType))
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var resolved = new List<ResolvedCameraDevice>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var descriptor in descriptors)
            {
                var platform = FlashCapDeviceMapping.ToPlatform(descriptor.DeviceType);
                var videoTarget = FlashCapDeviceMapping.ToVideoTarget(platform, descriptor.Name, descriptor.Identity);
                var lockKey = $"{platform}:{videoTarget}";

                // Skip the same physical device surfaced by a second (lower-priority) backend.
                if (!seen.Add(lockKey))
                {
                    continue;
                }

                var characteristics = descriptor.Characteristics;
                var selectionFormats = characteristics.Select(ToCaptureFormat).ToList();

                resolved.Add(new ResolvedCameraDevice
                {
                    Id = $"cam{resolved.Count}",
                    Name = descriptor.Name,
                    Platform = platform,
                    VideoTarget = videoTarget,
                    LockKey = lockKey,
                    Descriptor = descriptor,
                    Characteristics = characteristics,
                    SelectionFormats = selectionFormats,
                    DisplayFormats = BuildDisplayFormats(selectionFormats),
                });
            }

            return resolved;
        }, cancellationToken);
    }

    /// <summary>Collapses per-fps duplicates to one entry per (resolution, pixel format), keeping the highest fps.</summary>
    private static IReadOnlyList<CaptureFormat> BuildDisplayFormats(IEnumerable<CaptureFormat> formats) =>
        formats
            .GroupBy(f => (f.Width, f.Height, f.PixelFormat))
            .Select(g => g.OrderByDescending(f => f.FramesPerSecond).First())
            .OrderByDescending(f => (long)f.Width * f.Height)
            .ThenByDescending(f => f.FramesPerSecond)
            .ToList();

    private static CaptureFormat ToCaptureFormat(VideoCharacteristics characteristics)
    {
        var fraction = characteristics.FramesPerSecond;
        var fps = fraction.Denominator != 0 ? (double)fraction.Numerator / fraction.Denominator : 0d;

        return new CaptureFormat(
            characteristics.Width, characteristics.Height, FriendlyPixelFormat(characteristics), Math.Round(fps, 2));
    }

    /// <summary>The requested input frame rate for a mode, rounded to a whole number (0 when unknown).</summary>
    private static int FrameRate(VideoCharacteristics characteristics)
    {
        var fraction = characteristics.FramesPerSecond;
        if (fraction.Denominator == 0)
        {
            return 0;
        }

        var fps = (double)fraction.Numerator / fraction.Denominator;
        return fps <= 0 ? 0 : (int)Math.Round(fps, MidpointRounding.AwayFromZero);
    }

    // DirectShow media-subtype GUIDs encode a FOURCC in the first 4 bytes, e.g.
    // 32595559-0000-0010-8000-00aa00389b71 == "YUY2". Recover the FOURCC for a friendlier label.
    private const string MediaSubtypeSuffix = "-0000-0010-8000-00aa00389b71";

    private static string FriendlyPixelFormat(VideoCharacteristics characteristics)
    {
        var raw = characteristics.RawPixelFormat;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var fourcc = TryFourccFromMediaSubtype(raw);
            if (fourcc is not null)
            {
                return fourcc;
            }

            // Already a friendly token (e.g. MJPG / YUYV) rather than a GUID — keep it.
            if (!Guid.TryParse(raw, out _))
            {
                return raw;
            }
        }

        return characteristics.PixelFormat.ToString();
    }

    internal static string? TryFourccFromMediaSubtype(string raw)
    {
        if (raw.Length != 36 || !raw.EndsWith(MediaSubtypeSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!uint.TryParse(raw.AsSpan(0, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var data1))
        {
            return null;
        }

        Span<char> fourcc = stackalloc char[4];
        for (var i = 0; i < 4; i++)
        {
            var b = (byte)((data1 >> (8 * i)) & 0xFF);
            if (b is < 0x20 or > 0x7E)
            {
                return null; // not printable ASCII: not a FOURCC subtype.
            }

            fourcc[i] = (char)b;
        }

        // Trailing spaces are significant in space-padded FOURCCs (e.g. "Y8  ", "RGB "), so keep them.
        // NUL padding (0x00) already fails the printability check above and returns null.
        return new string(fourcc);
    }

    public void Dispose()
    {
        foreach (var lazy in _deviceLocks.Values)
        {
            if (lazy.IsValueCreated)
            {
                lazy.Value.Dispose();
            }
        }
    }

    /// <summary>Internal device record holding both the display view and the native capture handles.</summary>
    private sealed class ResolvedCameraDevice
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required CapturePlatform Platform { get; init; }
        public required string VideoTarget { get; init; }
        public required string LockKey { get; init; }
        public required CaptureDeviceDescriptor Descriptor { get; init; }
        public required IReadOnlyList<VideoCharacteristics> Characteristics { get; init; }
        public required IReadOnlyList<CaptureFormat> SelectionFormats { get; init; }
        public required IReadOnlyList<CaptureFormat> DisplayFormats { get; init; }
    }
}
