using FluentValidation;
using MooreHotels.Application.DTOs;

namespace MooreHotels.Application.Validators;

public class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.FirstName)
            .NotEmpty().WithMessage("First name is required.")
            .MaximumLength(80).WithMessage("First name cannot exceed 80 characters.");

        RuleFor(x => x.LastName)
            .NotEmpty().WithMessage("Last name is required.")
            .MaximumLength(80).WithMessage("Last name cannot exceed 80 characters.");

        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("A valid email address is required.")
            .MaximumLength(254).WithMessage("Email cannot exceed 254 characters.");

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required.")
            .MinimumLength(12).WithMessage("Password must be at least 12 characters long.")
            .MaximumLength(128).WithMessage("Password cannot exceed 128 characters.")
            .Must(password => !string.IsNullOrEmpty(password) && password.Any(char.IsUpper))
            .WithMessage("Password must contain an uppercase character.")
            .Must(password => !string.IsNullOrEmpty(password) && password.Any(char.IsLower))
            .WithMessage("Password must contain a lowercase character.")
            .Must(password => !string.IsNullOrEmpty(password) && password.Any(char.IsDigit))
            .WithMessage("Password must contain a digit.")
            .Must(password => !string.IsNullOrEmpty(password) && password.Any(character => !char.IsLetterOrDigit(character)))
            .WithMessage("Password must contain a non-alphanumeric character.")
            .Must(password => !string.IsNullOrEmpty(password) && password.Distinct().Count() >= 4)
            .WithMessage("Password must contain at least four unique characters.");

        RuleFor(x => x.Phone)
            .NotEmpty().WithMessage("Phone number is required.")
            .Matches(@"^\+?[0-9\s\-()]{7,30}$").WithMessage("Invalid phone number format.");
    }
}

public class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("A valid email address is required.");

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required.");

        RuleFor(x => x.TwoFactorCode)
            .MaximumLength(32)
            .When(x => x.TwoFactorCode is not null);
    }
}
