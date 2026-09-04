using FluentAssertions;
using Moq;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Application.Services;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using Xunit;

namespace MooreHotels.UnitTests.Services;

public class RoomServiceTests
{
    private readonly Mock<IRoomRepository> _roomRepoMock;
    private readonly Mock<IBookingRepository> _bookingRepoMock;
    private readonly Mock<IImageService> _imageServiceMock;
    private readonly Mock<IHotelTimeService> _hotelTimeMock;
    private readonly RoomService _service;

    public RoomServiceTests()
    {
        _roomRepoMock = new Mock<IRoomRepository>();
        _bookingRepoMock = new Mock<IBookingRepository>();
        _imageServiceMock = new Mock<IImageService>();
        _hotelTimeMock = new Mock<IHotelTimeService>();
        _hotelTimeMock.Setup(service => service.GetCheckInUtc(It.IsAny<DateTime>()))
            .Returns<DateTime>(date => DateTime.SpecifyKind(date.Date.AddHours(13), DateTimeKind.Utc));
        _hotelTimeMock.Setup(service => service.GetCheckOutUtc(It.IsAny<DateTime>()))
            .Returns<DateTime>(date => DateTime.SpecifyKind(date.Date.AddHours(11), DateTimeKind.Utc));
        _hotelTimeMock.SetupGet(service => service.CheckInTime).Returns(new TimeOnly(14, 0));
        _hotelTimeMock.SetupGet(service => service.CheckOutTime).Returns(new TimeOnly(12, 0));

        _service = new RoomService(
            _roomRepoMock.Object,
            _bookingRepoMock.Object,
            _imageServiceMock.Object,
            _hotelTimeMock.Object);
    }

    [Fact]
    public async Task GetAllRoomsAsync_QueriesRepository_OnEveryCall()
    {
        // Arrange
        var rooms = new List<Room>
        {
            new()
            {
                Id = Guid.NewGuid(),
                RoomNumber = "101",
                Name = "Executive Suite",
                Category = RoomCategory.Executive,
                Floor = PropertyFloor.FirstFloor,
                PricePerNight = 45000m,
                Capacity = 2,
                Size = "45 sqm",
                Description = "Luxury suite",
                IsOnline = true,
                Status = RoomStatus.Available,
                CreatedAt = DateTime.UtcNow
            }
        };

        _roomRepoMock
            .Setup(r => r.GetAllAsync(true))
            .ReturnsAsync(rooms);

        // Act
        var firstCall = await _service.GetAllRoomsAsync();
        var secondCall = await _service.GetAllRoomsAsync();

        // Assert
        firstCall.Should().HaveCount(1);
        secondCall.Should().HaveCount(1);
        _roomRepoMock.Verify(r => r.GetAllAsync(true), Times.Exactly(2));
    }

    [Fact]
    public async Task CheckAvailabilityAsync_ReturnsFalse_WhenRoomIsBooked()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var room = new Room
        {
            Id = roomId,
            RoomNumber = "102",
            Name = "Deluxe Room",
            IsOnline = true,
            Status = RoomStatus.Available
        };

        var checkIn = DateTime.UtcNow.Date.AddDays(2);
        var checkOut = DateTime.UtcNow.Date.AddDays(4);

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _bookingRepoMock.Setup(b => b.IsRoomBookedAsync(roomId, It.IsAny<DateTime>(), It.IsAny<DateTime>())).ReturnsAsync(true);

        // Act
        var result = await _service.CheckAvailabilityAsync(roomId, checkIn, checkOut);

        // Assert
        result.Available.Should().BeFalse();
        result.Message.Should().Contain("already secured");
    }

    [Fact]
    public async Task CheckAvailabilityAsync_ReturnsTrue_WhenRoomIsAvailable()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var room = new Room
        {
            Id = roomId,
            RoomNumber = "103",
            Name = "Standard Room",
            IsOnline = true,
            Status = RoomStatus.Available
        };

        var checkIn = DateTime.UtcNow.Date.AddDays(5);
        var checkOut = DateTime.UtcNow.Date.AddDays(7);

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _bookingRepoMock.Setup(b => b.IsRoomBookedAsync(roomId, It.IsAny<DateTime>(), It.IsAny<DateTime>())).ReturnsAsync(false);

        // Act
        var result = await _service.CheckAvailabilityAsync(roomId, checkIn, checkOut);

        // Assert
        result.Available.Should().BeTrue();
        result.Message.Should().Contain("Available");
    }
}
