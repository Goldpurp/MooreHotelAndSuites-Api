using System.ComponentModel.DataAnnotations;

namespace MooreHotels.Application.DTOs;

public sealed class ConfirmTransferRequest
{
    /// <summary>
    /// Must be exactly <c>ACCEPT</c>. Matching is case-sensitive and whitespace is not removed.
    /// </summary>
    [Required(ErrorMessage = "confirmationText is required and must be exactly 'ACCEPT'.")]
    [StringLength(6, MinimumLength = 6,
        ErrorMessage = "confirmationText must be exactly 'ACCEPT' with no surrounding whitespace.")]
    public string? ConfirmationText { get; init; }

    /// <summary>
    /// Identifies the dashboard acknowledgement UI. The server always stores
    /// <c>TypedAcknowledgement</c> and does not trust this value as audit data.
    /// </summary>
    [StringLength(50)]
    public string? ConfirmationMethod { get; init; }

    /// <summary>
    /// Deprecated compatibility field. The value is ignored; the API generates
    /// the real manual-payment reference on the server.
    /// </summary>
    public string? TransactionReference { get; init; }
}
