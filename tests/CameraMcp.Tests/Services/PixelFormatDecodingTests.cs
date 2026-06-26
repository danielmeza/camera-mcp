using CameraMcp.Server.Services;

namespace CameraMcp.Tests.Services;

public class PixelFormatDecodingTests
{
    [Theory]
    [InlineData("32595559-0000-0010-8000-00aa00389b71", "YUY2")]
    [InlineData("47504a4d-0000-0010-8000-00aa00389b71", "MJPG")]
    [InlineData("3231564e-0000-0010-8000-00aa00389b71", "NV12")]
    public void Decodes_media_subtype_guid_to_fourcc(string raw, string expected)
    {
        Assert.Equal(expected, CameraService.TryFourccFromMediaSubtype(raw));
    }

    [Fact]
    public void Preserves_significant_trailing_spaces_in_padded_fourcc()
    {
        // 0x20203859 little-endian -> 'Y','8',' ',' '  ("Y8  ") — the trailing spaces are significant.
        Assert.Equal("Y8  ", CameraService.TryFourccFromMediaSubtype("20203859-0000-0010-8000-00aa00389b71"));
    }

    [Theory]
    [InlineData("773c9ac0-3274-11d0-b724-00aa006c1a01")] // a format-type GUID, not a media subtype
    [InlineData("MJPG")]                                  // already a friendly fourcc, not a GUID
    [InlineData("")]
    public void Returns_null_for_non_media_subtype(string raw)
    {
        Assert.Null(CameraService.TryFourccFromMediaSubtype(raw));
    }
}
