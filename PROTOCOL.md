# N.E.K.O RimWorld protocol v2

The current product source version is **v3.4 / 3.4.0**. This label does not
change transport v2, public_query.v2, world_observation.v2.3, native_behavior.v1,
colony_state.v1 or situation_snapshot.v1, and does not imply publication or deployment.

The executable transport contract is Source/Protocol.cs and the plugin's
game/protocol.py. Transport uses a 4-byte unsigned big-endian length and UTF-8 JSON
on loopback. The Mod is the client; Python is the listener.

Mutual authentication uses hello/challenge/authenticate/welcome and HMAC-SHA256
over connection epoch and nonces in the neko-rimworld-bridge/v2 domain. Hello
advertises query.execute.v2, native_behavior.v1, situation_snapshot.v1, colony_state.v1 and colony_write.transactions.v1;
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
|read_colony_vitals|colony_vitals.v1; internal planner background, catalog key `colony_vitals`, no model tool and not advertised in hello|
|inspect_planet / inspect_factions / inspect_quests / inspect_trade / inspect_research / inspect_resources|colony_state.v1; scoped native facts|
|evaluate_journey / evaluate_trade|colony_state.v1; native read-only calculations|

`read_situation` accepts only optional `map` (existing map binding, default current).
It returns sampled map/clock and native conditions, visible hostile groups,
cached alerts with observed culprit facts, actionable quests, and recent archive
events. Per-section availability and original facts remain explicit. Alert report
culprits are observed from the UI's existing native calculation; this read never
recalculates alerts. Projection and consultation snapshot reuse belong to the plugin.

`read_colony_vitals` accepts only optional `map`. It returns one sample of four
domains (`life`, `food`, `shelter`, `labor`) for the native FreeColonistsSpawned
population, each with `status`, `numbers`, `method` (the native source and
caliber of every number), and at most 8 `worst` rows plus `worst_omitted`. It
reports facts only: levels, thresholds, freshness and write suggestions belong to
the plugin. A failed domain is `unavailable` with its error; the others remain.

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

The existing `session_snapshot` and `pause_changed` event data, and the existing
`read_situation` clock, add a `pause_state` object from the same native pause
policy. `status=observed` includes `connection_epoch`, `colony_id`, `load_epoch`,
`sampled_tick`, `owner`, `pause_revision`, `pause_transition_revision`, `paused`,
`speed_paused`, `force_paused`, and `windows_force_paused`. The owner can be
`unknown`, including after a connection or load change. `status=unavailable`
contains no claimed ownership when hooks, state or binding cannot be observed.
Scope and sampled tick identify the evidence; a later pause transition can make it
stale. This adds no query, control action or recovery permission. Existing
`set_paused` request and result shapes remain unchanged.

Native pause evidence in `session_snapshot`, `pause_changed` and `heartbeat`,
and private realtime-control receipts, may include `monotonic` with `domain`,
`frequency` and `counter`. The counter is sampled from the native process
monotonic clock; domain identifies that process instance and frequency gives ticks
per second. Compare counters only within the same domain and frequency. The
realtime timing window runs from the existing declaration receipt to the native
terminal receipt; parent-process mode-close elapsed time is a separate measure.
The evidence describes wall time in observed pause states, not CPU simulation time.
It is omitted from model-facing situation and event presentations and grants no
control or recovery permission. Older records may omit it.

Current pause events, ownership snapshots and situation clocks also include optional
`pause_windows` evidence. The existing public `game` query exposes the same reader
as `pause_windows.*` (native GraphQL `Game.pauseWindows`), so callers can request a
fresh sample instead of reusing a situation background. Evidence contains `status`,
`sampled_tick`, total `count`, `truncated`, and at most 16 window rows: runtime
`window_id`, `type`, `stack_index`, `opened_tick`, and `opening_source` with status
and at most eight bounded managed method frames. Identity/source records bind the
actual Game, WindowStack, window object and native ID; IDs are not persistent or
cross-load identities. Opening frames describe the observed managed call path,
not a person, user intent or permission to select/close a window. An existing
window without a captured opening has unknown source. Unavailable stacks report
unknown count, never an observed zero; partial/truncated samples remain partial.
Older producers may omit this additive field. The optional opening hook does not
change existing pause-hook readiness or resume guards. No window-control action
is added. Cached situation fields retain their original sample tick and must not
be treated as a current pause observation.

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


