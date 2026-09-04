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
    Reserved
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
