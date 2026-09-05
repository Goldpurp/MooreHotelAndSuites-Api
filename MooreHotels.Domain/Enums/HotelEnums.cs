namespace MooreHotels.Domain.Enums;

public enum RoomCategory
{
    Standard,
    Deluxe,
    Executive,
    PresidentialSuite
}

public enum PropertyFloor
{
    GroundFloor,
    FirstFloor,
    SecondFloor,
    Bungalow
}


public enum RoomStatus
{
    Available,
    Occupied,
    Cleaning,
    Maintenance,
    Reserved,
    Dirty,
    Clean,
    Inspected,
    OutOfOrder
}

public enum BookingStatus
{
    Pending,
    Confirmed,
    CheckedIn,
    CheckedOut,
    Cancelled,
    NoShow
}


public enum PaymentStatus
{
    Paid,
    PartiallyPaid,
    Unpaid,
    AwaitingVerification,
    RefundPending,
    Refunded
}

public enum PaymentMethod
{
    Monnify,
    // Kept only so historical rows containing this value remain readable.
    // Runtime validation rejects new Paystack bookings.
    Paystack,
    DirectTransfer
}

public enum UserRole
{
    Admin,
    Manager,
    Staff,
    Client
}

public enum ProfileStatus
{
    Active,
    Suspended
}

public enum AddOnCategory
{
    Dining,
    Wellness,
    Transportation,
    Laundry,
    ExecutiveServices,
    Other
}

public enum FolioStatus
{
    Open,
    Closed
}

public enum FolioEntryType
{
    RoomCharge,
    AddOnCharge,
    Tax,
    Fee,
    Discount,
    Payment,
    Credit,
    Refund,
    Adjustment,
    Void
}

public enum FolioEntryDirection
{
    Debit,
    Credit
}

public enum HousekeepingTaskType
{
    CheckoutCleaning,
    StayoverService,
    Inspection,
    MaintenanceRecovery,
    RoomMoveCleaning
}

public enum OperationalTaskStatus
{
    Pending,
    Assigned,
    InProgress,
    Completed,
    Cancelled
}

public enum WorkPriority
{
    Low,
    Normal,
    High,
    Urgent
}

public enum MaintenanceWorkOrderStatus
{
    Open,
    Assigned,
    InProgress,
    Resolved,
    Cancelled
}

public enum ChannelEventDirection
{
    Inbound,
    Outbound
}

public enum ChannelEventStatus
{
    Pending,
    Processed,
    Failed,
    DeadLetter
}
