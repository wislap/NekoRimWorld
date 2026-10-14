# N.E.K.O RimWorld protocol v2

The executable wire contract is Source/Protocol.cs and the plugin's
game/protocol.py. [DATA_PATH.md](../N.E.K.O/plugin/plugins/neko_rimworld/DATA_PATH.md)
describes ownership, limits and supported read paths.

Transport uses a 4-byte unsigned big-endian length and UTF-8 JSON on loopback.
The Mod is the client; Python is the listener. Mutual authentication uses
hello/challenge/authenticate/welcome and HMAC-SHA256 over connection epoch and both
nonces in the neko-rimworld-bridge/v2 domain. Hello advertises query.execute.v2,
spatial_query.v1, and native_behavior.v1.

Session messages retain save colony_id, current load_epoch, tick/sample,
request correlation and strictly increasing per-direction sequence numbers.
Only the v2 wire shape is accepted. A query request contains an operation and
arguments object; a result contains status, optional data, issues and failure
code/message. Gameplay DTO meanings belong to the readers.

## Supported contracts and operations

- query_catalog: current public, selection, spatial and world contracts;
  the live Verse executor adds native behavior and game-implementation audit metadata.
- query / describe_query: public_query.v2, canonical fields, conditions,
  related collections, grouping and whole-selection aggregates.
- spatial_query: spatial_query.v1.
- survey_space, inspect_route, inspect_state: world_observation.v2.3.
- inspect_actions, inspect_threats: native_behavior.v1.

The current catalog has no SDL, template list/parameters, GraphQL document
parameters, resource-report contract or legacy result-description declaration.
graphql, describe_schema, template_query, resource_report and
colony_snapshot return unsupported_operation without native reads.

The shared typed field schema remains internal: it supplies current public
getter/condition semantics and the in-process capture adapter. Public collections
use limit/offset windows, never old root keyset cursors. A window does not change
the population counted; missing/failed fields remain unknown with scoped issues.

Current public responses contain no execution.evidence descriptor tree or
reference contract. Request arguments, schema identity, execution phases, effective
scope, sampled ticks and complete/partial facts remain in raw results and audit.
Earlier stored envelopes are not rewritten.

FrozenWorld is an offline public-query host. Its catalog omits live world/spatial
contracts and uses a consistent host-specific schema identity. Capture verifies
only current public_query_parity and retains original native snapshot field keys;
historical snapshots and runners remain sealed.

## V2.1 identity and scope repair

Spatial name/ID lookup now runs within one request over cooperative main-thread
slices (2 ms yield target, 12 ms slice guard, 2-second total request lifetime).
Individual native getters and membership capture cannot be preempted; these are
not hard frame-time guarantees. Each reference captures up to 100000 candidate
identities at its lookup start; held membership remains bounded and cycle-aware.
No persistent index or background Verse access is added. Lookup start/end ticks
and membership semantics are returned; reachability is checked on the resolved
objects at the final tick. Objects created after membership capture are outside
that lookup. The map is bound once at request start. Unknown remains unknown.

The plugin accepts `map` for Pawn reads and supports `id` as the singular alias
of `ids`; the map constraint is carried through lookup and final read. The look
schema advertises per-kind aspect combinations. Failed definition/name resolution
reports lookup source/scope/coverage separately: the requested instance read or
count is **not_run**, never a complete zero count. A complete missing exact-ID
public query is an empty result; an interrupted lookup remains partial.

## V2.1-B optional native path summary

The retained Mod operation `spatial_query` accepts a `from` Pawn reference, a `to`
Thing or map-cell reference, and optional `detail` (`summary` or `route`). Omitting
`detail` or using `summary` checks native reachability only. With `route`, a
reachable target also runs at most one native `FindPathNow` in the same request.
The catalog advertises `spatial_query.route_summary = native_path_summary.v1`.
The default plugin uses `evaluate_activity` for activity checks and does not
expose `inspect_space` as a model tool. Native reachability does not establish
reservation success, work eligibility or arrival time.

A found `route` reports `steps` (grid transitions), `path_length` (sum of cardinal
1 and diagonal sqrt(2) cell widths), `native_cost` (native weighted search cost),
and start/end map cells. The native GenPath endpoint normalization is applied
before pathfinding: a Thing's returned endpoint need not be its occupied cell.
The path is not guaranteed shortest; cost is not seconds, elapsed ticks or ETA.
Paths are never issued to a Pawn and are disposed after reduction; no full node
array or persistent path handle reaches the Agent.

The route status is `found`, `not_computed` (lookup/unsupported/unreachable), or
`unavailable` (pathfinding/projection failure). A failed route leaves a completed
reachability result usable. The synchronous game call cannot be preempted; a
completed valid summary that exceeds the cooperative 12ms target is preserved
with `route_budget_overrun` and `read_budget_exceeded`, not discarded or retried.
Node reduction remains bounded. This is not a hard frame-time or travel-time
guarantee, nor a diagnosis of which obstacle caused a negative reachability result.

## Current read lifecycle and capabilities

Four frame operations share one iterator-advance, timing and idempotent-release
implementation. Spatial requests retain a 2-second lifetime and an explicit yield
before route evaluation; public queries, world observations and behavior inspections
retain 8-second lifetimes. Each domain owns its world/map checks, completion,
partial/error outcome and sampling semantics. Native read budgets are unchanged.

The canonical capability is bill_giver (native IBillGiver); the duplicate
production capability is retired. The production state facet still reads native
bills and distinguishes manufacturing from surgery; removing the capability alias
does not remove that facet. Public observations have one raw result structure,
without a second reference-descriptor tree.
