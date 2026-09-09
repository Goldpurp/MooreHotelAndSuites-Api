using System.ComponentModel.DataAnnotations;

namespace MooreHotels.Application.DTOs;

public sealed record ApproveRefundRequest(
    [Required, StringLength(500, MinimumLength = 10)] string Reason,
    [Range(typeof(decimal), "0.01", "9999999999999999")] decimal? Amount = null);

public sealed record CompleteRefundRequest(
    [Required, StringLength(160, MinimumLength = 4)] string TransactionReference,
    [Range(typeof(decimal), "0.01", "9999999999999999")] decimal Amount,
    [Required, RegularExpression("^(BankTransfer|Cash|Monnify)$")] string Channel,
    [Required, RegularExpression("^(BankStatement|ProviderReceipt|CashVoucher)$")]
    string EvidenceType,
    [StringLength(500)] string? Notes = null);
