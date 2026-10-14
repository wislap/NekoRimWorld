# N.E.K.O RimWorld Bridge

Read-only RimWorld 1.6 bridge for the companion N.E.K.O plugin. The production
wire protocol is v2; the current read contracts are public_query.v2,
world_observation.v2.3, spatial_query.v1, and native_behavior.v1.

The plugin presents public reads as look / count and exposes native
observations, spatial surveys, activity checks and Def knowledge through its
semantic consultation. The Mod reads on the game thread and does not issue
player orders.

## Supported operations

query_catalog, query, describe_query, spatial_query, survey_space,
inspect_route, inspect_state, inspect_actions, and inspect_threats.

Public queries use canonical schema-derived fields, related collections, conditions,
whole-selection counts and stack-unit sums. Conditions run before display paging.
Current queries use explicit limit / offset; every continuation is a new
observation. Result scope, sampling, issues and completeness retain native
meaning. Unknown reads never become an exact zero.

GraphQL documents, template requests, native schema discovery and dedicated
resource reports are retired production operations. They return
unsupported_operation without game reads. The catalog has no templates, SDL,
legacy argument schemas or resource capability. The internal GraphQL type and
field schema remains the shared substrate for current getters and conditions.

The local Def publisher writes an immutable payload plus its validated ready.json
index. It does not publish a current.json compatibility copy. The plugin reads
only this ready-index contract.

## Source responsibilities

- Protocol, authentication and BridgeRuntime: framed loopback transport.
- NekoGameComponent: game-thread request queue and frame-operation lifecycle.
- GameQueryEngine, PublicQuery, PublicFields, SelectionConditions: one
  public vocabulary over typed native fields.
- VerseGameData, ThingData, PawnDetailsData: explicit native getters and
  holder traversal.
- WorldObservation, spatial and native behavior readers: scoped on-demand reads.
- KnowledgeStreamingPublisher: bounded offline Def publication.

Capture and FrozenWorld adapters live in rimworld-bench. Capture consumes typed
native field declarations directly and verifies current public-query parity.
FrozenWorld advertises public queries only; it cannot supply live world/spatial
observation contracts.

## Build and validation

Compile-time target: RimWorld 1.6.4871 / .NET Framework 4.7.2. The source project
normally outputs to 1.6/Assemblies; verification builds must override output and
intermediate directories to avoid deploying a DLL. A running game retains its
loaded assembly; activate changes only by a normal restart.

Tests/BridgeSmoke runs deterministic protocol, query, selection, enum,
knowledge and observation tests under net472 and net8.0. These checks do not
certify game compatibility or model answer quality. See [PROTOCOL.md](PROTOCOL.md)
for the current wire boundaries.

## License

[MIT](LICENSE). Third-party dependencies retain their respective licenses.

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

A found `route` reports `steps` (grid transitions), `grid_distance` (sum of cardinal
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

Path summaries also publish `path_length` with explicit along-path semantics.
`grid_distance` remains an equal-valued wire compatibility alias. The plugin
shows one canonical length to the Agent; no path search or distance arithmetic
changed in this refinement.

## Exact IDs and spatial positions

Public queries can push exact top-level id.eq/in into Thing candidate enumeration, retaining all original scope filters and predicate checks. Native deadlines still apply. Spatial Thing descriptors now include the spawned instance position (null when unspawned), separate from the path endpoint. Health/trait field descriptions document per-game-day bleeding and trait variant codes.

## V2.2 observation operations

The catalog exposes survey_space, inspect_route and inspect_state through
world_observation.v2.3. Reads run on the game thread through the frame-operation
pump, share native state/environment projections, and keep scoped completeness
and sampled tick intervals explicit. Existing spatial_query remains compatible.
No action or reservation is performed. Detached cursor results expire after two
minutes and are cleared on load changes. Native synchronous getter/pathfinder
overruns are not claimed to be hard-preemptible. Test-only game construction and
UUID evaluation hosting live in rimworld-bench's capture Mod, not this Mod.


### V2.2.1 redo: scoped observations and compact support references

The first V2.2.1 relationship patch was reverted. The redo keeps the V2.2 tool
names and arguments. `objects_summary.scope` states the exact object selection:
its zero counts cannot establish absence outside that selection. Environment
scope remains the observed footprint independently of the object filter.
`context` reports resolved actor/user IDs and explicitly requested StatDefs.

Reservation `scope.direction=reservations_on_target` distinguishes records on
this object (or cell) from reservations claimed by a Pawn elsewhere. Destination
cells and job targets remain separate. The work facet explains current native
work-type disablement and assignment priority without inferring job eligibility.

`environment.roof_support_objects` now contains compact identity/location
references in both detail modes. Only runtime type, holder type/chain and
capability metadata are omitted there; IDs, locations, footprint, faction,
membership and any other fields remain. Totals, row limits, coverage and hazards
are unchanged. `inspect_state` still returns full state and containment metadata.
Formatting happens before native page fitting and output-size checks. No Pawn
health/work lists are pruned and no model-facing entity dictionary is introduced.

The separately evaluated output-policy rewrite was not promoted. Benchmark
results and limitations are documented in the archived rimworld-bench-archive/docs-20261013/bench/planning/V221-REDO.md.

### V2.3 object-reader support

Native `inspect_state` accepts optional `sections` (`identity`, `health`, `skills`,
`gear`, `work`, `state`), `detail_limit`, `include_fogged`, and `exact_lookup`.
Omitted options retain legacy state and visible-only behavior. Exact lookup is an
adapter contract: case-sensitive Pawn.LabelShort / Thing.Label matching, loaded-map
search when map is absent, with context and cell current-map defaults unchanged.
Sections gate reads before facet evaluation; reservation snapshots are initialized
only when requested. Pawn body parts, real skills, work assignments and native
StatDefs preserve totals, bounds and unknowns.

The same v2.3 catalog now advertises optional `inspect_state.facets` for named
physical/device facets such as `bed`, `reservations`, `door`, and `condition`.
It requires the `state` section when sections are explicit and filters native
phases before their getters execute. Health/skills/gear/work still use their
own sections. Omitted facets preserve the existing full-state behavior.
Selected scope is recorded; a facet unsupported by the target is explicitly
`not_applicable`, while failed or unavailable reads retain their failure state.
Cell requests apply supported environment/reservation facets and identify other
requested facets as inapplicable. This adds no native operation.

The `things` root accepts native `capabilities`, shared with world observation and
applied before paging/counting. The scalar capability field is explicit-selection
only. This supports discovery without environmental scans or vanilla definition
lists. The modern semantic Agent exposes this through look/count, with state merged
into look; the underlying native operations remain compatible.

## Contract world_observation.v2.3: on-demand object detail

`survey_space` and `inspect_route` accept an optional boolean `objects`
(default `true`). With `objects:false` the native read skips generating the
incidental object listing, the per-object spatial rows and their paging;
`objects_summary` reports `status:not_requested` with the explicit meaning
that not-requested objects are not evidence of absence. Environment, gases,
hazards, coverage, area/zone intersections and route segments (including door
reads) are internal forensics of every read and are never suppressed by this
flag — output selection and necessary evidence collection stay separate.
`inspect_state` targets do not accept the flag. The catalog advertises
`world_observation.v2.3`; the parameter is additive, so requests without it
keep the previous complete behavior byte-for-byte.
