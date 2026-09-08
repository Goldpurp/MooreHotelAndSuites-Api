namespace MooreHotels.Domain.Entities;

/// <summary>
/// Permanently binds one physical database to a single application environment.
/// </summary>
public sealed class DatabaseEnvironmentBoundary
{
    public int Id { get; set; } = 1;
    public string EnvironmentName { get; set; } = string.Empty;
    public DateTime BoundAtUtc { get; set; } = DateTime.UtcNow;
}
