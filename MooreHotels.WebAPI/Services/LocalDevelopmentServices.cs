using MooreHotels.Application.DTOs;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces;
using MooreHotels.Application.Interfaces.Services;

namespace MooreHotels.WebAPI.Services;

/// <summary>
/// Local development transport: confirms that an email would be sent without
/// transmitting guest data or credentials to an external provider.
/// </summary>
public sealed class LocalEmailService : IEmailService
{
    private readonly ILogger<LocalEmailService> _logger;
    private readonly string _mailboxRoot;

    public LocalEmailService(
        ILogger<LocalEmailService> logger,
        IWebHostEnvironment environment)
    {
        _logger = logger;
        var solutionRoot = Directory.GetParent(environment.ContentRootPath)?.FullName
            ?? environment.ContentRootPath;
        _mailboxRoot = Path.Combine(solutionRoot, ".local", "mailbox");
    }

    private Task Accepted(string template, string recipient)
    {
        _logger.LogInformation(
            "Local email accepted. Template={Template}; RecipientDomain={RecipientDomain}",
            template,
            recipient.Split('@').LastOrDefault() ?? "invalid");
        return Task.CompletedTask;
    }

    private async Task AcceptedWithLink(string template, string recipient, string link)
    {
        Directory.CreateDirectory(_mailboxRoot);
        var fileName = $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{template}-{Guid.NewGuid():N}.txt";
        var path = Path.Combine(_mailboxRoot, fileName);
        await File.WriteAllTextAsync(
            path,
            $"Template: {template}{Environment.NewLine}" +
            $"Recipient: {recipient}{Environment.NewLine}" +
            $"Link: {link}{Environment.NewLine}");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        await Accepted(template, recipient);
    }

    public Task SendBookingConfirmationAsync(string email, string guestName, string bookingCode, string roomName, string roomCategory, int capacity, DateTime checkIn, DateTime checkOut, int nights, decimal totalAmount) => Accepted("BookingConfirmation", email);
    public Task SendCancellationNoticeAsync(string email, string guestName, string bookingCode, string roomName, string roomCategory, DateTime checkIn, string? reason = null) => Accepted("Cancellation", email);
    public Task SendCheckInReminderAsync(string email, string guestName, string bookingCode, string roomName, DateTime checkIn) => Accepted("CheckInReminder", email);
    public Task SendEmailVerificationAsync(string email, string name, string link) => AcceptedWithLink("EmailVerification", email, link);
    public Task SendPasswordResetAsync(string email, string name, string link) => AcceptedWithLink("PasswordReset", email, link);
    public Task SendPaymentSuccessAsync(string email, string guestName, string bookingCode, string roomName, decimal amount, string reference) => Accepted("PaymentSuccess", email);
    public Task SendCheckOutThankYouAsync(string email, string guestName, string bookingCode, string roomName) => Accepted("CheckOutThankYou", email);
    public Task SendAdminNewBookingAlertAsync(string adminEmail, string guestName, string bookingCode, string roomName, string roomCategory, int capacity, DateTime checkIn, DateTime checkOut, int nights, decimal totalAmount, string guestEmail, string guestPhone) => Accepted("AdminNewBooking", adminEmail);
    public Task SendStaffWelcomeEmailAsync(string email, string name, string setupLink, string role) => AcceptedWithLink("StaffWelcome", email, setupLink);
    public Task SendAccountSuspendedAsync(string email, string name) => Accepted("AccountSuspended", email);
    public Task SendAccountActivatedAsync(string email, string name) => Accepted("AccountActivated", email);
    public Task SendRefundCompletionNoticeAsync(string email, string guestName, string bookingCode, string roomName, decimal amount, string reference) => Accepted("RefundCompleted", email);
    public Task SendAdminRefundAlertAsync(string adminEmail, string guestName, string bookingCode, string roomName, decimal amount) => Accepted("AdminRefund", adminEmail);
}

public sealed class UnavailableMonnifyService : IMonnifyService
{
    public Task<MonnifyInitializationResult> InitializeMonnifyPaymentAsync(
        string email,
        string name,
        decimal amount,
        Guid bookingId,
        string bookingCode,
        string paymentReference,
        string callbackUrl,
        CancellationToken cancellationToken = default) =>
        throw new BadRequestException(
            "Online payment is disabled in the Local profile. Use direct transfer for local testing.");

    public Task<MonnifyVerificationResult?> VerifyTransactionAsync(
        string paymentReference,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<MonnifyVerificationResult?>(null);

    public Task<string> GetAccessTokenAsync(
        CancellationToken cancellationToken = default) =>
        throw new BadRequestException("Monnify is disabled in the Local profile.");
}

public sealed class LocalImageService : IImageService
{
    private static readonly HashSet<string> AllowedFolders =
        new(StringComparer.OrdinalIgnoreCase) { "rooms", "website-assets", "avatars", "general" };

    private readonly string _uploadRoot;
    private readonly string _publicBaseUrl;

    public LocalImageService(IWebHostEnvironment environment, IConfiguration configuration)
    {
        _uploadRoot = Path.Combine(environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"), "uploads");
        _publicBaseUrl = (configuration["Api:PublicBaseUrl"] ?? "http://localhost:5222").TrimEnd('/');
    }

    public async Task<ImageUploadResult?> UploadImageAsync(IFormFile file, string folder = "general")
    {
        if (file.Length == 0)
        {
            return null;
        }

        folder = AllowedFolders.Contains(folder) ? folder.ToLowerInvariant() : "general";
        var extension = file.ContentType.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/avif" => ".avif",
            _ => throw new BadRequestException("Unsupported image type.")
        };
        var fileName = $"{Guid.NewGuid():N}{extension}";
        var folderPath = Path.Combine(_uploadRoot, folder);
        Directory.CreateDirectory(folderPath);

        var target = Path.Combine(folderPath, fileName);
        await using (var stream = new FileStream(
                         target,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         64 * 1024,
                         FileOptions.Asynchronous))
        {
            await file.CopyToAsync(stream);
        }

        var publicId = $"local/{folder}/{fileName}";
        return new ImageUploadResult(publicId, $"{_publicBaseUrl}/uploads/{folder}/{fileName}");
    }

    public async Task<List<ImageUploadResult>> UploadMultipleAsync(List<IFormFile> files, string folder = "rooms")
    {
        var results = new List<ImageUploadResult>(files.Count);
        foreach (var file in files)
        {
            var result = await UploadImageAsync(file, folder);
            if (result is not null)
            {
                results.Add(result);
            }
        }

        return results;
    }

    public Task<bool> DeleteImageAsync(string publicId)
    {
        if (!publicId.StartsWith("local/", StringComparison.Ordinal))
        {
            return Task.FromResult(false);
        }

        var relativePath = publicId["local/".Length..].Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_uploadRoot, relativePath));
        var root = Path.GetFullPath(_uploadRoot) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.Ordinal) || !File.Exists(fullPath))
        {
            return Task.FromResult(false);
        }

        File.Delete(fullPath);
        return Task.FromResult(true);
    }
}
