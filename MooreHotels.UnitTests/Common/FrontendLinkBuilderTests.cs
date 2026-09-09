using FluentAssertions;
using MooreHotels.Application.Common;
using Xunit;

namespace MooreHotels.UnitTests.Common;

public sealed class FrontendLinkBuilderTests
{
    [Fact]
    public void WithFragment_KeepsSensitiveTokensOutOfTheQueryString()
    {
        var link = FrontendLinkBuilder.WithFragment(
            "https://example.test/",
            "/reset-password",
            new Dictionary<string, string?>
            {
                ["email"] = "guest+test@example.test",
                ["token"] = "secret token/+="
            });

        link.Should().StartWith("https://example.test/reset-password#");
        link.Should().NotContain("?");
        link.Should().Contain("email=guest%2Btest@example.test");
        link.Should().Contain("token=secret%20token%2F%2B%3D");
    }
}
