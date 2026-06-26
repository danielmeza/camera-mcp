using CameraMcp.Server.Configuration;
using CameraMcp.Server.Models;
using CameraMcp.Server.Services;
using Microsoft.Extensions.Options;

namespace CameraMcp.Tests.Services;

public class CaptureStoreTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "camroot");

    [Fact]
    public void IsWithin_accepts_same_and_nested_paths()
    {
        Assert.True(CaptureStore.IsWithin(Root, Root));
        Assert.True(CaptureStore.IsWithin(Root, Path.Combine(Root, "scene-1")));
        Assert.True(CaptureStore.IsWithin(Root, Path.Combine(Root, "scene-1", "frame-001.jpg")));
    }

    [Fact]
    public void IsWithin_rejects_outside_paths()
    {
        Assert.False(CaptureStore.IsWithin(Root, Path.GetTempPath()));      // the parent
        Assert.False(CaptureStore.IsWithin(Root, Root + "-sibling"));        // prefix but not nested
        Assert.False(CaptureStore.IsWithin(Root, Path.Combine(Path.GetTempPath(), "elsewhere")));
    }

    [Fact]
    public void Clear_empties_the_output_directory_but_keeps_it()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "image-1.jpg"), new byte[100]);
            File.WriteAllBytes(Path.Combine(dir, "capture-1.mp4"), new byte[200]);
            var scene = Directory.CreateDirectory(Path.Combine(dir, "scene-1")).FullName;
            File.WriteAllBytes(Path.Combine(scene, "frame-001.jpg"), new byte[50]);
            File.WriteAllBytes(Path.Combine(scene, "frame-002.jpg"), new byte[50]);

            var result = Store(dir).Clear(null);

            Assert.True(Directory.Exists(dir));                       // the root is kept
            Assert.Empty(Directory.GetFileSystemEntries(dir));        // ...but emptied
            Assert.Equal(4, result.FilesDeleted);
            Assert.Equal(1, result.DirectoriesDeleted);              // the scene-1 folder
            Assert.Equal(400, result.BytesFreed);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void Clear_removes_a_named_subdirectory_entirely()
    {
        var dir = NewTempDir();
        try
        {
            var scene = Directory.CreateDirectory(Path.Combine(dir, "scene-7")).FullName;
            File.WriteAllBytes(Path.Combine(scene, "frame-001.jpg"), new byte[10]);

            var result = Store(dir).Clear(scene);

            Assert.False(Directory.Exists(scene));
            Assert.True(Directory.Exists(dir)); // the root is untouched
            Assert.Equal(1, result.FilesDeleted);
            Assert.Equal(1, result.DirectoriesDeleted); // the scene-7 folder itself
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void Clear_rejects_a_directory_outside_the_output_root()
    {
        var dir = NewTempDir();
        try
        {
            Assert.Throws<CaptureValidationException>(() => Store(dir).Clear(Path.GetTempPath()));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void Clear_returns_zero_for_a_missing_directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"camroot-missing-{Guid.NewGuid():N}");
        var result = Store(dir).Clear(null);
        Assert.Equal(0, result.FilesDeleted);
        Assert.Equal(0, result.BytesFreed);
    }

    private static CaptureStore Store(string outputDirectory) =>
        new(Options.Create(new CameraMcpOptions { OutputDirectory = outputDirectory }));

    private static string NewTempDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"camroot-{Guid.NewGuid():N}")).FullName;

    private static void Cleanup(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
