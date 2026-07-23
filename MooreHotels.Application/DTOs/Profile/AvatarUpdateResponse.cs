namespace MooreHotels.Application.DTOs;

public sealed record AvatarUpdateData(string AvatarUrl, DateTime UpdatedAtUtc);

public sealed record AvatarUpdateResponse(string Message, AvatarUpdateData Data);
