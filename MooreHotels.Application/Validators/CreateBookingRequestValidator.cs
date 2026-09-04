using FluentValidation;
using MooreHotels.Application.DTOs;
using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.Validators;

public class CreateBookingRequestValidator : AbstractValidator<CreateBookingRequest>
{
    public CreateBookingRequestValidator()
    {
        RuleFor(x => x.RoomId)
            .NotEmpty().WithMessage("Room selection is required.");

        RuleFor(x => x.CheckIn)
            .NotEmpty().WithMessage("Check-in date is required.");

        RuleFor(x => x.CheckOut)
            .NotEmpty().WithMessage("Check-out date is required.")
            .GreaterThan(x => x.CheckIn)
            .WithMessage("Check-out date must be strictly after the check-in date.");

        RuleFor(x => x.GuestFirstName)
            .NotEmpty().WithMessage("Guest first name is required.")
            .MaximumLength(80).WithMessage("First name cannot exceed 80 characters.");

        RuleFor(x => x.GuestLastName)
            .NotEmpty().WithMessage("Guest last name is required.")
            .MaximumLength(80).WithMessage("Last name cannot exceed 80 characters.");

        RuleFor(x => x.GuestEmail)
            .NotEmpty().WithMessage("Guest email address is required.")
            .EmailAddress().WithMessage("A valid email address is required.")
            .MaximumLength(254).WithMessage("Email cannot exceed 254 characters.");

        RuleFor(x => x.GuestPhone)
            .NotEmpty().WithMessage("Phone number is required.")
            .Matches(@"^\+?[0-9\s\-()]{7,30}$").WithMessage("Invalid phone number format.");

        RuleFor(x => x.PaymentMethod)
            .NotNull().WithMessage("Payment method is required.")
            .IsInEnum().WithMessage("Invalid payment method selected.")
            .Must(method => method is PaymentMethod.Monnify or PaymentMethod.DirectTransfer)
            .WithMessage("Paystack is not supported. Choose Monnify or direct bank transfer.");

        RuleFor(x => x.EmailVerificationToken)
            .MaximumLength(128)
            .MinimumLength(40)
            .When(x => !string.IsNullOrWhiteSpace(x.EmailVerificationToken));
    }
}

public sealed class RequestBookingEmailVerificationRequestValidator
    : AbstractValidator<RequestBookingEmailVerificationRequest>
{
    public RequestBookingEmailVerificationRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Guest email address is required.")
            .EmailAddress().WithMessage("A valid email address is required.")
            .MaximumLength(254).WithMessage("Email cannot exceed 254 characters.");
    }
}
