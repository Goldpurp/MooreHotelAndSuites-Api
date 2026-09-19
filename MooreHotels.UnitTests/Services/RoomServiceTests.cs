using FluentAssertions;
using Moq;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Exceptions;
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
    private readonly Mock<IHotelTimeService> _hotelTimeMock;
    private readonly Mock<IAuditService> _auditServiceMock;
    private readonly RoomService _service;

    public RoomServiceTests()
    {
        _roomRepoMock = new Mock<IRoomRepository>();
        _bookingRepoMock = new Mock<IBookingRepository>();
        _hotelTimeMock = new Mock<IHotelTimeService>();
        _auditServiceMock = new Mock<IAuditService>();
        _hotelTimeMock.Setup(service => service.GetCheckInUtc(It.IsAny<DateTime>()))
            .Returns<DateTime>(date => DateTime.SpecifyKind(date.Date.AddHours(13), DateTimeKind.Utc));
        _hotelTimeMock.Setup(service => service.GetCheckOutUtc(It.IsAny<DateTime>()))
            .Returns<DateTime>(date => DateTime.SpecifyKind(date.Date.AddHours(11), DateTimeKind.Utc));
        _hotelTimeMock.SetupGet(service => service.CheckInTime).Returns(new TimeOnly(14, 0));
        _hotelTimeMock.SetupGet(service => service.CheckOutTime).Returns(new TimeOnly(12, 0));

        _service = new RoomService(
            _roomRepoMock.Object,
            _bookingRepoMock.Object,
            _hotelTimeMock.Object,
            _auditServiceMock.Object);
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
            Status = RoomStatus.Available,
            RoomType = new RoomType { Id = Guid.NewGuid(), IsActive = true }
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
            Status = RoomStatus.Available,
            RoomType = new RoomType { Id = Guid.NewGuid(), IsActive = true }
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

    [Fact]
    public async Task CreateRoomAsync_BootstrapsRoomType_WhenCategoryHasNone()
    {
        var actorId = Guid.NewGuid();
        var request = CreateValidRoomRequest();
        RoomType? addedType = null;
        Room? addedRoom = null;
        _roomRepoMock.Setup(repository => repository.GetByRoomNumberAsync(request.RoomNumber!))
            .ReturnsAsync((Room?)null);
        _roomRepoMock.Setup(repository => repository.GetDefaultRoomTypeForCategoryAsync(request.Category))
            .ReturnsAsync((RoomType?)null);
        _roomRepoMock.Setup(repository => repository.GetAnyRoomTypeForCategoryAsync(request.Category))
            .ReturnsAsync((RoomType?)null);
        _roomRepoMock.Setup(repository => repository.AddRoomTypeAsync(It.IsAny<RoomType>()))
            .Callback<RoomType>(type => addedType = type)
            .Returns(Task.CompletedTask);
        _roomRepoMock.Setup(repository => repository.AddAsync(It.IsAny<Room>()))
            .Callback<Room>(room => addedRoom = room)
            .Returns(Task.CompletedTask);

        var result = await _service.CreateRoomAsync(request, actorId);

        addedType.Should().NotBeNull();
        addedType!.Code.Should().Be("DELUXE");
        addedType.Name.Should().Be("Deluxe");
        addedType.Category.Should().Be(RoomCategory.Deluxe);
        addedType.BaseOccupancy.Should().Be(2);
        addedType.MaxOccupancy.Should().Be(request.Capacity);
        addedType.BasePricePerNight.Should().Be(request.PricePerNight);
        addedType.IsActive.Should().BeTrue();
        addedRoom.Should().NotBeNull();
        addedRoom!.RoomTypeId.Should().Be(addedType.Id);
        result.RoomTypeId.Should().Be(addedType.Id);
        _auditServiceMock.Verify(service => service.LogActionAsync(
            actorId,
            "ROOM_TYPE_CREATED",
            "RoomType",
            addedType.Id.ToString(),
            null,
            It.IsAny<object>()), Times.Once);
    }

    [Fact]
    public async Task CreateRoomAsync_RejectsInactiveExistingType_WithoutBootstrappingReplacement()
    {
        var actorId = Guid.NewGuid();
        var request = CreateValidRoomRequest();
        _roomRepoMock.Setup(repository => repository.GetByRoomNumberAsync(request.RoomNumber!))
            .ReturnsAsync((Room?)null);
        _roomRepoMock.Setup(repository => repository.GetDefaultRoomTypeForCategoryAsync(request.Category))
            .ReturnsAsync((RoomType?)null);
        _roomRepoMock.Setup(repository => repository.GetAnyRoomTypeForCategoryAsync(request.Category))
            .ReturnsAsync(new RoomType
            {
                Id = Guid.NewGuid(),
                Category = request.Category,
                IsActive = false
            });

        var action = () => _service.CreateRoomAsync(request, actorId);

        await action.Should().ThrowAsync<BadRequestException>()
            .WithMessage("Select an active room type.");
        _roomRepoMock.Verify(repository => repository.AddRoomTypeAsync(It.IsAny<RoomType>()), Times.Never);
        _roomRepoMock.Verify(repository => repository.AddAsync(It.IsAny<Room>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateRoomAsync_AllowsMissingOptionalDetails(string? optional)
    {
        var request = CreateValidRoomRequest() with { RoomNumber = optional, Size = optional, Description = optional };
        var validation = new MooreHotels.Application.Validators.CreateRoomRequestValidator().Validate(request);
        validation.IsValid.Should().BeTrue();

        var result = await _service.CreateRoomAsync(request, Guid.NewGuid());

        result.Name.Should().Be("James");
        result.RoomNumber.Should().BeEmpty();
        result.Size.Should().BeEmpty();
        result.Description.Should().BeEmpty();
        _roomRepoMock.Verify(repository => repository.GetByRoomNumberAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CreateRoomAsync_RejectsDuplicateName_BeforeWritingRoom()
    {
        _roomRepoMock.Setup(repository => repository.GetByNameAsync("James"))
            .ReturnsAsync(new Room { Id = Guid.NewGuid(), Name = "James" });
        var action = () => _service.CreateRoomAsync(CreateValidRoomRequest(), Guid.NewGuid());
        await action.Should().ThrowAsync<BadRequestException>().WithMessage("*room name is already registered*");
        _roomRepoMock.Verify(repository => repository.AddAsync(It.IsAny<Room>()), Times.Never);
    }

    private static CreateRoomRequest CreateValidRoomRequest() => new(
        "001",
        "James",
        RoomCategory.Deluxe,
        PropertyFloor.FirstFloor,
        RoomStatus.Available,
        35000m,
        2,
        "35 sqm",
        "A comfortable deluxe room.",
        ["Wi-Fi", "Air conditioning"],
        false);
}
