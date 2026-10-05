namespace MooreHotels.Infrastructure.Persistence;

internal static class SqlLikePattern
{
    public const string EscapeCharacter = "!";

    // User-provided references and search terms must remain literal SQL patterns.
    public static string Literal(string value) => value
        .Replace("!", "!!", StringComparison.Ordinal)
        .Replace("%", "!%", StringComparison.Ordinal)
        .Replace("_", "!_", StringComparison.Ordinal);
}
