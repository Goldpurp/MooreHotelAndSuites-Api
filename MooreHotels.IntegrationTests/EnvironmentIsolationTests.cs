using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MooreHotels.Infrastructure.Persistence;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class EnvironmentIsolationTests
{
    private readonly ManualTransferTestFixture _fixture;

    public EnvironmentIsolationTests(ManualTransferTestFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task Local_database_is_bound_to_local_before_requests_are_served()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MooreHotelsDbContext>();

        var boundary = await db.DatabaseEnvironmentBoundaries
            .AsNoTracking()
            .SingleAsync();

        Assert.Equal(1, boundary.Id);
        Assert.Equal("local", boundary.EnvironmentName);
        Assert.True(boundary.BoundAtUtc <= DateTime.UtcNow);
    }

    [Fact]
    public async Task Application_code_cannot_modify_the_database_environment_boundary()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MooreHotelsDbContext>();
        var boundary = await db.DatabaseEnvironmentBoundaries.SingleAsync();
        boundary.EnvironmentName = "production";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.SaveChangesAsync());

        Assert.Contains("environment boundary is immutable", exception.Message);
    }
}
