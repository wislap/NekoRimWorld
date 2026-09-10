using System;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal sealed class QueryOutcome
{
    public string Status { get; set; } = ProtocolStatuses.Ok;
    public JObject? Data { get; set; }
    public JObject? Execution { get; set; }
    public QueryIssue[] Issues { get; set; } = Array.Empty<QueryIssue>();
    public string? ErrorCode { get; set; }
    public string? Message { get; set; }
    public static QueryOutcome Failure(string code, string message, string status = ProtocolStatuses.Failed) =>
        new() { Status = status, ErrorCode = code, Message = message };
}