## Transactions and independent controls (v3.3 source)

`colony_write.transactions.v1` advertises the dry_run, commit and undo subset of
`colony_write.v1`. Pause and event support use separate capabilities. Native
`query_catalog.colony_write` describes the registered operations and evidence
extraction, with `catalog_id` as SHA-256 of its compact JSON before that field is
added. The existing public query schema_id is unchanged. Query-only engines do
not advertise the native write catalog.
Authenticated `write_request` uses `mode` and `arguments`, and carries the usual
session and request identity. `dry_run` and `commit` carry a changeset directly;
`undo` takes `token`, and `set_paused` takes `paused` and `role`. A current
game session with a valid ledger accepts the first three modes through the
authenticated queue. `colony_pause.v1` independently enables `set_paused`, and
`colony_events.v1` enables explicit event subscription and policy requests. Both
require the audited native binary and available native event hooks; invalid
field-ledger data disables transactions without disabling pause control.
The read-only frontend does not register write tools. Stale sessions are rejected and a full queue returns `busy`.
The operation-specific target and value grammar remains for the native operation
catalog; the staging parser validates only the common bounded structure.

The work facet also exposes `allowed_area_key` as `{map_id, area_id}` using native
integer `Area.ID`. The existing `allowed_area` remains the native load ID.
Allowed-area assignment uses the native setter while the pawn has an active job;
there is no job-idle prerequisite. Restricting an area may cause RimWorld to
interrupt the current job. Rollback and undo restore the configured area field,
not that interrupted job or its progress. The existing target/map, evidence and
compare-before-write checks still apply.

`write_allowed_area_key` reads the raw restriction used by writes and is absent
when player settings or map are unavailable. `inspect_research.current_regular_project`
is an explicit nullable ordinary project ID. `inspect_storage` selects one player
storage by ID and map and reads its priority and requested definition acceptance;
missing definitions or unavailable targets are errors, not negative observations.

The native `pauseMode` is an automatic-pause threshold, not a general negative-event
classification. Core `NegativeEvent` and `PositiveEvent` share `AnyLetter`;
the existing `def` identifies those exact definitions, and modded definitions
remain unclassified. Messages have no letter pause threshold. The current
preference describes eligibility now, not the historical cause of a pause.

Successful wire results carry the game-port receipt directly in `data`: pause
returns `{paused, role}`; undo can return `unknown_token`, `already_undone`, or
`stale_epoch` with its current `load_epoch`. Conflict `last_writer` and `at_tick`
may be null when provenance is unknown.

Preview rows contain exactly `op`, `object`, `field`, `expect`, and `value`;
readback rows contain exactly `object`, `field`, and `value`. Values may be JSON
null. Arrays may be empty: the wire contract does not assert how many physical
fields a future executor expands from an operation, and the port already permits
empty arrays. This is not a claim that any operation was executed.

The transaction core implements one synchronous `Advance`, resolves
all fields and validates their expected values before writing, and restores
written fields in reverse order on a setter, readback, or metadata publication
failure. The existing game-thread frame scheduler runs the coordinated write.
The ledger keeps each field's latest AI value, author, changeset and tick in the
save; receipt and undo state is confined to the current load epoch. Undo first
compares all fields against the values it wrote. Save-scoped identity references
allow confirmed destroyed objects to be pruned after loading as well as before
saving or handling a request; unresolved references and off-map objects are not
assumed destroyed. Old saves without these references retain unknown entries.
A malformed saved ledger disables
writing and preserves the original stored data.

The internal Verse directory covers work priority, medical care, allowed area,
forbidden flags, existing production-bill fields, ordinary research, and storage
priority / individual item permission. Bill edits expand into distinct fields.
Native writes require the Unity main thread, Playing state and no long event.
Work settings must already be initialized. Priority changes involving zero are
rejected when a current or queued job could be affected. Area changes have no
job-idle prerequisite and use the native interruption behavior described above.
Item filters reject nonofficial definitions, virtual descendants and items
excluded by the parent's filter. These other operation-specific restrictions
retain their existing scope.

The registered schema also supports creating ordinary production bills on the
supported native worktable classes. Creation has its own ordered bill-list
precondition; undo additionally guards the created bill's configuration and
position. Special bill types remain unsupported.

