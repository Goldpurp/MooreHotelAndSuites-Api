using MooreHotels.Application.Interfaces;

namespace MooreHotels.Infrastructure.Services;

public sealed class EmailDeliveryContext : IEmailDeliveryContext
{
    public Guid? IdempotencyKey { get; set; }
}
