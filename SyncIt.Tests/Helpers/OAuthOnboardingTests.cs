using FluentAssertions;
using SyncIt.Core.Helpers;

namespace SyncIt.Tests.Helpers;

public class OAuthClientResolverTests
{
    private const string Id1 = "111111111111-aaaaaaaaaaaaaaaaaaaa.apps.googleusercontent.com";
    private const string Id2 = "222222222222-bbbbbbbbbbbbbbbbbbbb.apps.googleusercontent.com";

    [Fact]
    public void CustomClient_WinsOverBuiltIn()
    {
        var c = OAuthClientResolver.Resolve((Id1, "custom-secret-value"), Id2, "builtin-secret");
        c!.Source.Should().Be(OAuthClientSource.Custom);
        c.ClientId.Should().Be(Id1);
    }

    [Fact]
    public void NoCustom_UsesBuiltIn()
        => OAuthClientResolver.Resolve(null, Id2, "builtin-secret")!.Source.Should().Be(OAuthClientSource.BuiltIn);

    [Fact]
    public void InvalidCustom_FallsBackToBuiltIn()
        => OAuthClientResolver.Resolve(("garbage", "x"), Id2, "builtin-secret")!.Source.Should().Be(OAuthClientSource.BuiltIn);

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(Id2, "")]
    [InlineData("not-an-id", "secret")]
    public void NothingUsable_ReturnsNull(string? id, string? secret)
        => OAuthClientResolver.Resolve(null, id, secret).Should().BeNull();

    [Fact]
    public void ReadBuiltIn_FromAssemblyWithoutMetadata_ReturnsNulls()
    {
        var (id, secret) = OAuthClientResolver.ReadBuiltIn(typeof(OAuthClientResolverTests).Assembly);
        id.Should().BeNull();
        secret.Should().BeNull();
    }
}

public class OAuthErrorClassifierTests
{
    [Theory]
    [InlineData("Error 403: access_denied. SyncIt has reached its user cap", OAuthFailureKind.UserCapReached)]
    [InlineData("Error:\"admin_policy_enforced\"", OAuthFailureKind.BlockedByAdmin)]
    [InlineData("Error:\"org_internal\"", OAuthFailureKind.BlockedByAdmin)]
    [InlineData("Error:\"invalid_client\", Description:\"The OAuth client was not found.\"", OAuthFailureKind.InvalidClient)]
    [InlineData("Error:\"access_denied\"", OAuthFailureKind.AccessDenied)]
    [InlineData("A task was canceled.", OAuthFailureKind.Other)]
    public void ClassifyText_MapsKnownErrors(string text, OAuthFailureKind expected)
        => OAuthErrorClassifier.ClassifyText(text).Should().Be(expected);

    [Fact]
    public void Classify_ReadsTokenResponseException()
    {
        var ex = new Google.Apis.Auth.OAuth2.Responses.TokenResponseException(
            new Google.Apis.Auth.OAuth2.Responses.TokenErrorResponse { Error = "access_denied" });
        OAuthErrorClassifier.Classify(new AggregateException(ex)).Should().Be(OAuthFailureKind.AccessDenied);
    }

    [Theory]
    [InlineData(OAuthFailureKind.UserCapReached, true)]
    [InlineData(OAuthFailureKind.InvalidClient, true)]
    [InlineData(OAuthFailureKind.BlockedByAdmin, false)]
    [InlineData(OAuthFailureKind.Other, false)]
    public void SuggestOwnClient_OnlyWhenItHelps(OAuthFailureKind kind, bool expected)
        => OAuthErrorClassifier.SuggestOwnClient(kind).Should().Be(expected);

    [Fact]
    public void Messages_ArePlainLanguage()
    {
        foreach (var kind in Enum.GetValues<OAuthFailureKind>())
            OAuthErrorClassifier.Message(kind, usingBuiltIn: true).Should().NotBeNullOrWhiteSpace().And.NotContain("Exception");
    }
}

public class ClientSecretFileParserTests
{
    private const string Id = "123456789012-abcdefghijklmnopqrst.apps.googleusercontent.com";

    [Fact]
    public void DesktopClient_Parses()
    {
        var r = ClientSecretFileParser.Parse($$$"""{"installed":{"client_id":"{{{Id}}}","project_id":"syncit-123","client_secret":"GOCSPX-abcdefghijklmnopqrstuv","redirect_uris":["http://localhost"]}}""");
        r.Ok.Should().BeTrue();
        r.ClientId.Should().Be(Id);
        r.ClientSecret.Should().Be("GOCSPX-abcdefghijklmnopqrstuv");
        r.ProjectId.Should().Be("syncit-123");
    }

    [Fact]
    public void WebClient_IsRejectedWithHelpfulMessage()
        => ClientSecretFileParser.Parse($$$"""{"web":{"client_id":"{{{Id}}}","client_secret":"GOCSPX-abcdefghijklmnopqrstuv"}}""").Error.Should().Contain("Desktop app");

    [Fact]
    public void ServiceAccount_IsRejected()
        => ClientSecretFileParser.Parse("""{"type":"service_account","private_key":"x"}""").Error.Should().Contain("service account");

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"installed":{"client_id":"bad","client_secret":"GOCSPX-abcdefghijklmnopqrstuv"}}""")]
    [InlineData("""{"installed":{"client_id":"123456789012-abcdefghijklmnopqrst.apps.googleusercontent.com"}}""")]
    public void InvalidFiles_ReturnError(string json)
        => ClientSecretFileParser.Parse(json).Ok.Should().BeFalse();

    [Fact]
    public void ParseFile_MissingFile_ReturnsError()
        => ClientSecretFileParser.ParseFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")).Ok.Should().BeFalse();
}
