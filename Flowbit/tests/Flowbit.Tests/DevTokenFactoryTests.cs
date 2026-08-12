extern alias FlowbitUi;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using DevTokenClaim = FlowbitUi::Flowbit.Ui.Auth.DevTokenClaim;
using DevTokenClaimRules = FlowbitUi::Flowbit.Ui.Auth.DevTokenClaimRules;
using DevTokenFactory = FlowbitUi::Flowbit.Ui.Auth.DevTokenFactory;
using Xunit;

namespace Flowbit.Tests;

public sealed class DevTokenFactoryTests
{
    [Fact]
    public void CreateAddsNormalizedCustomStringClaimsAlongsideIdentityAndRoles()
    {
        var raw = CreateFactory().Create(
            " sherif ",
            ["Manager", "manager"],
            60,
            [new DevTokenClaim(" depId ", " 10 "), new DevTokenClaim("department", "Finance")]);

        var token = new JwtSecurityTokenHandler().ReadJwtToken(raw);

        Assert.Contains(token.Claims, claim => claim.Type == JwtRegisteredClaimNames.Sub && claim.Value == "sherif");
        Assert.Single(token.Claims, claim => claim.Value == "Manager");
        Assert.Equal("10", token.Claims.Single(claim => claim.Type == "depId").Value);
        Assert.Equal("Finance", token.Claims.Single(claim => claim.Type == "department").Value);
        Assert.All(
            token.Claims.Where(claim => claim.Type is "depId" or "department"),
            claim => Assert.Equal(ClaimValueTypes.String, claim.ValueType));
    }

    [Fact]
    public void NormalizeRejectsCaseInsensitiveDuplicateNames()
    {
        var error = Assert.Throws<ArgumentException>(() => DevTokenClaimRules.Normalize(
            [new DevTokenClaim("depId", "10"), new DevTokenClaim("DEPID", "20")]));

        Assert.Contains("duplicated", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("sub")]
    [InlineData("role")]
    [InlineData("exp")]
    [InlineData("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name")]
    public void NormalizeRejectsClaimsManagedByTheTokenFactory(string name)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            DevTokenClaimRules.Normalize([new DevTokenClaim(name, "value")]));

        Assert.Contains("managed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeAllowsCustomIdentityClaimsAndEmptyStringValues()
    {
        var claims = DevTokenClaimRules.Normalize(
            [new DevTokenClaim(" preferred_username ", " sherif "), new DevTokenClaim("region", " ")]);

        Assert.Equal(
            [new DevTokenClaim("preferred_username", "sherif"), new DevTokenClaim("region", string.Empty)],
            claims);
    }

    private static DevTokenFactory CreateFactory()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = "flowbit-tests",
                ["Jwt:Audience"] = "flowbit-api-tests",
                ["Jwt:Key"] = "flowbit-tests-signing-key-at-least-32-bytes-long"
            })
            .Build();
        return new DevTokenFactory(configuration, NullLogger<DevTokenFactory>.Instance);
    }
}
