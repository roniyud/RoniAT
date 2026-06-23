using System.Text.Json.Serialization;
using RoniAT.TradingEngine.Models;

namespace RoniAT.TradingEngine.Contracts;

public sealed record AuditLogResponse(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("details")] string Details,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt
)
{
    public static AuditLogResponse FromRecord(AuditLogRecord record)
    {
        return new AuditLogResponse(record.Id, record.Action, record.Details, record.CreatedAt);
    }
}
