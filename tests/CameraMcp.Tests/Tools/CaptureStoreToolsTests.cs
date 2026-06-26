using CameraMcp.Server.Models;
using CameraMcp.Server.Services;
using CameraMcp.Server.Tools;
using ModelContextProtocol;

namespace CameraMcp.Tests.Tools;

public class CaptureStoreToolsTests
{
    [Fact]
    public void ClearCaptures_returns_summary_json()
    {
        var store = new StubStore { Result = new ClearResult(@"C:\captures", 12, 3, 4096) };
        var tools = new CaptureStoreTools(store);

        var json = tools.ClearCaptures();

        Assert.Contains("\"filesDeleted\": 12", json);
        Assert.Contains("\"directoriesDeleted\": 3", json);
        Assert.Contains("\"bytesFreed\": 4096", json);
    }

    [Fact]
    public void ClearCaptures_passes_directory_through()
    {
        var store = new StubStore { Result = new ClearResult(@"C:\captures\scene-1", 2, 1, 100) };
        var tools = new CaptureStoreTools(store);

        tools.ClearCaptures(@"C:\captures\scene-1");

        Assert.Equal(@"C:\captures\scene-1", store.LastDirectory);
    }

    [Fact]
    public void ClearCaptures_translates_validation_error_to_McpException()
    {
        var store = new StubStore { Exception = new CaptureValidationException("outside output dir") };
        var tools = new CaptureStoreTools(store);

        var ex = Assert.Throws<McpException>(() => tools.ClearCaptures(@"C:\Windows"));
        Assert.Equal("outside output dir", ex.Message);
    }

    private sealed class StubStore : ICaptureStore
    {
        public ClearResult? Result { get; init; }
        public Exception? Exception { get; init; }
        public string? LastDirectory { get; private set; }

        public ClearResult Clear(string? directory)
        {
            LastDirectory = directory;
            if (Exception is not null)
            {
                throw Exception;
            }

            return Result ?? throw new InvalidOperationException("no stub result configured");
        }
    }
}
