# N.E.K.O RimWorld Bridge

Read-only RimWorld 1.6 bridge for the N.E.K.O plugin. The v2 transport supports
public_query.v2, world_observation.v2.3 and native_behavior.v1. Reads run on the
game thread; the bridge does not issue player orders.

## Current reads

`query_catalog`, `query`, `describe_query`, `survey_space`, `inspect_route`,
`inspect_state`, `inspect_actions` and `inspect_threats` are the complete operation set.

Public queries use schema-derived fields, related collections, conditions,
whole-selection counts and stack-unit sums. Conditions run before display paging.
Limit/offset windows remain new observations; scoped completeness and unknowns
are explicit. The internal GraphQL schema supplies typed native getters, rather
than another public execution API.

World observations share native state and environment projections. Object state
supports named sections/facets, exact IDs/names and visible/held scope. Actor and
user remain distinct contexts. Surveys and routes can omit incidental object rows
with `objects:false`; environment, gases, hazards, coverage and route segments
remain collected. A missing object listing never establishes absence.

`inspect_route` uses the actor's native pathfinder parameters and returns bounded
path geometry, native weighted cost and environment evidence. `path_length` is
along-path cell width, not straight-line distance or travel time. The route is
never issued to a Pawn. Failed native reads retain unknown/partial meaning.

Behavior inspections preserve their action/target scope and native implementation
audit. An unsupported binary or patched dependency does not become eligibility.
The canonical capability `bill_giver` identifies native IBillGiver; production
bills remain a state facet.

## Source ownership

- Protocol and BridgeRuntime own framed authenticated loopback transport.
- NekoGameComponent owns the queue and active frame-operation lifecycle.
- GameQueryEngine/PublicQuery/SelectionConditions own the public query vocabulary.
- VerseGameData/ThingData/PawnDetailsData own typed native getters and holders.
- WorldObservation owns scoped state, survey and route reads; GameWorldReferences
  owns loaded-map references and cell coordinates.
- NativeBehaviorOperation owns behavior sampling and domain results.
- KnowledgeStreamingPublisher publishes immutable Def payloads and ready.json.

Public queries, world observations and behavior inspections share the iterator,
timing and release skeleton. Each retains an 8-second lifetime, 12ms cooperative
read budget, domain sampling and partial/error outcome. Synchronous native calls
cannot be hard-preempted.

## Build and validation

The target is RimWorld 1.6.4871 / .NET Framework 4.7.2. Verification builds must
override output paths so they do not deploy into `1.6/Assemblies`. When overriding
BaseIntermediateOutputPath, also exclude the existing `**/obj/**` tree from Compile
items. A running game keeps its loaded assembly until a normal restart.

Tests/BridgeSmoke covers deterministic protocol, query, enum, knowledge,
observation, route reduction and lifecycle behavior under net472 and net8.0.
These checks do not establish actual gameplay or answer quality.

The bench capture Mod exports paused native snapshots and verifies public-query
parity. Scenario control/fixture types belong to the bench consultation setup.
FrozenWorld supports public-query diagnostics only. Retired execution and capture
implementations live in the workspace archive; current code does not load them.

See [PROTOCOL.md](PROTOCOL.md) for the wire contract. [MIT](LICENSE).
