using FluentAssertions;
using SyncIt.Core.Models;
using SyncIt.Core.Services;
using SyncIt.Tests.Helpers;

namespace SyncIt.Tests.Services;

public sealed class GlobFilterTests
{
    private readonly GlobFilter _filter = new();

    private static SyncPair MakePair(
        List<string>? excludes = null,
        List<string>? includes = null,
        List<string>? selectedSubfolders = null)
    {
        var pair = TestHelpers.CreateTestSyncPair();
        pair.ExcludeGlobs = excludes ?? [];
        pair.IncludeGlobs = includes ?? [];
        pair.SelectedSubfolders = selectedSubfolders ?? [];
        return pair;
    }

    [Fact]
    public void IsExcluded_MatchesExcludeGlob_ReturnsTrue()
    {
        var pair = MakePair(excludes: ["*.tmp"]);
        _filter.IsExcluded("file.tmp", pair).Should().BeTrue();
    }

    [Fact]
    public void IsExcluded_NoExcludeMatch_EmptyInclude_ReturnsFalse()
    {
        var pair = MakePair(excludes: ["*.tmp"]);
        _filter.IsExcluded("file.txt", pair).Should().BeFalse();
    }

    [Fact]
    public void IsExcluded_MatchesIncludeGlob_ReturnsFalse()
    {
        var pair = MakePair(includes: ["*.docx"]);
        _filter.IsExcluded("report.docx", pair).Should().BeFalse();
    }

    [Fact]
    public void IsExcluded_NoIncludeMatch_ReturnsTrue()
    {
        var pair = MakePair(includes: ["*.docx"]);
        _filter.IsExcluded("photo.jpg", pair).Should().BeTrue();
    }

    [Fact]
    public void IsExcluded_ExcludeWinsOverInclude()
    {
        var pair = MakePair(excludes: ["*.tmp"], includes: ["*"]);
        _filter.IsExcluded("data.tmp", pair).Should().BeTrue();
    }

    [Fact]
    public void IsExcluded_BackslashNormalization_Works()
    {
        var pair = MakePair(excludes: ["node_modules/**"]);
        _filter.IsExcluded(@"node_modules\package.json", pair).Should().BeTrue();
    }

    [Fact]
    public void IsExcluded_SelectedSubfolders_ExcludesOutsideFolders()
    {
        var pair = MakePair(selectedSubfolders: ["Projects"]);
        _filter.IsExcluded("Finance/report.docx", pair).Should().BeTrue();
    }

    [Fact]
    public void IsExcluded_SelectedSubfolders_IncludesSelectedFolder()
    {
        var pair = MakePair(selectedSubfolders: ["Projects"]);
        _filter.IsExcluded("Projects/app.cs", pair).Should().BeFalse();
    }

    [Fact]
    public void IsExcluded_SelectedSubfolders_ExcludesRootFiles()
    {
        // Root-level files outside selected subfolders should be excluded
        // (Drive root is the base — only selected subfolders are synced)
        var pair = MakePair(selectedSubfolders: ["Projects"]);
        _filter.IsExcluded("readme.txt", pair).Should().BeTrue();
    }

    [Fact]
    public void IsExcluded_SelectedSubfolders_CaseInsensitive()
    {
        var pair = MakePair(selectedSubfolders: ["Projects"]);
        _filter.IsExcluded("projects/app.cs", pair).Should().BeFalse();
    }

    [Fact]
    public void IsIncluded_IsInverseOfIsExcluded()
    {
        var pair = MakePair(excludes: ["*.tmp"]);
        _filter.IsIncluded("file.tmp", pair).Should().BeFalse();
        _filter.IsIncluded("file.txt", pair).Should().BeTrue();
    }

    [Fact]
    public void InvalidateCache_ClearsCompiledGlobs()
    {
        var pair = MakePair(excludes: ["*.bak"]);

        // Prime the cache
        _filter.IsExcluded("file.bak", pair).Should().BeTrue();

        // Change the pair's globs and invalidate
        pair.ExcludeGlobs = ["*.log"];
        _filter.InvalidateCache(pair.Id);

        // Now .bak should NOT be excluded (new globs applied)
        _filter.IsExcluded("file.bak", pair).Should().BeFalse();
        _filter.IsExcluded("file.log", pair).Should().BeTrue();
    }

    [Fact]
    public void IsExcluded_WildcardSubfolder_MatchesDeepPaths()
    {
        var pair = MakePair(excludes: [".git/**"]);
        _filter.IsExcluded(".git/objects/abc123", pair).Should().BeTrue();
    }

    [Fact]
    public void IsExcluded_EmptyGlobs_IncludesEverything()
    {
        var pair = MakePair();
        _filter.IsExcluded("anything.xyz", pair).Should().BeFalse();
    }
}
