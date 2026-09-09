namespace MooreHotels.Application.Interfaces;

public interface IEmailDeliveryContext
{
    Guid? IdempotencyKey { get; set; }
}
