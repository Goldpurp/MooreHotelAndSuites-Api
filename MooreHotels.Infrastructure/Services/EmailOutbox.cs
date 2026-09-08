using System.Net.Mail;
using System.Text.Json;
using MooreHotels.Application.Interfaces;
using MooreHotels.Domain.Entities;
using MooreHotels.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;

namespace MooreHotels.Infrastructure.Services;

public sealed class EmailOutbox : IEmailOutbox
{
    private const int MaximumPayloadLength = 32 * 1024;
    private readonly MooreHotelsDbContext _db;
    private readonly IDataProtector _protector;

    public EmailOutbox(
        MooreHotelsDbContext db,
        IDataProtectionProvider dataProtectionProvider)
    {
        _db = db;
        _protector = dataProtectionProvider.CreateProtector(
            "MooreHotels.EmailOutbox.Payload.v1");
    }

    public EmailOutboxMessage Create(
        string emailTemplate,
        string recipient,
        object payload,
        string? dataSubjectGuestId = null)
    {
        if (string.IsNullOrWhiteSpace(emailTemplate) || emailTemplate.Length > 50 ||
            !MailAddress.TryCreate(recipient, out var address))
        {
            throw new InvalidOperationException("The transactional email request is invalid.");
        }

        var payloadJson = JsonSerializer.Serialize(payload);
        if (payloadJson.Length > MaximumPayloadLength)
            throw new InvalidOperationException("The transactional email payload is too large.");

        var now = DateTime.UtcNow;
        return new EmailOutboxMessage
        {
            Id = Guid.NewGuid(),
            Template = emailTemplate,
            Recipient = address.Address,
            DataSubjectGuestId = string.IsNullOrWhiteSpace(dataSubjectGuestId)
                ? null
                : dataSubjectGuestId.Trim(),
            ProtectedPayload = _protector.Protect(payloadJson),
            AttemptCount = 0,
            NextAttemptAtUtc = now,
            CreatedAtUtc = now
        };
    }

    public T ReadPayload<T>(EmailOutboxMessage message) where T : class
    {
        var json = _protector.Unprotect(message.ProtectedPayload);
        return JsonSerializer.Deserialize<T>(json)
            ?? throw new InvalidOperationException("Transactional email payload is invalid.");
    }

    public async Task EnqueueAsync(
        string emailTemplate,
        string recipient,
        object payload,
        string? dataSubjectGuestId = null,
        CancellationToken cancellationToken = default)
    {
        _db.EmailOutboxMessages.Add(Create(
            emailTemplate,
            recipient,
            payload,
            dataSubjectGuestId));
        await _db.SaveChangesAsync(cancellationToken);
    }
}
