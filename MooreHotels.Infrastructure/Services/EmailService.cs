using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using MooreHotels.Application.Interfaces;
using MooreHotels.Application.DTOs;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MooreHotels.Application.Exceptions;
using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;

namespace MooreHotels.Infrastructure.Services;

public sealed class EmailService : IEmailService
{
    public const string HttpClientName = "Brevo";
    private const int MaximumProviderResponseBytes = 64 * 1024;

    private readonly EmailSettings _settings;
    private readonly ILogger<EmailService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _publicAppUrl;
    private readonly string _dashboardUrl;

    private const string LogoUrl =
        "https://res.cloudinary.com/dxryndnhl/image/upload/v1777386016/slazzer-preview-ofc3f_uvulyz.png";

    public EmailService(
        IOptions<EmailSettings> settings,
        ILogger<EmailService> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _settings = settings.Value;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _publicAppUrl = (configuration["PublicAppUrl"] ?? "https://moorehotelandsuites.com").TrimEnd('/');
        _dashboardUrl = (configuration["DashboardUrl"] ?? "https://admin.moorehotelandsuites.com").TrimEnd('/');
    }

    private static string E(string? value) => HtmlEncoder.Default.Encode(value ?? string.Empty);

    private async Task SendEmailAsync(
        string toEmail,
        string subject,
        string body)
    {
        if (!MailAddress.TryCreate(toEmail, out var recipient) ||
            string.IsNullOrWhiteSpace(subject) ||
            subject.Length > 200 ||
            string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException(
                "The transactional email request is invalid.");
        }

        var idempotencyKey = Guid.NewGuid();
        var payload = new
        {
            sender = new
            {
                name = _settings.SenderName,
                email = _settings.SenderEmail
            },
            to = new[] { new { email = recipient.Address } },
            subject,
            htmlContent = body,
            headers = new Dictionary<string, string>
            {
                ["idempotencyKey"] = idempotencyKey.ToString()
            },
            tags = new[] { "moore-hotels-transactional" }
        };

        var client = _httpClientFactory.CreateClient(HttpClientName);
        for (var attempt = 1; attempt <= _settings.MaxRetryAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "v3/smtp/email");
            request.Headers.TryAddWithoutValidation(
                "api-key",
                _settings.ApiPass);
            request.Content = JsonContent.Create(payload);

            try
            {
                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead);
                if (response.StatusCode == HttpStatusCode.Created)
                {
                    await ValidateAcceptedResponseAsync(response);
                    _logger.LogInformation(
                        "Brevo accepted transactional email. RecipientDomain={RecipientDomain}; Attempt={Attempt}.",
                        recipient.Host,
                        attempt);
                    return;
                }

                if (!IsTransient(response.StatusCode) ||
                    attempt == _settings.MaxRetryAttempts)
                {
                    _logger.LogError(
                        "Brevo rejected transactional email. StatusCode={StatusCode}; Attempt={Attempt}.",
                        response.StatusCode,
                        attempt);
                    throw DeliveryUnavailable();
                }

                await Task.Delay(GetRetryDelay(response, attempt));
            }
            catch (OperationCanceledException) when (
                attempt < _settings.MaxRetryAttempts)
            {
                await Task.Delay(GetRetryDelay(response: null, attempt));
            }
            catch (OperationCanceledException exception)
            {
                throw DeliveryUnavailable(exception);
            }
            catch (HttpRequestException) when (
                attempt < _settings.MaxRetryAttempts)
            {
                _logger.LogWarning(
                    "Brevo transport failed transiently. Attempt={Attempt}.",
                    attempt);
                await Task.Delay(GetRetryDelay(response: null, attempt));
            }
            catch (HttpRequestException exception)
            {
                throw DeliveryUnavailable(exception);
            }
        }

        throw DeliveryUnavailable();
    }

    private static async Task ValidateAcceptedResponseAsync(
        HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentLength >
            MaximumProviderResponseBytes)
        {
            throw DeliveryUnavailable();
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory())) > 0)
        {
            if (buffer.Length + read > MaximumProviderResponseBytes)
            {
                throw DeliveryUnavailable();
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read));
        }

        buffer.Position = 0;
        try
        {
            using var document = await JsonDocument.ParseAsync(
                buffer,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });
            if (!document.RootElement.TryGetProperty(
                    "messageId",
                    out var messageId) ||
                messageId.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(messageId.GetString()))
            {
                throw DeliveryUnavailable();
            }
        }
        catch (JsonException exception)
        {
            throw DeliveryUnavailable(exception);
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static TimeSpan GetRetryDelay(
        HttpResponseMessage? response,
        int attempt)
    {
        var providerDelay = response?.Headers.RetryAfter?.Delta;
        if (providerDelay.HasValue)
        {
            return providerDelay.Value <= TimeSpan.FromSeconds(5)
                ? providerDelay.Value
                : TimeSpan.FromSeconds(5);
        }

        return TimeSpan.FromMilliseconds(250 * attempt * attempt);
    }

    private static ServiceUnavailableException DeliveryUnavailable(
        Exception? innerException = null) =>
        innerException is null
            ? new ServiceUnavailableException(
                "Transactional email delivery is temporarily unavailable.")
            : new ServiceUnavailableException(
                "Transactional email delivery is temporarily unavailable.",
                innerException);

    private string BuildTemplate(
        string title,
        string content,
        string accentColor = "#B7792A",
        string? preheader = null)
    {
        string optimizedLogo = LogoUrl.Contains("cloudinary.com")
            ? LogoUrl.Replace(
                "/upload/",
                "/upload/e_trim/f_auto,q_auto,w_420/")
            : LogoUrl;
        var safeTitle = E(title);
        var safePreheader = E(preheader ?? title);
        var safeSenderEmail = E(_settings.SenderEmail);
        var safePublicAppUrl = E(_publicAppUrl);
        var currentYear = DateTime.UtcNow.Year;

        return $@"
<!DOCTYPE html>
<html lang='en'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <meta http-equiv='X-UA-Compatible' content='IE=edge'>
    <meta name='color-scheme' content='light'>
    <meta name='supported-color-schemes' content='light'>
    <title>{safeTitle} | Moore Hotels &amp; Suites</title>
    <style>
        html, body {{ margin: 0 !important; padding: 0 !important; width: 100% !important; }}
        table, td {{ border-collapse: collapse !important; }}
        img {{ border: 0; outline: none; text-decoration: none; -ms-interpolation-mode: bicubic; }}
        a {{ color: inherit; }}
        @media only screen and (max-width: 680px) {{
            .email-outer {{ padding: 0 !important; }}
            .email-shell {{ width: 100% !important; border-left: 0 !important; border-right: 0 !important; }}
            .mobile-pad {{ padding-left: 20px !important; padding-right: 20px !important; }}
            .mobile-header {{ padding-top: 14px !important; padding-bottom: 14px !important; }}
            .mobile-hero {{ padding-top: 20px !important; padding-bottom: 20px !important; }}
            .mobile-content {{ padding-top: 28px !important; padding-bottom: 32px !important; }}
            .mobile-footer {{ padding-top: 26px !important; padding-bottom: 24px !important; }}
            .mobile-stack {{ display: block !important; box-sizing: border-box !important; width: 100% !important; text-align: left !important; }}
            .mobile-stack-right {{ display: block !important; box-sizing: border-box !important; width: 100% !important; text-align: left !important; padding-top: 16px !important; }}
            .mobile-button {{ display: block !important; box-sizing: border-box !important; width: 100% !important; padding-left: 18px !important; padding-right: 18px !important; text-align: center !important; }}
            .mobile-break {{ overflow-wrap: anywhere !important; word-break: break-word !important; }}
            .hero-title {{ font-size: 26px !important; line-height: 32px !important; letter-spacing: -0.25px !important; }}
            .brand-logo {{ width: 184px !important; max-width: 184px !important; }}
            .mobile-amount {{ font-size: 21px !important; line-height: 27px !important; }}
        }}
    </style>
    <!--[if mso]>
    <noscript>
        <xml>
            <o:OfficeDocumentSettings xmlns:o='urn:schemas-microsoft-com:office:office'>
                <o:PixelsPerInch>96</o:PixelsPerInch>
            </o:OfficeDocumentSettings>
        </xml>
    </noscript>
    <![endif]-->
</head>
<body style='margin:0; padding:0; background-color:#F3F0EA; -webkit-text-size-adjust:100%; -ms-text-size-adjust:100%;'>
    <div style='display:none; max-height:0; overflow:hidden; opacity:0; color:transparent; line-height:1px; font-size:1px;'>
        {safePreheader}
        &zwnj;&nbsp;&zwnj;&nbsp;&zwnj;&nbsp;&zwnj;&nbsp;&zwnj;&nbsp;&zwnj;&nbsp;
    </div>

    <table role='presentation' width='100%' border='0' cellspacing='0' cellpadding='0' bgcolor='#F3F0EA'>
        <tr>
            <td class='email-outer' align='center' style='padding:28px 12px;'>
                <!--[if mso]>
                <table role='presentation' width='640' border='0' cellspacing='0' cellpadding='0'><tr><td>
                <![endif]-->
                <table class='email-shell' role='article' aria-roledescription='email' aria-label='{safeTitle}' width='640' border='0' cellspacing='0' cellpadding='0' style='width:100%; max-width:640px; background-color:#FFFFFF; border:1px solid #E4DED5;'>
                    <tr>
                        <td align='center' class='mobile-pad mobile-header' style='padding:18px 42px; background-color:#FFFFFF; border-bottom:1px solid #EEE9E2; line-height:0; font-size:0;'>
                            <img class='brand-logo'
                                 src='{optimizedLogo}'
                                 alt='Moore Hotels &amp; Suites'
                                 width='210'
                                 style='display:block; width:210px; max-width:100%; height:auto;' />
                        </td>
                    </tr>
                    <tr>
                        <td class='mobile-pad mobile-hero' style='padding:28px 42px 26px; background-color:#171512; border-bottom:3px solid {accentColor}; font-family:Arial, Helvetica, sans-serif;'>
                            <h1 class='hero-title' style='margin:0; color:#FFFFFF; font-size:32px; font-weight:500; line-height:39px; letter-spacing:-0.4px;'>
                                {safeTitle}
                            </h1>
                        </td>
                    </tr>
                    <tr>
                        <td class='mobile-pad mobile-content' style='padding:36px 42px 40px; background-color:#FFFFFF; color:#393630; font-family:Arial, Helvetica, sans-serif; font-size:16px; line-height:26px;'>
                            {content}
                        </td>
                    </tr>
                    <tr>
                        <td class='mobile-pad mobile-footer' style='padding:28px 42px 26px; background-color:#F8F6F2; border-top:1px solid #E8E2DA; font-family:Arial, Helvetica, sans-serif;'>
                            <table role='presentation' width='100%' border='0' cellspacing='0' cellpadding='0'>
                                <tr>
                                    <td class='mobile-stack' valign='top' style='width:58%; color:#171512; font-size:13px; font-weight:700; line-height:20px; letter-spacing:1.2px; text-transform:uppercase;'>
                                        Moore Hotels &amp; Suites
                                        <div style='margin-top:5px; color:#817A70; font-size:11px; font-weight:400; line-height:18px; letter-spacing:0.5px;'>
                                            Luxury comfort. Considered service.
                                        </div>
                                    </td>
                                    <td class='mobile-stack-right' align='right' valign='top' style='width:42%; color:#615B53; font-size:12px; line-height:20px;'>
                                        <a href='{safePublicAppUrl}' style='color:#615B53; text-decoration:underline;'>Visit our website</a><br/>
                                        <a href='mailto:{safeSenderEmail}' style='color:#615B53; text-decoration:underline;'>Contact guest services</a>
                                    </td>
                                </tr>
                            </table>
                            <div style='margin-top:25px; padding-top:20px; border-top:1px solid #E4DED5; color:#918A80; font-size:10px; line-height:17px;'>
                                This transactional message was sent by Moore Hotels &amp; Suites in connection with an account, reservation or stay. Please do not share booking or security details with anyone you do not trust.<br/>
                                &copy; {currentYear} Moore Hotels &amp; Suites. All rights reserved.
                            </div>
                        </td>
                    </tr>
                </table>
                <!--[if mso]>
                </td></tr></table>
                <![endif]-->
            </td>
        </tr>
    </table>
</body>
</html>";
    }
    
    private string GetBookingSummaryHtml(
        string bookingCode,
        string roomName,
        string roomCategory,
        int? capacity,
        DateTime? checkIn,
        DateTime? checkOut,
        int? nights,
        decimal? amount)
    {
        bookingCode = E(bookingCode);
        roomName = E(roomName);
        roomCategory = E(roomCategory);
        var html = $@"
        <table role='presentation' width='100%' border='0' cellspacing='0' cellpadding='0' style='margin:30px 0; border:1px solid #DDD6CC; background-color:#FFFFFF;'>
            <tr>
                <td colspan='2' style='padding:17px 20px; background-color:#F5F2ED; border-bottom:1px solid #DDD6CC;'>
                    <table role='presentation' width='100%' border='0' cellspacing='0' cellpadding='0'>
                        <tr>
                            <td style='color:#70695F; font-size:10px; font-weight:700; line-height:16px; letter-spacing:1.7px; text-transform:uppercase;'>
                                Reservation details
                            </td>
                            <td class='mobile-break' align='right' style='color:#171512; font-family:Consolas, Monaco, monospace; font-size:13px; font-weight:700; line-height:18px;'>
                                {bookingCode}
                            </td>
                        </tr>
                    </table>
                </td>
            </tr>
            <tr>
                <td class='mobile-stack' width='50%' valign='top' style='padding:20px; border-bottom:1px solid #ECE7E0;'>
                    <div style='color:#918A80; font-size:10px; font-weight:700; line-height:15px; letter-spacing:1.2px; text-transform:uppercase;'>Room</div>
                    <div style='margin-top:5px; color:#26231F; font-size:15px; font-weight:700; line-height:22px;'>{roomName}</div>
                </td>
                <td class='mobile-stack-right' width='50%' valign='top' style='padding:20px; border-bottom:1px solid #ECE7E0;'>
                    <div style='color:#918A80; font-size:10px; font-weight:700; line-height:15px; letter-spacing:1.2px; text-transform:uppercase;'>Category</div>
                    <div style='margin-top:5px; color:#26231F; font-size:15px; font-weight:700; line-height:22px;'>{roomCategory}</div>
                </td>
            </tr>";

        if (checkIn.HasValue)
        {
            var departure = checkOut.HasValue && checkOut.Value > checkIn.Value
                ? $@"
                <td class='mobile-stack-right' width='50%' valign='top' style='padding:20px;'>
                    <div style='color:#918A80; font-size:10px; font-weight:700; line-height:15px; letter-spacing:1.2px; text-transform:uppercase;'>Departure</div>
                    <div style='margin-top:5px; color:#26231F; font-size:15px; font-weight:700; line-height:22px;'>{checkOut.Value:ddd, MMM dd, yyyy}</div>
                    <div style='color:#777067; font-size:12px; line-height:18px;'>Before 12:00 PM</div>
                </td>"
                : string.Empty;
            var arrivalWidth = string.IsNullOrEmpty(departure) ? "100%" : "50%";
            html += $@"
            <tr>
                <td class='mobile-stack' width='{arrivalWidth}' valign='top' style='padding:20px;'>
                    <div style='color:#918A80; font-size:10px; font-weight:700; line-height:15px; letter-spacing:1.2px; text-transform:uppercase;'>Arrival</div>
                    <div style='margin-top:5px; color:#26231F; font-size:15px; font-weight:700; line-height:22px;'>{checkIn.Value:ddd, MMM dd, yyyy}</div>
                    <div style='color:#777067; font-size:12px; line-height:18px;'>From 2:00 PM</div>
                </td>
                {departure}
            </tr>";
        }

        if (capacity is > 0)
        {
            html += $@"
            <tr>
                <td colspan='2' style='padding:14px 20px; background-color:#FAF8F5; border-top:1px solid #ECE7E0; color:#615B53; font-size:12px; line-height:18px;'>
                    Maximum occupancy: <strong style='color:#26231F;'>{capacity.Value} {(capacity.Value == 1 ? "guest" : "guests")}</strong>
                </td>
            </tr>";
        }

        if (nights is > 0 && amount is > 0)
        {
            html += $@"
            <tr>
                <td class='mobile-stack' style='padding:20px; background-color:#171512; color:#FFFFFF;'>
                    <div style='color:#BDB5AA; font-size:10px; font-weight:700; line-height:15px; letter-spacing:1.2px; text-transform:uppercase;'>Stay</div>
                    <div style='margin-top:5px; font-size:14px; font-weight:700; line-height:21px;'>{nights.Value} {(nights.Value == 1 ? "night" : "nights")}</div>
                </td>
                <td class='mobile-stack-right' align='right' style='padding:20px; background-color:#171512; color:#FFFFFF;'>
                    <div style='color:#D7BA7D; font-size:10px; font-weight:700; line-height:15px; letter-spacing:1.2px; text-transform:uppercase;'>Reservation total</div>
                    <div class='mobile-amount' style='margin-top:5px; font-size:20px; font-weight:700; line-height:26px;'>NGN {amount.Value:N2}</div>
                </td>
            </tr>";
        }

        html += @"
        </table>";

        return html;
    }




    // Guest emails
    public async Task SendBookingConfirmationAsync(string email, string guestName, string bookingCode, string roomName, string roomCategory, int capacity, DateTime checkIn, DateTime checkOut, int nights, decimal totalAmount)
    {
        guestName = E(guestName);
        var content = $@"
        <p style='margin-top:0;'>Dear <strong>{guestName}</strong>,</p>
        <p>Your booking request at <strong>Moore Hotels & Suites</strong> has been received. Keep your reference below and complete payment where required; the hotel will confirm the stay after payment verification.</p>
        
        {GetBookingSummaryHtml(bookingCode, roomName, roomCategory, capacity, checkIn, checkOut, nights, totalAmount)}

        <div style='margin-top:25px; padding:17px 18px; background-color:#FAF5E9; border:1px solid #E7D5AE; border-radius:6px; font-size:14px; color:#5C4826;'>
            <strong>Important note:</strong> Please present a valid government-issued ID upon check-in. Our check-in time starts from 2:00 PM.
        </div>
        <p style='margin-top:20px;'>If you have any special requests prior to your arrival, simply reply to this email.</p>";

        await SendEmailAsync(
            email,
            $"Booking received: {bookingCode}",
            BuildTemplate(
                "Booking Received",
                content,
                "#B7792A",
                $"We have received reservation {bookingCode}."));
    }

    public async Task SendCancellationNoticeAsync(string email, string guestName, string bookingCode, string roomName, string roomCategory, DateTime checkIn, string? reason = null)
    {
        guestName = E(guestName);
        var safeRoomName = E(roomName);
        reason = E(!string.IsNullOrWhiteSpace(reason) ? reason : "Requested by guest or management.");
        var content = $@"
        <p style='margin-top:0;'>Dear <strong>{guestName}</strong>,</p>
        <p>This email confirms that your reservation for <strong>{safeRoomName}</strong> starting on <strong>{checkIn:MMM dd, yyyy}</strong> has been cancelled.</p>
        
        <div style='margin:20px 0; padding:15px; background-color:#FFF1F2; border-left:4px solid #E11D48; color:#9F1239;'>
            <strong>Reason for Cancellation:</strong><br/>
            {reason}
        </div>

        {GetBookingSummaryHtml(bookingCode, roomName, roomCategory, null, checkIn, null, null, null)}

        <p style='margin-top:20px; font-size:14px; color:#64748B;'>If this was an error or you wish to re-book, please visit our website or contact our support team.</p>";

        await SendEmailAsync(
            email,
            $"Booking cancelled: {bookingCode}",
            BuildTemplate(
                "Booking Cancelled",
                content,
                "#9C2F2F",
                $"Reservation {bookingCode} has been cancelled."));
    }

    public async Task SendCheckInReminderAsync(string email, string guestName, string bookingCode, string roomName, DateTime checkIn)
    {
        guestName = E(guestName);
        roomName = E(roomName);
        var manageBookingUrl = E($"{_publicAppUrl}/manage-booking");
        var content = $@"
        <p style='margin-top:0;'>Dear <strong>{guestName}</strong>,</p>
        <p>We are looking forward to your arrival! This is a friendly reminder that your stay in our <strong>{roomName}</strong> begins tomorrow, <strong>{checkIn:MMM dd, yyyy}</strong>.</p>
        
        <div style='margin:25px 0; text-align:center;'>
             <a class='mobile-button' href='{manageBookingUrl}' style='display:inline-block; background-color:#C94B11; color:#FFFFFF; padding:14px 30px; border-radius:8px; text-decoration:none; font-weight:700;'>View Stay Details</a>
        </div>

        <p style='font-size:14px; line-height:1.6;'>Standard check-in starts at <strong>2:00 PM</strong>. Safe travels!</p>";

        await SendEmailAsync(
            email,
            "Your stay begins tomorrow",
            BuildTemplate(
                "Your Stay Begins Tomorrow",
                content,
                "#B7792A",
                $"We look forward to welcoming you for reservation {bookingCode}."));
    }

    public async Task SendEmailVerificationAsync(string email, string name, string link)
    {
        name = E(name);
        link = E(link);
        var content = $@"
        <p style='margin-top:0;'>Dear <strong>{name}</strong>,</p>
        <p>Thank you for registering with <strong>Moore Hotels & Suites</strong>. To complete your account setup and access our full range of services, please verify your email address below.</p>
        
        <div style='margin:35px 0; text-align:center;'>
             <a class='mobile-button' href='{link}' style='display:inline-block; background-color:#C94B11; color:#FFFFFF; padding:16px 35px; border-radius:8px; text-decoration:none; font-weight:700; font-size:16px;'>Verify My Email</a>
        </div>

        <p class='mobile-break' style='font-size:13px; color:#777067;'>This link will expire in two hours. If the button above doesn't work, copy and paste this link into your browser:<br/>{link}</p>";

        await SendEmailAsync(
            email,
            "Verify your Moore Hotels email",
            BuildTemplate(
                "Confirm Your Registration",
                content,
                "#B7792A",
                "Complete your Moore Hotels & Suites account registration."));
    }

    public async Task SendPasswordResetAsync(string email, string name, string link)
    {
        name = E(name);
        link = E(link);
        var content = $@"
        <p style='margin-top:0;'>Dear <strong>{name}</strong>,</p>
        <p>We received a request to reset your Moore Hotels account password.</p>

        <div style='margin:35px 0; text-align:center;'>
             <a class='mobile-button' href='{link}' style='display:inline-block; background-color:#C94B11; color:#FFFFFF; padding:16px 35px; border-radius:8px; text-decoration:none; font-weight:700; font-size:16px;'>Reset My Password</a>
        </div>

        <p style='font-size:13px; color:#64748B;'>This link expires in two hours and can be used once. If you did not request it, you can ignore this message.</p>";

        await SendEmailAsync(
            email,
            "Reset your Moore Hotels password",
            BuildTemplate(
                "Password Reset",
                content,
                "#B7792A",
                "Use the secure link in this message to reset your password."));
    }

    public async Task SendPaymentSuccessAsync(string email, string guestName, string bookingCode, string roomName, decimal amount, string reference)
    {
        guestName = E(guestName);
        bookingCode = E(bookingCode);
        roomName = E(roomName);
        reference = E(reference);
        var content = $@"
        <p style='margin-top:0;'>Dear <strong>{guestName}</strong>,</p>
        <p>This is to confirm that we have successfully received your payment for booking <strong>{bookingCode}</strong>.</p>
        
        <table role='presentation' width='100%' style='margin:25px 0; background-color:#F0FDF4; border: 1px solid #BBF7D0; border-radius:12px; padding:20px;'>
            <tr>
                <td class='mobile-stack'>
                    <div style='font-size:12px; color:#166534; text-transform:uppercase; letter-spacing:1px; margin-bottom:5px;'>Amount Received</div>
                    <div style='font-size:24px; font-weight:800; color:#14532D;'>NGN {amount:N2}</div>
                </td>
                <td class='mobile-stack-right' align='right'>
                    <div style='font-size:11px; color:#166534; text-transform:uppercase; letter-spacing:1px; margin-bottom:5px;'>Transaction Reference</div>
                    <div class='mobile-break' style='font-size:14px; font-family:monospace; color:#14532D;'>{reference}</div>
                </td>
            </tr>
        </table>

        <p style='font-size:14px; color:#4B5563;'>Your stay in our <strong>{roomName}</strong> is now fully secured. We look forward to seeing you soon.</p>";

        await SendEmailAsync(
            email,
            $"Payment confirmed: {bookingCode}",
            BuildTemplate(
                "Payment Confirmed",
                content,
                "#2F6B4F",
                $"Payment for reservation {bookingCode} has been confirmed."));
    }

    public async Task SendCheckOutThankYouAsync(string email, string guestName, string bookingCode, string roomName)
    {
        guestName = E(guestName);
        bookingCode = E(bookingCode);
        roomName = E(roomName);
        var content = $@"
        <p style='margin-top:0;'>Dear <strong>{guestName}</strong>,</p>
        <p>Thank you for choosing <strong>Moore Hotels & Suites</strong>. We hope you enjoyed your stay in our <strong>{roomName}</strong> and that everything was to your satisfaction.</p>
        
        <table role='presentation' width='100%' style='margin:25px 0; border:1px solid #E2E8F0; border-radius:12px; padding:20px; background-color:#F8FAFC;'>
            <tr>
                <td class='mobile-stack'>
                    <div style='font-size:11px; color:#64748B; text-transform:uppercase; letter-spacing:1px; margin-bottom:5px;'>Booking Reference</div>
                    <div style='font-size:16px; font-weight:700; color:#1E293B; font-family:monospace;'>{bookingCode}</div>
                </td>
                <td class='mobile-stack-right' align='right'>
                    <div style='font-size:11px; color:#64748B; text-transform:uppercase; letter-spacing:1px; margin-bottom:5px;'>Stay Location</div>
                    <div style='font-size:16px; font-weight:700; color:#1E293B;'>{roomName}</div>
                </td>
            </tr>
        </table>

        <p style='font-size:14px; color:#475569;'>We would love to hear about your experience. Feel free to leave us a review or contact us directly with any feedback.</p>
        <p style='margin-top:20px; font-weight:600;'>We hope to welcome you back soon!</p>";

        await SendEmailAsync(
            email,
            "Thank you for staying with Moore Hotels",
            BuildTemplate(
                "Thank You for Your Stay",
                content,
                "#B7792A",
                $"Thank you for choosing us for reservation {bookingCode}."));
    }

    // Vibrant staff welcome email
    public async Task SendStaffWelcomeEmailAsync(
        string email,
        string name,
        string setupLink,
        string role)
    {
        name = E(name);
        setupLink = E(setupLink);
        role = E(role);
        var subject = "Welcome to Moore Hotels Team";
        var accentGreen = "#16a34a";

        var content = $@"
        <p style='margin-top:0; font-size:16px; color:#2D3436;'>
            Dear <strong>{name}</strong>,
        </p>

        <p style='font-size:16px; color:#4A4A4A;'>
            Welcome to the <strong>Moore Hotels & Suites</strong> family. 
            Your staff account has been successfully provisioned.
        </p>

        <!-- Luxury Info Box -->
        <table role='presentation' width='100%' border='0' cellspacing='0' cellpadding='0' 
               style='margin:30px 0; background-color:#F0FDF4; border: 1px solid #DCFCE7; border-radius:8px;'>
            <tr>
                <td style='padding:25px;'>
                    <div style='margin-bottom:20px;'>
                        <span style='font-size:11px; font-weight:bold; color:{accentGreen}; text-transform:uppercase; letter-spacing:1px;'>Assigned Role</span><br/>
                        <span style='font-size:18px; font-weight:600; color:#111827;'>{role}</span>
                    </div>
                </td>
            </tr>
        </table>

        <p style='font-size:15px; color:#4A4A4A; line-height:1.6;'>
            Create your password using the secure, single-use link below. It expires in two hours.
        </p>

        <div style='margin:30px 0; text-align:center;'>
            <a class='mobile-button' href='{setupLink}' style='display:inline-block; background-color:{accentGreen}; color:#FFFFFF; padding:14px 30px; border-radius:8px; text-decoration:none; font-weight:700;'>Set Up My Account</a>
        </div>

        <p style='font-size:13px; color:#94A3B8; font-style:italic; margin-top:25px; border-top:1px solid #F1F5F9; padding-top:15px;'>
            If you experience any issues accessing your account, please contact the system administrator.
        </p>";

        await SendEmailAsync(
            email,
            subject,
            BuildTemplate(
                "Welcome to the Team",
                content,
                "#2F6B4F",
                "Your Moore Hotels & Suites staff account is ready."));
    }

    // Admin & security
    public async Task SendAdminNewBookingAlertAsync(string adminEmail, string guestName, string bookingCode, string roomName, string roomCategory, int capacity, DateTime checkIn, DateTime checkOut, int nights, decimal totalAmount, string guestEmail, string guestPhone)
    {
        guestName = E(guestName);
        guestEmail = E(guestEmail);
        guestPhone = E(guestPhone);
        var bookingsUrl = E($"{_dashboardUrl}/?view=bookings");
        var content = $@"
        <p style='margin:0 0 20px 0;'>A new booking has been received and requires attention from the operations team.</p>
        
        <table role='presentation' width='100%' style='background-color:#F8F9FA; border-radius:12px; border:1px solid #E5E7EB;'>
            <tr>
                <td style='padding:25px;'>
                    <div style='font-size:12px; color:#6B7280; text-transform:uppercase; letter-spacing:1px; margin-bottom:8px;'>Guest Information</div>
                    <div style='font-size:18px; font-weight:700; color:#111827;'>{guestName}</div>
                    <div class='mobile-break' style='font-size:14px; color:#4B5563; margin-top:4px;'>{guestEmail} | {guestPhone}</div>
                </td>
            </tr>
        </table>

        {GetBookingSummaryHtml(bookingCode, roomName, roomCategory, capacity, checkIn, checkOut, nights, totalAmount)}

        <div style='margin-top:25px; text-align:center;'>
            <a class='mobile-button' href='{bookingsUrl}' style='display:inline-block; background-color:#111111; color:#FFFFFF; padding:12px 25px; border-radius:6px; text-decoration:none; font-size:14px; font-weight:600;'>Manage in Portal</a>
        </div>";

        await SendEmailAsync(
            adminEmail,
            $"New reservation: {bookingCode}",
            BuildTemplate(
                "New Reservation Received",
                content,
                "#B7792A",
                $"Reservation {bookingCode} requires operational attention."));
    }


    public async Task SendAccountSuspendedAsync(string email, string name)
    {
        name = E(name);
        var content = $@"
        <p>Dear <strong>{name}</strong>,</p>
        <p>This is to inform you that your account access has been suspended by the administration.</p>

        <p style='font-size:14px;'>Please contact support if you believe this is an error.</p>";
        await SendEmailAsync(
            email,
            "Moore Hotels account suspended",
            BuildTemplate(
                "Account Access Suspended",
                content,
                "#9C2F2F",
                "An important update about your Moore Hotels account."));
    }

    public async Task SendAccountActivatedAsync(string email, string name)
    {
        name = E(name);
        var content = $@"
        <p>Dear <strong>{name}</strong>,</p>
        <p>We are pleased to inform you that your account access at <strong>Moore Hotels & Suites</strong> has been successfully restored.</p>
        <p>You may now log in to the portal using your existing credentials.</p>";
        await SendEmailAsync(
            email,
            "Moore Hotels account access restored",
            BuildTemplate(
                "Account Access Restored",
                content,
                "#2F6B4F",
                "Your Moore Hotels account access has been restored."));
    }

    // Refunds
    public async Task SendAdminRefundAlertAsync(string adminEmail, string guestName, string bookingCode, string roomName, decimal amount)
    {
        guestName = E(guestName);
        bookingCode = E(bookingCode);
        roomName = E(roomName);
        var content = $@"
        <p style='margin:0 0 20px 0;'>A manual refund request has been triggered and requires administrative confirmation.</p>
        
        <table role='presentation' width='100%' style='border:1px solid #FECACA; background-color:#FEF2F2; border-radius:12px; padding:20px;'>
            <tr>
                <td class='mobile-stack'>
                    <div style='font-size:11px; color:#B91C1C; text-transform:uppercase; letter-spacing:1px; margin-bottom:5px;'>Refund Amount</div>
                    <div style='font-size:24px; font-weight:800; color:#991B1B;'>NGN {amount:N2}</div>
                </td>
                <td class='mobile-stack-right' align='right'>
                    <div style='font-size:11px; color:#B91C1C; text-transform:uppercase; letter-spacing:1px; margin-bottom:5px;'>Booking Code</div>
                    <div style='font-size:16px; font-weight:700; color:#991B1B; font-family:monospace;'>{bookingCode}</div>
                </td>
            </tr>
        </table>

        <div style='margin-top:20px; padding:15px; background-color:#FFFFFF; border:1px solid #FEE2E2; border-radius:8px;'>
            <div style='font-size:12px; color:#6B7280; margin-bottom:4px;'>Guest Details</div>
            <div style='font-size:14px; font-weight:600; color:#111827;'>{guestName}</div>
            <div style='font-size:12px; color:#6B7280; margin-top:10px; margin-bottom:4px;'>Room</div>
            <div style='font-size:14px; font-weight:600; color:#111827;'>{roomName}</div>
        </div>";

        await SendEmailAsync(
            adminEmail,
            $"Refund action required: {bookingCode}",
            BuildTemplate(
                "Refund Action Required",
                content,
                "#9C2F2F",
                $"Reservation {bookingCode} requires refund processing."));
    }

    public async Task SendRefundCompletionNoticeAsync(string email, string guestName, string bookingCode, string roomName, decimal amount, string reference)
    {
        guestName = E(guestName);
        bookingCode = E(bookingCode);
        roomName = E(roomName);
        reference = E(reference);
        var content = $@"
        <p style='margin-top:0;'>Dear <strong>{guestName}</strong>,</p>
        <p>Your refund for booking <strong>{bookingCode}</strong> (<strong>{roomName}</strong>) has been successfully processed.</p>
        
        <table role='presentation' width='100%' style='margin:25px 0; background-color:#F0FDF4; border:1px solid #BBF7D0; border-radius:12px; padding:20px;'>
            <tr>
                <td class='mobile-stack'>
                    <div style='font-size:12px; color:#166534; text-transform:uppercase; letter-spacing:1px; margin-bottom:5px;'>Amount Refunded</div>
                    <div style='font-size:24px; font-weight:800; color:#14532D;'>NGN {amount:N2}</div>
                </td>
                <td class='mobile-stack-right' align='right'>
                    <div style='font-size:11px; color:#166534; text-transform:uppercase; letter-spacing:1px; margin-bottom:5px;'>Reference ID</div>
                    <div class='mobile-break' style='font-size:14px; font-family:monospace; color:#14532D;'>{reference}</div>
                </td>
            </tr>
        </table>

        <p style='font-size:14px; color:#4B5563;'>The funds should reflect in your account within 3-5 business days. We apologize for any inconvenience caused and hope to serve you again in the future.</p>";

        await SendEmailAsync(
            email,
            $"Refund completed: {bookingCode}",
            BuildTemplate(
                "Refund Completed",
                content,
                "#2F6B4F",
                $"The refund for reservation {bookingCode} has been processed."));
    }
}
