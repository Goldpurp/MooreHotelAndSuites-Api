namespace MooreHotels.Domain.Common;

public sealed class MonnifySettings
{
    public bool Enabled { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string ContractCode { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.monnify.com";
    public bool EnforceWebhookIpAllowlist { get; set; } = true;
    public string[] AllowedWebhookIpAddresses { get; set; } = ["35.242.133.146"];
}
