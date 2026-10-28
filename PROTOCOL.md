# N.E.K.O RimWorld protocol v2

The current product source version is **v3.2 / 3.2.0**. This label does not
change transport v2, public_query.v2, world_observation.v2.3, native_behavior.v1,
colony_state.v1 or situation_snapshot.v1, and does not imply publication or deployment.

The executable transport contract is Source/Protocol.cs and the plugin's
game/protocol.py. Transport uses a 4-byte unsigned big-endian length and UTF-8 JSON
on loopback. The Mod is the client; Python is the listener.

Mutual authentication uses hello/challenge/authenticate/welcome and HMAC-SHA256
over connection epoch and nonces in the neko-rimworld-bridge/v2 domain. Hello
advertises query.execute.v2, native_behavior.v1, situation_snapshot.v1 and colony_state.v1;
the query catalog carries
the detailed public and world contract versions.

## Operations

|Operation|Contract|
|---|---|
|query_catalog|Public query, selection and world contracts; live Verse adds behavior and implementation audit metadata|
|query / describe_query|public_query.v2 fields, conditions, collections, grouping and whole-selection aggregates|
|survey_space / inspect_route / inspect_state|world_observation.v2.3|
|inspect_actions / inspect_threats|native_behavior.v1|
|read_situation|situation_snapshot.v1; internal consultation background, separate catalog key and no model tool|
|inspect_planet / inspect_factions / inspect_quests / inspect_trade / inspect_research / inspect_resources|colony_state.v1; scoped native facts|
|evaluate_journey / evaluate_trade|colony_state.v1; native read-only calculations|

`read_situation` accepts only optional `map` (existing map binding, default current).
It returns sampled map/clock and native conditions, visible hostile groups,
cached alerts with observed culprit facts, actionable quests, and recent archive
events. Per-section availability and original facts remain explicit. Alert report
culprits are observed from the UI's existing native calculation; this read never
recalculates alerts. Projection and consultation snapshot reuse belong to the plugin.

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
inspect_quests, inspect_trade, inspect_research, inspect_resources, evaluate_journey
and evaluate_trade.
Each operation supplies its executable argument schema. These native operations
support the plugin's look domains and two computations, rather than one model tool
per native operation. Existing public_query/world_observation/native_behavior contracts
retain their current meanings.

Responses retain selected scope, sample, pre-page population total and continuation.
World positions retain layer and tile identity; relations are to the player;
quest reward alternatives and deadline kinds remain distinct; research progress
and public prerequisites remain state-bound. Trader inventory is existing-only,
and quotation never opens a TradeSession, generates stock or executes a deal.
Journey calculation uses an existing player caravan and never issues orders.

Resource observations separate spawned ground, player-pawn-held, seller-stock
and other-held quantities; physical presence does not establish ownership or
availability. Journey cargo_add/cargo_remove baskets return a separate hypothetical
cargo scenario alongside the current estimate without changing carried items.
Research may follow item production paths; a project, its requirements and active
research work remain separate facts. Quotes bind inventory and affordable quantity
to the selected trader, negotiator and channel. The executable catalog declares
which optional arguments the connected Mod supports.

The shared frame-operation lifecycle provides world invalidation, lifetime and
release. A synchronous domain/path calculation cannot be hard-preempted. Software
and provider-free pipeline checks establish contracts and wiring, not content
quality or generalized cost improvement. No user assembly deployment is implied.

## Extended pause and work fields (v3.3 B1)

`SituationSnapshot` clock now includes pause source fields: `paused` (native
Paused: speed_paused or force_paused), `speed_paused` (CurTimeSpeed==Paused),
`force_paused` (ForcePaused: windows/events/tile-picker/cutscenes),
`windows_force_paused` (WindowStack.WindowsForcePause), `automatic_pause_mode`
(PrefsData setting), and optional `force_pause_window` (first forcePause window type).
Letter auto-pause sets speed to paused; state alone cannot distinguish it from user pause.

Event items now carry `auto_pause_mode` (LetterDef.pauseMode enum string or null for
messages) and `pauses_game` (boolean or null: whether this letter's pauseMode would
trigger auto-pause given the current AutomaticPauseMode pref).

Work facet includes `use_work_priorities` (Game.playSettings.useWorkPriorities) and
meaning: when false, GetPriority returns 3 for humanlike pawns and priority writes
have no effect.

Production facet now includes per-bill `bill_id` (GetUniqueLoadID) and
`recipe_available_now` (RecipeDef.AvailableNow), and for Building_WorkTable owners
`available_recipes` (array of {recipe, research_prerequisites_met, available_now})
listing loaded recipe/workbench associations with research gates evaluated.
Native `AvailableNow` also evaluates ideology and faction gates. To avoid inserting
missing research progress, it is called only when every prerequisite dictionary key
exists; an incomplete prerequisite yields false, while an absent key whose zero
progress still meets cost yields null (unknown).


## Write contract staging (v3.3)

`colony_write.v1` is a reserved contract identifier, not an advertised capability
until a native executor and required transaction guarantees are implemented.
Authenticated `write_request` uses `mode` and `arguments`, and carries the usual
session and request identity. `dry_run` and `commit` carry a changeset directly;
`undo` takes `token`, and `set_paused` takes `paused` and `role`. The current Mod
responds with correlated `write_result`: `unsupported`, or `stale_session` with
`stale_epoch` when the envelope session is stale. No operation is performed.
The operation-specific target and value grammar remains for the native operation
catalog; the staging parser validates only the common bounded structure.

The work facet also exposes `allowed_area_key` as `{map_id, area_id}` using native
integer `Area.ID`. The existing `allowed_area` remains the native load ID.

The native `pauseMode` is an automatic-pause threshold, not a general negative-event
classification. Core `NegativeEvent` and `PositiveEvent` share `AnyLetter`;
the existing `def` identifies those exact definitions, and modded definitions
remain unclassified. Messages have no letter pause threshold. The current
preference describes eligibility now, not the historical cause of a pause.

Successful wire results carry the game-port receipt directly in `data`: pause
returns `{paused, role}`; undo can return `unknown_token`, `already_undone`, or
`stale_epoch` with its current `load_epoch`. Conflict `last_writer` and `at_tick`
may be null when provenance is unknown. These shapes do not imply a native
executor or undo ledger exists yet.

Preview rows contain exactly `op`, `object`, `field`, `expect`, and `value`;
readback rows contain exactly `object`, `field`, and `value`. Values may be JSON
null. Arrays may be empty: the wire contract does not assert how many physical
fields a future executor expands from an operation, and the port already permits
empty arrays. This is not a claim that any operation was executed.
