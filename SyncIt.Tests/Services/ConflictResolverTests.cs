using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SyncIt.Core.Models;
using SyncIt.Core.Services;
using SyncIt.Tests.Helpers;

namespace SyncIt.Tests.Services;

public sealed class ConflictResolverTests
{
    private readonly ConflictResolver _resolver = new(NullLogger<ConflictResolver>.Instance);

    private static ConflictContext MakeContext(
        DateTime? localTime = null,
        DateTime? remoteTime = null,
        string? localHash = null,
        string? remoteHash = null) => new()
    {
        LocalRecord = TestHelpers.CreateFileRecord(
            side: SyncSide.Local,
            contentHash: localHash ?? "local-hash",
            modifiedUtc: localTime ?? DateTime.UtcNow),
        RemoteRecord = TestHelpers.CreateFileRecord(
            side: SyncSide.Remote,
            contentHash: remoteHash ?? "remote-hash",
            modifiedUtc: remoteTime ?? DateTime.UtcNow.AddSeconds(-10)),
        RelativePath = "docs/file.txt",
    };

    [Fact]
    public void Resolve_IdenticalHashes_ReturnsNoConflict()
    {
        var ctx = MakeContext(localHash: "samehash", remoteHash: "samehash");
        var result = _resolver.Resolve(new ConflictPolicyOptions(), ctx);

        result.Outcome.Should().Be(ConflictOutcome.NoConflict);
    }

    [Fact]
    public void Resolve_LastWriteWins_LocalNewer_ReturnsUseLocal()
    {
        var ctx = MakeContext(
            localTime: DateTime.UtcNow,
            remoteTime: DateTime.UtcNow.AddMinutes(-5));

        var result = _resolver.Resolve(
            new ConflictPolicyOptions { Resolution = ConflictResolution.LastWriteWins }, ctx);

        result.Outcome.Should().Be(ConflictOutcome.UseLocal);
    }

    [Fact]
    public void Resolve_LastWriteWins_RemoteNewer_ReturnsUseRemote()
    {
        var ctx = MakeContext(
            localTime: DateTime.UtcNow.AddMinutes(-5),
            remoteTime: DateTime.UtcNow);

        var result = _resolver.Resolve(
            new ConflictPolicyOptions { Resolution = ConflictResolution.LastWriteWins }, ctx);

        result.Outcome.Should().Be(ConflictOutcome.UseRemote);
    }

    [Fact]
    public void Resolve_LastWriteWins_WithinTolerance_PrefersLocal()
    {
        var now = DateTime.UtcNow;
        var ctx = MakeContext(localTime: now, remoteTime: now.AddSeconds(1));

        var result = _resolver.Resolve(
            new ConflictPolicyOptions { Resolution = ConflictResolution.LastWriteWins }, ctx);

        result.Outcome.Should().Be(ConflictOutcome.UseLocal);
        result.ReasonDescription.Should().Contain("tolerance");
    }

    [Fact]
    public void Resolve_PreferLocal_ReturnsUseLocal()
    {
        var ctx = MakeContext(
            localTime: DateTime.UtcNow.AddMinutes(-5),
            remoteTime: DateTime.UtcNow);

        var result = _resolver.Resolve(
            new ConflictPolicyOptions { Resolution = ConflictResolution.PreferLocal }, ctx);

        result.Outcome.Should().Be(ConflictOutcome.UseLocal);
    }

    [Fact]
    public void Resolve_PreferRemote_ReturnsUseRemote()
    {
        var ctx = MakeContext(
            localTime: DateTime.UtcNow,
            remoteTime: DateTime.UtcNow.AddMinutes(-5));

        var result = _resolver.Resolve(
            new ConflictPolicyOptions { Resolution = ConflictResolution.PreferRemote }, ctx);

        result.Outcome.Should().Be(ConflictOutcome.UseRemote);
    }

    [Fact]
    public void Resolve_KeepBoth_ReturnsBothPathsWithSuffix()
    {
        var ctx = MakeContext();
        var result = _resolver.Resolve(
            new ConflictPolicyOptions { Resolution = ConflictResolution.KeepBoth }, ctx);

        result.Outcome.Should().Be(ConflictOutcome.KeepBoth);
        result.KeepBothRemotePath.Should().NotBeNullOrEmpty();
        result.KeepBothRemotePath.Should().Contain("SyncIt conflict");
    }

    [Fact]
    public void Resolve_KeepBoth_DefaultSuffix_ContainsDate()
    {
        var ctx = MakeContext();
        var result = _resolver.Resolve(
            new ConflictPolicyOptions { Resolution = ConflictResolution.KeepBoth }, ctx);

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        result.KeepBothRemotePath.Should().Contain(today);
    }

    [Fact]
    public void Resolve_IdenticalHashes_CaseInsensitive()
    {
        var ctx = MakeContext(localHash: "ABC123", remoteHash: "abc123");
        var result = _resolver.Resolve(new ConflictPolicyOptions(), ctx);

        result.Outcome.Should().Be(ConflictOutcome.NoConflict);
    }
}
