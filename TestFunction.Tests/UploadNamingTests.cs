using TestShared;
using Xunit;

namespace TestFunction.Tests;

public sealed class UploadNamingTests
{
    [Theory]
    [InlineData(12, 2026, 7, 2)]
    [InlineData(1, 2027, null, null)]
    [InlineData(1, 2027, 7, null)]
    [InlineData(1, 2027, null, 2)]
    public void DateAndIdentifiersRoundTrip(int month, int year, int? staffId, int? typeId)
    {
        var location = UploadNaming.Create(new(year, month, 1, 0, 0, 0, TimeSpan.Zero), Guid.NewGuid(), "../../SID999_passport.pdf", staffId, typeId);
        Assert.Equal("uploads", location.ContainerName);
        Assert.StartsWith($"{year:D4}/{month:D2}/", location.BlobName);
        Assert.DoesNotContain("..", location.BlobName);
        Assert.Equal((staffId, typeId), UploadNaming.Parse(location.BlobName));
    }

    [Fact]
    public void ConfiguredContainerUsesUtcDatePath()
    {
        var location = UploadNaming.Create(new(2027, 1, 1, 0, 30, 0, TimeSpan.FromHours(1)),
            Guid.NewGuid(), "certificate.pdf", 7, 2, "staff-uploads");

        Assert.Equal("staff-uploads", location.ContainerName);
        Assert.StartsWith("2026/12/SID7_DTID_2_", location.BlobName);
    }

    [Theory]
    [InlineData("SID7_DTID_2_", 7, 2)]
    [InlineData("SID7_", 7, null)]
    [InlineData("DTID_2_", null, 2)]
    [InlineData("", null, null)]
    public void LegacyMonthOnlyPathsStillParse(string identifiers, int? staffId, int? typeId)
    {
        var name = $"09/{identifiers}{Guid.NewGuid():N}_certificate.pdf";

        Assert.Equal((staffId, typeId), UploadNaming.Parse(name));
    }
}