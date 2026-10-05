using System.Net;
using System.Text;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class PublicInputErrorTests(ManualTransferTestFixture fixture)
{
    [Theory]
    [InlineData("{\"roomId\":\"not-a-guid\"}")]
    [InlineData("{\"adultCount\":\"three\"}")]
    [InlineData("{\"paymentMethod\":\"invalid-method\"}")]
    [InlineData("{\"roomId\":")]
    public async Task Invalid_public_json_returns_field_errors_without_internal_types(string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/bookings");
        request.Headers.Add("X-Moore-App-Environment", "local");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("errors", content);
        Assert.DoesNotContain("MooreHotels.", content);
        Assert.DoesNotContain("System.", content);
        Assert.DoesNotContain("CreateBookingRequest", content);
        Assert.DoesNotContain("BytePositionInLine", content);
    }
}
