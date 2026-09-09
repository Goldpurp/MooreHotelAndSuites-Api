namespace MooreHotels.Domain.Enums;

public enum DataSubjectRequestType
{
    Access,
    Rectification,
    Erasure,
    Restriction,
    Portability,
    Objection
}

public enum DataSubjectRequestStatus
{
    Pending,
    InProgress,
    Completed,
    Rejected
}
