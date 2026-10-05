using System.ComponentModel.DataAnnotations;

namespace MooreHotels.Application.DTOs;

public sealed class ConfirmTransferRequest
{
    [Required, StringLength(120, MinimumLength = 6)]
    public string? BankReference { get; init; }
    public decimal? Amount { get; init; }
    [Required, StringLength(500, MinimumLength = 10)]
    public string? Reason { get; init; }
    /// <summary>
    /// Must be exactly <c>ACCEPT</c>. Matching is case-sensitive and whitespace is not removed.
    /// </summary>
    [Required(ErrorMessage = "confirmationText is required and must be exactly 'ACCEPT'.")]
    [StringLength(6, MinimumLength = 6,
        ErrorMessage = "confirmationText must be exactly 'ACCEPT' with no surrounding whitespace.")]
    public string? ConfirmationText { get; init; }

    /// <summary>
    /// Identifies the dashboard acknowledgement UI. The server always stores
    /// <c>BankStatementReview</c> and does not trust this value as audit data.
    /// </summary>
    [StringLength(50)]
    public string? ConfirmationMethod { get; init; }

    /// <summary>
    /// Deprecated compatibility field, ignored. BankReference supplies bank evidence.
    /// </summary>
    public string? TransactionReference { get; init; }
}
