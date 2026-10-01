using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Configuration;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Identity;
using Xunit;

namespace MooreHotels.UnitTests.Identity;

public sealed class JwtServiceTests
{
    [Fact]
    public void Default_staff_session_covers_an_eight_hour_shift()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "0123456789abcdef0123456789abcdef",
                ["Jwt:Issuer"] = "MooreHotels",
                ["Jwt:Audience"] = "MooreHotels_Clients"
            })
            .Build();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = "staff@example.test",
            UserName = "staff@example.test",
            Name = "Test Staff",
            Role = UserRole.Staff,
            SecurityStamp = Guid.NewGuid().ToString("N")
        };

        var encoded = new JwtService(configuration).GenerateToken(user);
        var token = new JwtSecurityTokenHandler().ReadJwtToken(encoded);

        Assert.InRange(token.ValidTo - token.ValidFrom, TimeSpan.FromMinutes(479), TimeSpan.FromMinutes(481));
    }
}
