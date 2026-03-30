using FluentAssertions;
using SyncIt.Core.Helpers;

namespace SyncIt.Tests.Helpers;

public sealed class PathHelperTests
{
    [Fact]
    public void SanitizeRemoteSegment_ReplacesWindowsInvalidCharacters()
    {
        var sanitized = PathHelper.SanitizeRemoteSegment("CONF: ref 12056/614944 [bkk/en]?*.txt");

        sanitized.Should().Be("CONF\uFF1A ref 12056\uFF0F614944 [bkk\uFF0Fen]\uFF1F\uFF0A.txt");
    }

    [Fact]
    public void SanitizeRemoteSegment_ProtectsReservedNamesAndTrailingDots()
    {
        PathHelper.SanitizeRemoteSegment("CON.").Should().Be("CON_");
        PathHelper.SanitizeRemoteSegment("report. ").Should().Be("report");
        PathHelper.SanitizeRemoteSegment(".").Should().Be("_");
    }

    [Fact]
    public void CombineRemoteRelativePath_SanitizesEachSegmentBeforeJoining()
    {
        var combined = PathHelper.CombineRemoteRelativePath("Trips", "Japan/Plan:Final?.docx");

        combined.Should().Be("Trips/Japan\uFF0FPlan\uFF1AFinal\uFF1F.docx");
    }
}
