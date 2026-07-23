namespace MooreHotels.Application.Exceptions;

public sealed class ServiceUnavailableException : Exception
{
    public ServiceUnavailableException(string message) : base(message) { }

    public ServiceUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }
}
