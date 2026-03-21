using FluentAssertions;
using SyncIt.Core.Services;

namespace SyncIt.Tests.Services;

public class DriveFolderCacheTests
{
    [Fact]
    public void Set_AddsEntry()
    {
        var cache = new DriveFolderCache();
        cache.Set("folder1", "MyFolder", "root");
        cache.Count.Should().Be(1);
    }

    [Fact]
    public void TryGetRelativePath_DirectChild_ReturnsFileName()
    {
        var cache = new DriveFolderCache();
        var result = cache.TryGetRelativePath("file1", "test.txt", "root", "root");
        result.Should().Be("test.txt");
    }

    [Fact]
    public void TryGetRelativePath_NestedFolder_ReturnsFullPath()
    {
        var cache = new DriveFolderCache();
        cache.Set("folder1", "Documents", "root");
        cache.Set("folder2", "Work", "folder1");

        var result = cache.TryGetRelativePath("file1", "report.pdf", "root", "folder2");
        result.Should().Be("Documents/Work/report.pdf");
    }

    [Fact]
    public void TryGetRelativePath_CacheMiss_ReturnsNull()
    {
        var cache = new DriveFolderCache();
        // parent "unknownFolder" is not cached
        var result = cache.TryGetRelativePath("file1", "test.txt", "root", "unknownFolder");
        result.Should().BeNull();
    }

    [Fact]
    public void Remove_DeletesEntry()
    {
        var cache = new DriveFolderCache();
        cache.Set("folder1", "MyFolder", "root");
        cache.Remove("folder1");
        cache.Count.Should().Be(0);
    }

    [Fact]
    public void Invalidate_ClearsAll()
    {
        var cache = new DriveFolderCache();
        cache.Set("f1", "A", "root");
        cache.Set("f2", "B", "root");
        cache.Invalidate();
        cache.Count.Should().Be(0);
    }

    [Fact]
    public void Set_OverwritesExisting()
    {
        var cache = new DriveFolderCache();
        cache.Set("folder1", "OldName", "root");
        cache.Set("folder1", "NewName", "root");
        cache.Count.Should().Be(1);

        var result = cache.TryGetRelativePath("file1", "test.txt", "root", "folder1");
        result.Should().Be("NewName/test.txt");
    }
}
