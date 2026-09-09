using FluentValidation;
using MooreHotels.Application.DTOs;

namespace MooreHotels.Application.Validators;

public sealed class CreateAddOnServiceRequestValidator : AbstractValidator<CreateAddOnServiceRequest>
{
    public CreateAddOnServiceRequestValidator()
    {
        RuleFor(request => request.Name)
            .NotEmpty()
            .MaximumLength(120);
        RuleFor(request => request.Description)
            .MaximumLength(500);
        RuleFor(request => request.Category)
            .IsInEnum();
        RuleFor(request => request.Price)
            .GreaterThan(0)
            .LessThanOrEqualTo(100_000_000m);
    }
}

public sealed class UpdateAddOnServiceRequestValidator : AbstractValidator<UpdateAddOnServiceRequest>
{
    public UpdateAddOnServiceRequestValidator()
    {
        RuleFor(request => request.Name)
            .NotEmpty()
            .MaximumLength(120)
            .When(request => request.Name is not null);
        RuleFor(request => request.Description)
            .MaximumLength(500)
            .When(request => request.Description is not null);
        RuleFor(request => request.Category)
            .IsInEnum()
            .When(request => request.Category.HasValue);
        RuleFor(request => request.Price)
            .GreaterThan(0)
            .LessThanOrEqualTo(100_000_000m)
            .When(request => request.Price.HasValue);
    }
}

public sealed class AddServiceToBookingRequestValidator : AbstractValidator<AddServiceToBookingRequest>
{
    public AddServiceToBookingRequestValidator()
    {
        RuleFor(request => request.AddOnServiceId)
            .NotEmpty();
        RuleFor(request => request.Quantity)
            .InclusiveBetween(1, 100);
        RuleFor(request => request.Notes)
            .MaximumLength(300);
    }
}
