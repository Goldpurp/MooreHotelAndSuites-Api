using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using MooreHotels.Application.DTOs;
using MooreHotels.Domain.Common;
using MooreHotels.Domain.Enums;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class BookingPaginationTests(ManualTransferTestFixture fixture)
{
    [Fact]
    public async Task Pages_search_and_filters_find_all_records_beyond_twenty_with_stable_tie_ordering()
    {
        var prefix = "PAGE" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var timestamp = DateTime.UtcNow.AddDays(-2);
        for (var i = 0; i < 32; i++)
        {
            var booking = await fixture.CreateBookingAsync(createdAtUtc: timestamp,
                bookingStatus: i % 2 == 0 ? BookingStatus.Confirmed : BookingStatus.Pending);
            await fixture.WithDbAsync(async db =>
            {
                (await db.Bookings.SingleAsync(b => b.Id == booking.Id)).BookingCode = prefix + i.ToString("00");
                return await db.SaveChangesAsync();
            });
        }
        var ids = new HashSet<Guid>();
        for (var page = 1; page <= 3; page++)
        {
            var result = await Get($"/api/bookings?page={page}&pageSize=12&search={prefix.ToLowerInvariant()}");
            Assert.Equal(32, result.TotalCount);
            Assert.Equal(3, result.TotalPages);
            Assert.Equal(page == 3 ? 8 : 12, result.Items.Count());
            foreach (var booking in result.Items) Assert.True(ids.Add(booking.Id), "A booking was repeated across pages.");
        }
        Assert.Equal(32, ids.Count);
        var filtered = await Get($"/api/bookings?pageSize=100&search={prefix}&status=Confirmed");
        Assert.Equal(16, filtered.TotalCount);
        Assert.All(filtered.Items, b => Assert.Equal(BookingStatus.Confirmed, b.Status));
        var last = await Get($"/api/bookings?search={prefix.ToLowerInvariant()}31");
        Assert.Equal(prefix + "31", Assert.Single(last.Items).BookingCode);
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("!")]
    public async Task Search_treats_pattern_characters_literally(string character)
    {
        var prefix = "SEARCH" + Guid.NewGuid().ToString("N");
        var matching = await fixture.CreateBookingAsync();
        var other = await fixture.CreateBookingAsync();
        await fixture.WithDbAsync(async db =>
        {
            (await db.Bookings.SingleAsync(b => b.Id == matching.Id)).TransactionReference = prefix + character + "MATCH";
            (await db.Bookings.SingleAsync(b => b.Id == other.Id)).TransactionReference = prefix + "XMATCH";
            return await db.SaveChangesAsync();
        });
        var result = await Get($"/api/bookings?search={Uri.EscapeDataString(prefix + character)}");
        Assert.Equal(matching.Id, Assert.Single(result.Items).Id);
    }

    private async Task<PagedResult<BookingDto>> Get(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Moore-App-Environment", "local");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Admin.Token);
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PagedResult<BookingDto>>(new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }))!;
    }
}