`colony_pause.policy.v1` adds the code-owned failure policy role to `set_paused`.
It accepts only `{paused: true, role: "policy"}`; policy cannot resume the game.
An actual running-to-paused transition has source `policy`. A no-op pause retains
the existing owner, including user or unknown ownership. Planner resume preserves
the existing user/unknown/forced-pause protections. Pause never enters field
changesets or the saved field ledger, and never selects a running speed.

Smoke tests establish contract and coordinator behavior. Separate isolated native
traces exercise selected real setters, save roundtrips and failure policy pause;
they do not establish complete fault coverage, frame time or autonomous gameplay
quality. These capabilities alone do not enable automatic play.

### Colony attention facts (`colony_attention.v1`)

This capability is advertised independently only when the native event hooks and alert report observation hook are available, with `colony_events.v1`; delivery uses the existing event subscription and `game_event` envelope. A new connection subscription or load epoch starts with `attention_snapshot`; changing the selected map also replaces the baseline. No model-side subscription or pause-policy field is added. Letters retain the existing `letter` event. Events use the existing cursor: a cursor gap is a loss of evidence, not permission to assume missing changes did not occur.

The existing `read_situation` response also contains `data.attention_snapshot`,
assembled from that same read's three sections without another native scan. It
has `kind=attention_snapshot`, `sample_tick` equal to the read's finished tick,
`map_id`, the same bounded partitions, and a `sample` copy of the parent read's
scope and non-atomic interval. Each partition additionally retains its own
`sample_tick` and `finished_tick`. A missing or interrupted partition is
`unavailable` or `partial`; this projection does not publish a game event or
imply an atomic snapshot. Existing consumers may omit the additional field.

Each snapshot contains `sample_tick`, `map_id`, and `alerts`, `conditions`, `threats` partitions. Each partition has `status` (`ok`, `partial`, or `unavailable`), `source`, `rows`, and `truncated` when the row limit is reached. Alert rows contain epoch-scoped observer `id`, native `runtime_type`, `label`, `priority`, `native_computed_tick`, `targets` and `targets_complete`. Condition rows contain native instance `id`, `def`, `scope`, `map_id`, `start_tick`, `duration_ticks`, and `permanent`. Threat rows contain native group or target `id`, `kind`, faction, visible count, map and member identities when available. Alert targets carry identity and map; cell targets carry map and coordinates. Alert cache scope is global, including other maps. Threats are visible spawned members of the current map's native player-hostile target cache, not all enemies or a prediction of attack.

Subsequent `attention_changed` events contain `sample_tick`, `map_id`, `category`, `source_id`, `edge`, `previous`, and `current`. Edges are `appeared`, `disappeared`, `priority_changed`, `targets_changed`, and `status_changed`; target changes additionally include `added` and `removed`. Conditions and threats use appearance and disappearance edges. A partial or unavailable partition does not establish absence; a target removal requires a complete target set. When a complete observation resumes, it is compared with retained confirmed facts. Unknown priority is not treated as a priority change. At most 32 rows per partition and 16 targets per row are projected, with strings capped at 128 characters. An overlength identity is omitted and marks the partition partial; other text truncation is marked on the row. A partially published sample retains its progress and retries only the first unsent event; the sample baseline advances after all its events are queued. The authoritative envelope examples are `Tests/fixtures/colony-attention-v1/vectors.json`.

`read_situation` native data includes `attention_sampling_metrics`, a per-load-epoch
aggregate of due samples and separate pending-publication retries. Each part reports
count, failure count, total and maximum Stopwatch milliseconds. `sample` covers
section reads, projection, difference and initial delivery; the section and
`difference_delivery` measurements are overlapping subparts, not additive totals.
A failed sample includes incomplete/unavailable sections, rejected publication or
an exception. Pending retries are counted separately. Alert reads use the native
cache and exclude the native `GetReport` computation; the existing alerts
`observer_metrics` separately measures detached report observation overhead.
This diagnostic is not a model-facing situation field and adds no events.

### Colony management (`colony_management.v1`)

The additional `query_catalog.colony_management` directory describes supported
business reads. It leaves the fixed `colony_state.v1` directory unchanged.
The native directory is authoritative for argument schemas; consumers must not
invent native field paths or infer support from a product version string.

