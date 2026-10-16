# N.E.K.O RimWorld protocol v2

The executable transport contract is Source/Protocol.cs and the plugin's
game/protocol.py. Transport uses a 4-byte unsigned big-endian length and UTF-8 JSON
on loopback. The Mod is the client; Python is the listener.

Mutual authentication uses hello/challenge/authenticate/welcome and HMAC-SHA256
over connection epoch and nonces in the neko-rimworld-bridge/v2 domain. Hello
advertises query.execute.v2 and native_behavior.v1; the query catalog carries
the detailed public and world contract versions.

## Operations

|Operation|Contract|
|---|---|
|query_catalog|Public query, selection and world contracts; live Verse adds behavior and implementation audit metadata|
|query / describe_query|public_query.v2 fields, conditions, collections, grouping and whole-selection aggregates|
|survey_space / inspect_route / inspect_state|world_observation.v2.3|
|inspect_actions / inspect_threats|native_behavior.v1|

Only this operation set is dispatched. Other names return unsupported_operation;
there is no compatibility execution route. Internal typed getter schema is also
used by paused capture and public-query diagnostics.

## Session and evidence

Session messages carry colony_id, load_epoch, tick/sample, request correlation
and strictly increasing per-direction sequence numbers. A request contains an
operation and arguments object. Results contain status, data, issues and failure
code/message. Domain readers own gameplay meanings.

Public results preserve request arguments, schema identity, phases, effective
scope, sampled ticks, and complete/partial facts. A display window does not change
the counted population. Missing and failed reads remain unknown. There is one raw
result structure without a separate reference-description tree.

World reads preserve actor and user IDs, requested StatDefs, target and environment
scope, coverage and sample intervals. Cell position is distinct from path endpoint.
Exact lookup and fog/held policies remain explicit. Reservation records on a target
do not imply the Pawn's reservations elsewhere or job eligibility.

Native route summaries report steps, path_length and native_cost, with native
endpoint normalization. Paths are disposed after reduction. Cost is not ETA and a
route is not an issued order. `objects:false` omits incidental object rows only;
environment and route evidence still run.

Three frame-operation domains share advancement, timing and idempotent release.
Public queries, world observations and behavior reads retain 8-second lifetimes
and 12ms cooperative budgets. Domain errors, sampling and completion remain local;
individual native getters/pathfinding cannot be hard-preempted.

The Def publisher produces immutable payloads and a validated ready.json index.
The plugin consumes that index directly. FrozenWorld advertises public-query
capabilities only; paused capture verifies public_query_parity. Historical records
and archived runners remain unchanged.

## Colony-state consultation reads

The additive `colony_state.v1` catalog declares inspect_planet, inspect_factions,
inspect_quests, inspect_trade, inspect_research, evaluate_journey and evaluate_trade.
Each operation supplies its executable argument schema. These native operations
support the plugin's look domains and two computations; they are not seven new
model tools. Existing public_query/world_observation/native_behavior contracts
retain their current meanings.

Responses retain selected scope, sample, pre-page population total and continuation.
World positions retain layer and tile identity; relations are to the player;
quest reward alternatives and deadline kinds remain distinct; research progress
and public prerequisites remain state-bound. Trader inventory is existing-only,
and quotation never opens a TradeSession, generates stock or executes a deal.
Journey calculation uses an existing player caravan and never issues orders.

The shared frame-operation lifecycle provides world invalidation, lifetime and
release. A synchronous domain/path calculation cannot be hard-preempted. Software
and provider-free pipeline checks establish contracts and wiring, not content
quality or generalized cost improvement. No user assembly deployment is implied.
