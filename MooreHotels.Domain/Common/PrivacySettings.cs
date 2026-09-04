namespace MooreHotels.Domain.Common;

public sealed class PrivacySettings
{
    public bool RequirePolicyAcceptance { get; init; }
    public string CurrentPrivacyPolicyVersion { get; init; } = string.Empty;
    public string CurrentBookingTermsVersion { get; init; } = string.Empty;
    public string PrivacyPolicyUrl { get; init; } = string.Empty;
    public string BookingTermsUrl { get; init; } = string.Empty;
    public bool EnableRetentionWorker { get; init; }
    public int GuestRetentionDays { get; init; } = 2555;
}
