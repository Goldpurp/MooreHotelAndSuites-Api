using FluentAssertions;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Validators;
using MooreHotels.Domain.Enums;
using Xunit;

namespace MooreHotels.UnitTests.Validators;

public class FluentValidationTests
{
    private readonly CreateBookingRequestValidator _bookingValidator = new();
    private readonly CreateRoomRequestValidator _roomValidator = new();
    private readonly RegisterRequestValidator _registerValidator = new();

    [Fact]
    public void CreateBookingRequestValidator_ShouldFail_WhenCheckOutIsBeforeCheckIn()
    {
        // Arrange
        var request = new CreateBookingRequest(
            RoomId: Guid.NewGuid(),
            GuestFirstName: "John",
            GuestLastName: "Doe",
            GuestEmail: "john.doe@example.com",
            GuestPhone: "+2348012345678",
            CheckIn: DateTime.UtcNow.Date.AddDays(5),
            CheckOut: DateTime.UtcNow.Date.AddDays(3), // Invalid: before check-in
            PaymentMethod: PaymentMethod.Monnify,
            Notes: "Arriving late");

        // Act
        var result = _bookingValidator.Validate(request);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateBookingRequest.CheckOut));
    }

    [Fact]
    public void CreateBookingRequestValidator_ShouldPass_ForValidRequest()
    {
        // Arrange
        var request = new CreateBookingRequest(
            RoomId: Guid.NewGuid(),
            GuestFirstName: "Jane",
            GuestLastName: "Smith",
            GuestEmail: "jane.smith@example.com",
            GuestPhone: "+2348098765432",
            CheckIn: DateTime.UtcNow.Date.AddDays(1),
            CheckOut: DateTime.UtcNow.Date.AddDays(4),
            PaymentMethod: PaymentMethod.Monnify,
            Notes: null);

        // Act
        var result = _bookingValidator.Validate(request);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void CreateBookingRequestValidator_ShouldFail_ForArchivedPaystackMethod()
    {
        var request = new CreateBookingRequest(
            RoomId: Guid.NewGuid(),
            GuestFirstName: "Jane",
            GuestLastName: "Smith",
            GuestEmail: "jane.smith@example.com",
            GuestPhone: "+2348098765432",
            CheckIn: DateTime.UtcNow.Date.AddDays(1),
            CheckOut: DateTime.UtcNow.Date.AddDays(4),
            PaymentMethod: PaymentMethod.Paystack,
            Notes: null);

        var result = _bookingValidator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error =>
            error.PropertyName == nameof(CreateBookingRequest.PaymentMethod) &&
            error.ErrorMessage.Contains("Paystack", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateRoomRequestValidator_ShouldFail_WhenPriceIsZeroOrNegative()
    {
        // Arrange
        var request = new CreateRoomRequest(
            RoomNumber: "201",
            Name: "Standard Room",
            Category: RoomCategory.Standard,
            Floor: PropertyFloor.SecondFloor,
            Status: RoomStatus.Available,
            PricePerNight: -500m, // Invalid negative price
            Capacity: 2,
            Size: "30 sqm",
            Description: "Cozy room",
            Amenities: new List<string> { "WiFi", "AC" });

        // Act
        var result = _roomValidator.Validate(request);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateRoomRequest.PricePerNight));
    }

    [Fact]
    public void RegisterRequestValidator_ShouldFail_WhenPasswordTooShort()
    {
        // Arrange
        var request = new RegisterRequest(
            FirstName: "User",
            LastName: "Test",
            Email: "user@example.com",
            Password: "123", // Invalid: < 8 chars
            Phone: "+2348011223344");

        // Act
        var result = _registerValidator.Validate(request);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(RegisterRequest.Password));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void AddServiceToBookingRequestValidator_ShouldFail_ForUnsafeQuantity(int quantity)
    {
        var validator = new AddServiceToBookingRequestValidator();
        var request = new AddServiceToBookingRequest(Guid.NewGuid(), quantity, null);

        var result = validator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error =>
            error.PropertyName == nameof(AddServiceToBookingRequest.Quantity));
    }
}