| Operation | Native evidence |
| --- | --- |
| `inspect_production` | Selected worktables, complete standard production settings and filters, recipe requirements, standard unfinished work and native target counters |
| `inspect_food_policies` | Native policy identities, complete content, effective assignments, retained references and full affected-user sets |
| `construction_catalog` | Loaded definitions, rotated-size inputs, costs, research, materials, room boundaries, interaction offsets and physical layers |
| `inspect_construction` | A visible-cell grid of at most 64 by 64 cells, terrain layers, roof, walkability, areas, zones and full intersecting object footprints |
| `preview_construction` | Per-element native placement qualification and exact current site evidence; it does not place anything |
| `inspect_construction_intents` | Saved plan/element identity, original object, confirmed native lifecycle lineage and current native state |
| `inspect_areas` | Native zone and area identities, membership, settings, references and explicitly scoped cell observations |

Maps in write operations use their canonical `Map.GetUniqueLoadID()` strings.
Spatial cells include fog/out-of-map unknowns; missing objects in an incomplete
or fogged region do not establish absence. Samples retain their actual start and
finish ticks and are not atomic snapshots. Construction spatial output always
uses literal `cell_groups:[{...cellProperties,_xz:[[worldX,worldZ],...]}]`.
Every group retains all original properties except `x`/`z`; properties must be
fully equal to share a group. `_xz` contains absolute world-coordinate pairs.
Unknown groups keep their original `known:false` and status, and no flat `cells`
array is also emitted. This self-contained JSON shape has no numbered property
dictionary or external decoder. Bounds, objects, sample and coverage are
unchanged. Native management completion checks bytes, node count and depth
within the existing transport limits; a genuinely dense distinct view that still
cannot fit returns `observation_result_too_large` with a narrowing request.
Interaction offsets are native
rotation offsets; multi-cell occupied rectangles additionally follow
`GenAdj.AdjustForRotation`'s even-size anchor adjustment. `is_room_boundary`
uses the audited native region/room classification, separately from movement
passability and roof support. `interaction_offsets` is the authoritative full
list, including definitions with multiple native interaction cells.

Construction intent evidence includes `rotation_policy: "native_door"` for
supported single-cell native doors whose blueprint and finished building set
their orientation from adjacent cells. `element.rotation` retains the submitted
rotation and `target.rotation` reports the sampled native object. The policy
applies only to the exact native replacement chain and its single-cell footprint.

Placement preview calls the audited normal native qualification path. This may
initialize native definition/PlaceWorker caches, but it does not change research
progress, configure a pawn, place a blueprint or create a Job. Custom native
construction runtime classes and non-native PlaceWorkers require separate
support. Normal placement does not silently clear existing objects, replace
protected structures, or complete construction.

### Additional transaction operations

The existing `dry_run`, `commit`, `undo`, identity, epoch and idempotency rules
remain in effect. Structural intent values and native CAS expectations have
separate schemas in the directory. A structural receipt contains the actual
created identity/configuration; its undo guard is frozen at commit. User or game
changes after that boundary are compared against that saved guard, rather than
resampling a new expected value at undo time.

Standard production supports `add_production_bill` with optional initial
`settings`, `set_production_bill`, `copy_production_bill`,
`reorder_production_bills` and `remove_production_bill`. Legacy `set_bill` keeps
its four-field contract. Standard `Bill_ProductionWithUft` is supported without
copying or destroying its bound unfinished item. Specialized bills remain
explicitly unsupported. Removal/undo refuses current or queued Job references
and bound unfinished work. Full ingredient filters include ordinary definitions,
special filters, hit points, quality and mental-break-chance ranges. Native target
counters retain their configured scope; a permitted ingredient is not inventory
and a bill is not proof of a runnable Job.

