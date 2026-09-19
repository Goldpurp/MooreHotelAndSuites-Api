namespace MooreHotels.Application.Exceptions;

public sealed class ServiceUnavailableException : Exception
{
    public string? ErrorCode { get; init; }

    public ServiceUnavailableException(string message) : base(message) { }

    public ServiceUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }
}
