using FluentValidation;
using MooreHotels.Application.DTOs;

namespace MooreHotels.Application.Validators;

public class CreateRoomRequestValidator : AbstractValidator<CreateRoomRequest>
{
    public CreateRoomRequestValidator()
    {
        RuleFor(x => x.RoomNumber)
            .MaximumLength(30).WithMessage("Room number cannot exceed 30 characters.");

        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Room name is required.")
            .MaximumLength(120).WithMessage("Room name cannot exceed 120 characters.");

        RuleFor(x => x.PricePerNight)
            .GreaterThan(0).WithMessage("Price per night must be greater than zero.");

        RuleFor(x => x.Capacity)
            .GreaterThan(0).WithMessage("Capacity must be at least 1 guest.")
            .LessThanOrEqualTo(50).WithMessage("Capacity cannot exceed 50 guests.");

        RuleFor(x => x.Category)
            .IsInEnum().WithMessage("Invalid room category.");

        RuleFor(x => x.Floor)
            .IsInEnum().WithMessage("Invalid property floor.");

        RuleFor(x => x.Status)
            .IsInEnum().WithMessage("Invalid room status.");

        RuleFor(x => x.Size)
            .MaximumLength(50);

        RuleFor(x => x.Description)
            .MaximumLength(4000);

        RuleFor(x => x.Amenities)
            .NotNull()
            .Must(items => items.Count <= 50)
            .WithMessage("A room cannot have more than 50 amenities.");

        RuleForEach(x => x.Amenities)
            .NotEmpty()
            .MaximumLength(120);
    }
}

public class UpdateRoomRequestValidator : AbstractValidator<UpdateRoomRequest>
{
    public UpdateRoomRequestValidator()
    {
        RuleFor(x => x.Name)
            .MaximumLength(120).When(x => x.Name != null)
            .WithMessage("Room name cannot exceed 120 characters.");

        RuleFor(x => x.PricePerNight)
            .GreaterThan(0).When(x => x.PricePerNight.HasValue)
            .WithMessage("Price per night must be greater than zero.");

        RuleFor(x => x.Capacity)
            .GreaterThan(0).When(x => x.Capacity.HasValue)
            .WithMessage("Capacity must be at least 1 guest.")
            .LessThanOrEqualTo(50).When(x => x.Capacity.HasValue)
            .WithMessage("Capacity cannot exceed 50 guests.");

        RuleFor(x => x.Category)
            .IsInEnum().When(x => x.Category.HasValue)
            .WithMessage("Invalid room category.");

        RuleFor(x => x.Floor)
            .IsInEnum().When(x => x.Floor.HasValue)
            .WithMessage("Invalid property floor.");

        RuleFor(x => x.Status)
            .IsInEnum().When(x => x.Status.HasValue)
            .WithMessage("Invalid room status.");

        RuleFor(x => x.Size)
            .MaximumLength(50)
            .When(x => x.Size is not null);

        RuleFor(x => x.Description)
            .MaximumLength(4000)
            .When(x => x.Description is not null);

        RuleFor(x => x.Amenities)
            .Must(items => items is null || items.Count <= 50)
            .WithMessage("A room cannot have more than 50 amenities.");

        RuleForEach(x => x.Amenities)
            .NotEmpty()
            .MaximumLength(120)
            .When(x => x.Amenities is not null);

        RuleForEach(x => x.Images)
            .NotEmpty()
            .MaximumLength(2048)
            .When(x => x.Images is not null);
    }
}