Food operations are `create_food_policy`, `copy_food_policy`,
`set_food_policy_content`, `set_food_policy` and `delete_food_policy`.
Policy business IDs are stable `food_policy:<saved-native-instance-GUID>` values.
The native `Policy.GetUniqueLoadID()` contains its label and changes on rename;
it is exposed only as `native_load_id` for audit, never used as a mutable API or
ledger identity. Native Scribe references use that current native load ID, then
the bridge binds restored business IDs after saved native identity resolution.
There is no name-based or old-native-ID fallback. Rename, assignment, copy,
delete, ledger and undo refer to the same original policy through its stable ID.
Unchanged native filter state is preserved without reconstructing it. Existing
latent allowed definitions remain expressible even when they are not human
edible; new allowances follow the audited food UI parent's positive abstract
Nutrition predicate and require an officially loaded definition. This does not
enable arbitrary newly introduced Mod definitions.
Shared content changes explicitly choose `fixed_users` with an exact set, or
`dynamic_shared`; native users are checked again on the commit frame. A
`native_instance` token distinguishes reused native policy IDs. The same saved
identity service covers areas and zones: live original references and tokens are
saved, then rebound after native cross-reference resolution. Deleted originals
remain available in RAM for same-epoch undo, but are not saved as reusable-ID
references. Policy structure verification reads the complete native directory,
including created/removed identities, rather than reading only a copy source.

`set_bed_assignment` is pawn-centric, preserves the previous bed and does not
evict another owner. `set_bed_medical` requires owners to be cleared explicitly
before enabling medical use. Areas/zones support normal creation and cell
changes. Native designation qualification currently requires the player's
current map; the bridge does not change the selected UI map. Zone edits preserve
the existing Home area, refuse overlaps with other zones, and keep one connected
nonempty region. Roof-area changes refuse to clear the opposite roof instruction
implicitly. Local area flags preserve all unselected cells of a larger Home,
allowed-area or roof instruction. Each selected cell has the stable field
`area_flag:x:z` on the real native area object, including its saved instance
token. Local patches contain at most 64 cells. Whole-mask `set_area_cells`
compares the complete old mask in the preparation frame, then writes only the
symmetric difference using those same per-cell identities. More than 64 changed
cells requires explicit local batches; an unchanged whole mask is rejected as
`no_changes` without creating ownership. There is no composite hash-mask field
that can alias a different local patch.

Each per-cell expected value and receipt also contains nullable `manual_id`.
Desired model cells remain `{x,z,enabled}`; their generation comes from the
original native evidence. Whole-mask CAS additionally binds the observed opaque
`manual_revision`. Full rows include sparse `manual_ids` and an explicit
`manual_ids_complete` flag, including manually cleared false cells. A generation
change conflicts even if the native boolean has returned to its earlier value.

`colony_write.manual_area.v1` reports the audited native brush commands and the
manage-areas row's Invert action. Only those UI scopes can label an area change
`user_designator`; model transactions explicitly exclude that scope. Native
automatic Home/roof updates remain game changes. `Area.Clear` and `Area.Invert`
are observed as bulk changes only inside those exact native UI scopes. Current
1.6 ordinary clear commands are the corresponding clear brushes; arbitrary
backend calls and unknown UI extensions are not claimed as manual sources.
Missing/foreign patches or invalid saved provenance disable guarded area writes.

Only explicitly selected cell observations, committed fields and explicitly
created area cells establish cell fences. Whole-mask reads establish one area
revision without creating a default per-cell record for the map. A manual source
has stable `manual_id`, positive per-cell `manual_order`, the original
`origin_load_epoch`, actual area/field/cell/map, native before/after values and
the actual UI method. Owned sources preserve the prior AI value and changeset
under `ownership`; observed but unowned sources have `ownership:null`.
The latest source is retained by save-scoped original-area reference and native
instance. It is exposed by both `area_manual_changed` events and ledger snapshots,
including while autoplay/event delivery is off. Snapshots add current native
status/value and explicit manual-source completeness. Delayed events are ordered
within their origin epoch and actual cell/instance; GUIDs and paused game ticks
are not ordering clocks. Return acceptance binds that exact manual generation.

`colony_write.ledger_snapshot.selection.v1` adds optional
`write_ledger_snapshot` arguments `fields:[{object,field}]` for 1–64 unique
identities. Native rows are selected before resolving current values and manual
sources. Results include `scope:{kind:"fields",fields:[...]}` and any
`missing_fields`; completeness applies only to that exact scope. A missing AI
ledger entry does not invent ownership for an observed unowned manual source.
Empty arguments retain the legacy full-ledger read; large colonies should use
the exact fields from `write_field_identities` or `write_undo_fields`.

`set_deconstruct`, `set_mine` and `set_cut_plant` create normal work
designations on exact visible native objects. They neither complete work nor
enable god mode. They refuse native immediate-destruction paths and conflicting
instructions/forbidden changes that would otherwise be cleared as side effects.
`value:false` removes only the exact requested designation.

