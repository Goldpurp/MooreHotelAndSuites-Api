using System.Security.Claims;

namespace MooreHotels.Application.Common;

public static class HotelAuthorization
{
    public const string DepartmentClaimType = "department";

    public const string ReservationsRead = "Reservations.Read";
    public const string ReservationsManage = "Reservations.Manage";
    public const string GuestPiiRead = "Guests.Pii.Read";
    public const string FolioManage = "Folios.Manage";
    public const string OperationsRead = "Operations.Read";
    public const string HousekeepingManage = "Housekeeping.Manage";
    public const string MaintenanceManage = "Maintenance.Manage";
    public const string NightAuditClose = "NightAudit.Close";
    public const string GuestCrmManage = "Guests.Crm.Manage";
    public const string ChannelsManage = "Channels.Manage";

    public static bool HasDepartment(ClaimsPrincipal user, params string[] departments)
    {
        var department = user.FindFirstValue(DepartmentClaimType);
        return user.IsInRole("Staff") &&
               departments.Contains(department, StringComparer.OrdinalIgnoreCase);
    }
}
