# N.E.K.O RimWorld Bridge v3.2

Product version: **3.2.0**. This is the current local source version, not a
published or deployed release. v3.2 includes the selected native situation
background, world/faction/quest/trade/research consultation, and the exact-read,
resource-source and loaded-journey improvements. Product and protocol versions
are independent; the supported wire contracts below retain their meanings.

Read-only RimWorld 1.6 bridge for the N.E.K.O plugin. The v2 transport supports
public_query.v2, world_observation.v2.3, native_behavior.v1 and the internal
situation_snapshot.v1 consultation background. Reads run on the
game thread; the bridge does not issue player orders.

## Current reads

`query_catalog`, `query`, `describe_query`, `survey_space`, `inspect_route`,
`inspect_state`, `inspect_actions`, `inspect_threats` and `read_situation` form the base operation set.

The additive `colony_state.v1` contract supplies world places and existing caravans,
faction relations, public task requirements/rewards/deadlines, existing trader
inventory and research progress/prerequisites. Native `evaluate_journey` compares
requested destinations for an existing player caravan; `evaluate_trade` quotes
an explicit trader/negotiator buy/sell basket. Neither issues orders. Ordinary
reads are exposed through the plugin's look vocabulary, without a discovery step
or automatic five-domain background. Missing settlement stock is never generated
for a read, and quotation never opens or changes a TradeSession.

`inspect_resources` reads selected item definitions (for example,
`{ "map":"current", "items":["ComponentIndustrial"] }`); the plugin exposes this as
`look` kind `resources` with `focus.items`. Its quantity/distribution views split
spawned ground, player-pawn-held, trader-stock and other-held units. They retain
unknown ownership instead of treating every map-held item as player supply. General count remains a physical collection count; merchant stock
is still seller inventory and quest reward offers are not delivered inventory.

`evaluate_journey` optionally accepts `cargo_add`/`cargo_remove` baskets such as
`[{"item":"WoodLog","quantity":40}]`. Rows keep the existing current-cargo estimate
and add `cargo_scenario` for the same party with changed numeric mass. The native
movement and arrival estimates include load-related speed changes and night rest;
overloaded scenarios have no loaded ETA. Added items use native definition Mass
and must have a fixed material; removals use actual carried stacks and cannot
exceed their quantity. No loading is performed or assumed. Supplies still describe
the unchanged current inventory/current-speed food estimate; obtaining/loading
time and changed-food/foraging simulation are outside this calculation.

`inspect_research` accepts `items:["ComponentIndustrial"]` instead of project
names/IDs. It follows loaded product recipes, workbenches and research references,
then returns the existing progress/requirements views plus `production_paths`.
Research requirements distinguish existing benches, power and active linked
facilities; an empty compatible combination does not imply the bench is absent.
Faction settlement previews are bounded and link to the paged world-object reader.
Single-item silver purchase quotes also return an affordable subset of the quoted
lot; currency remains bound to that trader/negotiator channel, not total wealth.

`read_situation` supplies native situation facts before consultation: current map
and clock, conditions, visible hostile groups, cached alerts and associated objects,
actionable quests, and recent letters/messages. It is advertised separately from
world tools and is never a model-callable tool. The plugin owns compact prompt
projection and reuse. Ordinary queries, world observations and action reads retain
their existing contracts. Native alert calculations are observed without rerunning
them or keeping their game-object graphs.

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
- SituationSnapshot owns internal situation facts; SituationAlertObservations
  observes detached facts from native alert calculations.
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

The completed original-30 + standard-45 regression compares the recent exact-read,
resource-source and loaded-journey improvements before/after; both arms already
contain the five consultation domains and the selected situation background.
Public input decreased 12.20% in that controlled comparison, with completion
74/75 -> 73/75 and remaining answer issues. This is not a whole-v3.2 cost estimate,
a claim of universal quality improvement, or a reason to deploy automatically.
See the workspace [regression report](../.bench-results/capability-regression75-20261016-v2/RESULTS.md)
and [implementation evidence](../.bench-results/consultation-decisions-20261016-v1/RESULTS.md).

The bench capture Mod exports paused native snapshots and verifies public-query
parity. Scenario control/fixture types belong to the bench consultation setup.
FrozenWorld supports public-query diagnostics only. Retired execution and capture
implementations live in the workspace archive; current code does not load them.

See [PROTOCOL.md](PROTOCOL.md) for the wire contract. [MIT](LICENSE).
