namespace MooreHotels.Domain.Entities;

public class MonnifyTransaction
{
    public Guid Id { get; set; }
    public Guid? BookingId { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public string TransactionReference { get; set; } = string.Empty; // Merchant paymentReference
    public string? MonnifyReference { get; set; } // Provider transactionReference
    public decimal Amount { get; set; }
    public decimal? Fee { get; set; }
    public decimal? SettledAmount { get; set; }
    public string Status { get; set; } = "PENDING"; // PENDING, PAID, OVERPAID, PARTIALLY_PAID, FAILED, CANCELLED
    public string? PaymentMethod { get; set; } // CARD, ACCOUNT_TRANSFER, etc.
    public string? Source { get; set; }
    public DateTime? VerifiedAtUtc { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public Booking? Booking { get; set; }
}
