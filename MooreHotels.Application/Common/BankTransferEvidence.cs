using System.Security.Cryptography;
using System.Text;
using MooreHotels.Application.Exceptions;

namespace MooreHotels.Application.Common;

public static class BankTransferEvidence
{
    // This validates evidence and prevents reuse; authenticity is checked by staff against the bank statement.
    public static string Reference(string? value, string bookingCode)
    {
        var reference = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(reference) || reference.Length is < 6 or > 120 ||
            reference.Any(char.IsControl) || reference.StartsWith("MANUAL-", StringComparison.Ordinal) ||
            reference.Equals(bookingCode, StringComparison.OrdinalIgnoreCase))
            throw new BadRequestException("Enter the bank transaction reference from the statement, not a booking or internal confirmation reference.");
        return reference;
    }

    public static void Amount(decimal? amount)
    {
        if (amount is not > 0 || amount > 99999999999999m || decimal.Round(amount.Value, 2) != amount)
            throw new BadRequestException("Enter the exact verified bank credit amount, with at most two decimal places.");
    }

    public static string CreditKey(string reference) =>
        "bank-credit:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reference)));
}
