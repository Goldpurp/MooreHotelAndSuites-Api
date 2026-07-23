namespace MooreHotels.Domain.Common;

public static class MonnifyPaymentPolicy
{
    // Monnify's hosted checkout and transfer account are documented as valid
    // for a maximum of 2,400 seconds.
    public static readonly TimeSpan HostedCheckoutWindow =
        TimeSpan.FromMinutes(40);
}
