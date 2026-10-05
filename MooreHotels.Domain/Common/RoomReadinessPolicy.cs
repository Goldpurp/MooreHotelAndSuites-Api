using System.Linq.Expressions;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;

namespace MooreHotels.Domain.Common;

public static class RoomReadinessPolicy
{
    // Housekeeping uses Inspected while inspection is in progress. Only a
    // passed inspection releases the room to Available for an immediate arrival.
    public static bool IsReady(Room room) => room.IsOnline && room.Status == RoomStatus.Available;

    public static bool CanSell(Room room, bool requireReady) =>
        room.IsOnline && room.Status is not (RoomStatus.Maintenance or RoomStatus.OutOfOrder) &&
        (!requireReady || room.Status == RoomStatus.Available);

    public static Expression<Func<Room, bool>> Sellable(bool requireReady) => room =>
        room.IsOnline && room.Status != RoomStatus.Maintenance && room.Status != RoomStatus.OutOfOrder &&
        (!requireReady || room.Status == RoomStatus.Available);
}
