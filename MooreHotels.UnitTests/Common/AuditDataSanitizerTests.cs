using System.Text.Json;
using FluentAssertions;
using MooreHotels.Application.Common;
using Xunit;

namespace MooreHotels.UnitTests.Common;

public sealed class AuditDataSanitizerTests
{
    [Fact]
    public void SanitizeJson_redacts_nested_pii_secrets_and_free_text()
    {
        var sanitized = AuditDataSanitizer.SanitizeJson(
            """
            {
              "BookingCode": "MH-2026-ABC123",
              "Email": "guest@example.test",
              "Nested": {
                "Reason": "Contains personal incident details",
                "AccessToken": "secret-token",
                "Status": "Confirmed"
              },
              "Items": [{ "GuestName": "Jane Guest", "Amount": 12500 }]
            }
            """);

        using var document = JsonDocument.Parse(sanitized!);
        document.RootElement.GetProperty("BookingCode").GetString()
            .Should().Be("MH-2026-ABC123");
        document.RootElement.GetProperty("Email").GetString()
            .Should().Be(AuditDataSanitizer.RedactedValue);
        document.RootElement.GetProperty("Nested").GetProperty("Reason").GetString()
            .Should().Be(AuditDataSanitizer.RedactedValue);
        document.RootElement.GetProperty("Nested").GetProperty("AccessToken").GetString()
            .Should().Be(AuditDataSanitizer.RedactedValue);
        document.RootElement.GetProperty("Nested").GetProperty("Status").GetString()
            .Should().Be("Confirmed");
        document.RootElement.GetProperty("Items")[0].GetProperty("GuestName").GetString()
            .Should().Be(AuditDataSanitizer.RedactedValue);
        document.RootElement.GetProperty("Items")[0].GetProperty("Amount").GetInt32()
            .Should().Be(12500);
        sanitized.Should().NotContain("guest@example.test");
        sanitized.Should().NotContain("secret-token");
        sanitized.Should().NotContain("Jane Guest");
    }

    [Fact]
    public void SanitizeJson_replaces_invalid_json_instead_of_persisting_it()
    {
        AuditDataSanitizer.SanitizeJson("guest@example.test is not JSON")
            .Should().Be(JsonSerializer.Serialize(AuditDataSanitizer.RedactedValue));
    }

    [Theory]
    [InlineData("\"guest@example.test\"")]
    [InlineData("[\"guest@example.test\"]")]
    [InlineData("42")]
    public void SanitizeJson_replaces_non_object_roots(string json)
    {
        AuditDataSanitizer.SanitizeJson(json)
            .Should().Be(JsonSerializer.Serialize(AuditDataSanitizer.RedactedValue));
    }
}