`place_blueprint` takes one element value containing `plan_id`, `element_id`,
`definition`, `kind`, `x`, `z`, `rotation` and optional `stuff`.
The native identity is scoped by map, plan and element; receipts and intent reads
include its actual `field_identity`. Up to 64 compatible elements can share one
transaction. All incompatible footprint/replacement claims are rejected before
any mutation; normal qualification is repeated at each write. A house's later
work is asynchronous native gameplay, not a single atomic house transaction.

`cancel_blueprint` only removes the exact unchanged unstarted planner blueprint.
It retains a `model_cancelled` intent and the original for same-epoch undo.
`restart_blueprint` is explicit, requires a known destroyed/cancelled original
plus current-site CAS, and refuses unknown or live originals. It restarts the
same element; it does not implicitly revise a design or override user takeover.
The planner's adopted-plan and user-return gates apply before requesting it.
Blueprint-to-frame provenance is transferred only after successful native
replacement; frame-to-building/floor provenance follows the exact construction
scope and returned native object or terrain setter. Foundations, temporary
terrain and top terrain are observed separately. Position matching alone never
creates ownership. A successful placement receipt is not a completed building.
The separate `verification_read` descriptor reads saved native intent evidence
after submission.

### Bounded selected observations (`colony_observation.v1`)

The capability is advertised with the native event-hook subscription support.
It uses the existing `policy_request` / `policy_result` transport and event
cursor; frontdesk `pause_on_letter_defs` policy is unchanged. Planner requests
replace the entire observation set for the current connection and load:

```json
{
  "source": "planner",
  "observations": [{
    "observation_id": "house-progress",
    "revision": 1,
    "conditions": [{
      "id": "walls",
      "kind": "construction",
      "map_id": "Map_0",
      "plan_id": "house-a",
      "target_ids": ["wall-1", "wall-2"],
      "max_samples": 100,
      "expires_at_tick": 80000,
      "sample_interval_ticks": 250
    }]
  }]
}
```

An empty set revokes all registrations. Supported kinds are `construction`
(plan-local element IDs plus `plan_id`), `production` (worktable IDs),
`food_policy` (policy IDs) and `resources` (exact loaded definition names).
Map IDs are required except for global food policies. Limits are 16 observations,
8 conditions per observation, 64 total targets, 1–1000 samples per condition and
an expiry no more than 600,000 game ticks ahead. Normal sampling intervals are
1–60,000 game ticks (default 250). While paused, the same scheduler may sample
at most once per two seconds of wall time, so same-tick user changes remain
observable; expiry still follows game ticks and samples still consume budget.

Successful transport returns normalized `observations` plus one registration
receipt per observation:

```json
{
  "observation_id": "house-progress",
  "revision": 1,
  "status": "registered",
  "covered_condition_ids": ["walls"],
  "receipt_id": "0123456789abcdef0123456789abcdef"
}
```

A rejection has `status:"rejected"`, no covered conditions and a reason. Merely
echoing the requested set is not registration evidence. An unchanged revision
retains its receipt and remaining budget. Condition changes require a new
revision; exhausted/expired revisions cannot be resurrected by replacing some
other registration. Reconnect/load invalidates registration coverage and requires
fresh receipts.

`game_event` uses `kind:"observation"`, `observation_id`, `revision`,
`condition_id`, `receipt_id`, `status`, `sample_tick`, `observation` and
`coverage`. Status is `changed`, `unknown`, `expired` or `exhausted`.
Complete samples read targets cooperatively, one at a time; an active resource
read receives one bounded frame slice before another read is started. Coverage
retains the non-atomic start/finish window. Registration checks native identities
without taking an uncounted sample. Pending publication is retried without
another native read, and the delivered baseline advances only after enqueue
succeeds. Events are capped at 256 KiB; oversized evidence becomes explicit
unknown/needs-read coverage. Unknown-to-known recovery publishes evidence even
when the native value matches the last previously known value.

Production and food events are compact native configuration/progress summaries;
full content is read on demand. Resource events reuse the current resource
reader and retain ground, player-pawn-held, other-held and trader source
partitions, exclusions and scope. A zero is valid only within that observed
scope. Inventory differences do not establish production, consumption, hauling
or loss causes. No observation registration invokes a model or enables a second
autonomous service.
