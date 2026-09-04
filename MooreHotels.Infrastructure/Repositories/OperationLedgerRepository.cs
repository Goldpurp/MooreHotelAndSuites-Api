using System.Data;
using Microsoft.EntityFrameworkCore;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace MooreHotels.Infrastructure.Repositories;

public sealed class OperationLedgerRepository : IOperationLedgerRepository
{
    private readonly MooreHotelsDbContext _db;

    public OperationLedgerRepository(MooreHotelsDbContext db) => _db = db;

    public async Task<IReadOnlyList<OperationLogEntryDto>> GetLedgerAsync(
        string? filter,
        string? search,
        int limit,
        DateTime? beforeUtc,
        Guid? beforeId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            WITH ledger AS (
                SELECT
                    md5(replace(b."Id"::text, '-', '') || ':reservation')::uuid AS event_id,
                    b."CreatedAt" AS event_time,
                    trim(concat_ws(' ', g."FirstName", g."LastName")) AS occupant_name,
                    coalesce(g."Email", '') AS occupant_email,
                    'RESERVATION'::text AS action,
                    coalesce(r."RoomNumber", 'N/A') AS asset_number,
                    coalesce(r."Category", '') AS asset_category,
                    b."BookingCode" AS verification_info,
                    'blue'::text AS status_color
                FROM bookings b
                LEFT JOIN guests g ON g."Id" = b."GuestId"
                LEFT JOIN rooms r ON r."Id" = b."RoomId"

                UNION ALL

                SELECT
                    md5(replace(b."Id"::text, '-', '') || ':cancelled')::uuid AS event_id,
                    coalesce(
                        b."CancelledAtUtc",
                        (
                            SELECT max((history_item->>'Timestamp')::timestamptz)
                            FROM jsonb_array_elements(
                                CASE
                                    WHEN jsonb_typeof(b."StatusHistoryJson") = 'array'
                                        THEN b."StatusHistoryJson"
                                    ELSE '[]'::jsonb
                                END
                            ) AS history_item
                            WHERE history_item->>'Status' IN ('Cancelled', '4')
                              AND history_item->>'Timestamp' ~
                                  '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}'
                        ),
                        b."CreatedAt") AS event_time,
                    trim(concat_ws(' ', g."FirstName", g."LastName")) AS occupant_name,
                    coalesce(g."Email", '') AS occupant_email,
                    'VOIDED'::text AS action,
                    coalesce(r."RoomNumber", 'N/A') AS asset_number,
                    coalesce(r."Category", '') AS asset_category,
                    b."BookingCode" AS verification_info,
                    'rose'::text AS status_color
                FROM bookings b
                LEFT JOIN guests g ON g."Id" = b."GuestId"
                LEFT JOIN rooms r ON r."Id" = b."RoomId"
                WHERE b."Status" = 'Cancelled'

                UNION ALL

                SELECT
                    v."Id" AS event_id,
                    v."Timestamp" AS event_time,
                    v."GuestName" AS occupant_name,
                    ''::text AS occupant_email,
                    replace(v."Action", '_', ' ') AS action,
                    v."RoomNumber" AS asset_number,
                    ''::text AS asset_category,
                    v."AuthorizedBy" AS verification_info,
                    CASE WHEN v."Action" = 'CHECK_IN' THEN 'emerald' ELSE 'amber' END AS status_color
                FROM visit_records v
            )
            SELECT
                event_id,
                event_time,
                occupant_name,
                occupant_email,
                action,
                asset_number,
                asset_category,
                verification_info,
                status_color
            FROM ledger
            WHERE (@filter IS NULL OR upper(action) = upper(@filter))
              AND (
                    @search IS NULL OR
                    occupant_name ILIKE '%' || @search || '%' OR
                    asset_number ILIKE '%' || @search || '%' OR
                    verification_info ILIKE '%' || @search || '%'
                  )
              AND (
                    @before_utc IS NULL OR
                    event_time < @before_utc OR
                    (@before_id IS NOT NULL AND event_time = @before_utc AND event_id < @before_id)
                  )
            ORDER BY event_time DESC, event_id DESC
            LIMIT @limit;
            """;

        var normalizedFilter = string.IsNullOrWhiteSpace(filter) ||
                               filter.Equals("SYSTEM FULL", StringComparison.OrdinalIgnoreCase)
            ? null
            : filter.Trim();
        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var normalizedBeforeUtc = beforeUtc.HasValue
            ? NormalizeUtc(beforeUtc.Value)
            : (DateTime?)null;

        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue(
                "filter",
                NpgsqlDbType.Text,
                (object?)normalizedFilter ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "search",
                NpgsqlDbType.Text,
                (object?)normalizedSearch ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "before_utc",
                NpgsqlDbType.TimestampTz,
                (object?)normalizedBeforeUtc ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "before_id",
                NpgsqlDbType.Uuid,
                (object?)beforeId ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "limit",
                NpgsqlDbType.Integer,
                Math.Clamp(limit, 1, 500));

            var entries = new List<OperationLogEntryDto>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                entries.Add(new OperationLogEntryDto(
                    reader.GetGuid(0),
                    reader.GetDateTime(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.GetString(8)));
            }

            return entries;
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync();
        }
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
