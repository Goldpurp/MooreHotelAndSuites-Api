namespace MooreHotels.Application.DTOs;

public class EmailSettings
{
    public string DeliveryMode { get; set; } = "Brevo";
    public string ApiPass { get; set; } = string.Empty;
    public string SenderEmail { get; set; } = string.Empty;
    public string SenderName { get; set; } = "Moore Hotels & Suites";
    public string AdminNotificationEmail { get; set; } = string.Empty;
    public string LogoUrl { get; set; } =
        "https://res.cloudinary.com/dxryndnhl/image/upload/v1777386016/slazzer-preview-ofc3f_uvulyz.png";
    public int MaxRetryAttempts { get; set; } = 3;
}
