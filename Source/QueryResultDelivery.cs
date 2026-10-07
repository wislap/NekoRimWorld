namespace Neko.RimWorld;

// Validate construction as well as serialization. A malformed outcome must
// produce a correlated failure, not throw before the queue can advance.
internal static class QueryResultDelivery
{
    internal static ProtocolEnvelope Build(ProtocolEnvelope request, SessionDescriptor session, QueryOutcome outcome)
    {
        try {
            var response = ProtocolEnvelope.CreateQueryResult(request, request.ConnectionEpoch, 0,
                session, outcome.Status, outcome.Data, outcome.Issues, outcome.ErrorCode, outcome.Message, outcome.Execution);
            ProtocolCodec.Serialize(response);
            return response;
        } catch (ProtocolException error) {
            return ProtocolEnvelope.CreateQueryResult(request, request.ConnectionEpoch, 0,
                session, ProtocolStatuses.Failed, errorCode: error.ErrorCode,
                message: "The query result exceeded transport limits or contained invalid data.");
        }
    }
}
