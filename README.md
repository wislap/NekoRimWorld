# N.E.K.O RimWorld Bridge

Read-only RimWorld 1.6 bridge for N.E.K.O. The current checkout includes the
v2.0 local Def Wiki publisher milestone alongside the existing query engine.
Package version remains 1.0.0; this milestone names the feature checkpoint.

The Mod exposes `query` / `describe_query` with `public_query.v2`, canonical
schema-derived fields, related collections and whole-selection aggregates.
The companion plugin normally presents these reads to the Agent as `look` / `count`,
with the native vocabulary retained as a fallback. A published local Def directory
also enables the plugin's `wiki(query)` tool.

The v2.1 spatial capability adds a separate `spatial_query` operation. It performs
one current-tick native reachability check from a resolved Pawn to one Thing or map
cell. It accepts names or IDs, returns ambiguity instead of choosing a target, and
does not infer WorkGiver eligibility, reservation success, routes, Rooms or Regions.
The semantic plugin exposes this capability as `inspect_space` only when the
connected Mod advertises `spatial_query.v1`.
Selection lists now accept both field paths and field maps. Bare child maps
are accepted only when every key is a known child field and no option keyword
is present. Ambiguous overlaps, mixed field/options and invalid arguments are
rejected before reads, with input locations for selection/compiler errors.
No business-specific selector or new Agent tool was added.

Single-object conditions also accept unambiguous child-field maps. Concrete
`on` projections share array, bare-field-map and explicit selection forms with
related objects; empty `on:{}` remains valid. Invalid discriminator structure is
reported before dependent field errors. Failed batches still perform no reads,
but report one primary error per invalid root request (at most eight), plus the
indices that passed compilation. Validation success is not an observation;
source/field work budgets remain shared across the entire batch.

Native enum-backed string fields now publish their actual value domains. The ten
exposed domains share validation and discovery: arbitrary names fail before game
reads; valid empty selections remain zero. Native flags allow comma-separated
names and `has_all` / `has_any`. Wire fields remain String, preserving existing
GraphQL aliases; names, labels and Def references remain open strings.

Production resolves enum names and values from the loaded `Assembly-CSharp`.
BridgeSmoke and frozen-world explicitly compile with `NEKO_SCHEMA_FIXTURE` and
copy `Tests/fixtures/native-enums.json`, generated from the pinned reference
assembly by `Tests/EnumCatalog`. Regenerate the fixture when updating the game
reference; it records the assembly SHA256. The production build does not read
this fixture. Predicate literals retain the 4096-character bound and are compiled
once; unrecognized observed native values produce unknown membership.

Definition conditions inside all/any/not now retain safe early filtering and
three-valued semantics. Per-root argument schemas and result scope descriptions
come from the registered schema. Nested collection scopes include their actual
arguments and predicate; relation descriptions are deduplicated. The 8192 source
candidate bound remains; ordinary instance predicates or display offsets do not
remove it. Scan-limit diagnostics now use the resolved arguments of the failing
root: an explicit single-map/single-definition `things` reading explains why
repartitioning those same dimensions or changing instance filters/display options
cannot extend its scan. Broader `things` reads preserve conditional narrowing
advice; other roots and nested collections do not inherit it. Observed
`known_matches`/`known_value` remain distinct from the candidate cap, and complete
sibling results remain usable. Other error codes and source-specific diagnostics
are unchanged. No Agent tool, research-round cap or forced-stop policy is added.
These fixes do not establish improved free-text answer quality.

Public query terminology now distinguishes Pawn instances (humans, animals,
mechanoids) from free colonists, and map/holder enumeration from pathfinding.
Own spawned map/position and containing map/position have separate descriptions;
Pawn.map_id is documented independently of status.position. Inventory, equipment,
apparel and currently carried contents retain their distinct populations. Generic
null descriptions no longer assert every issue-free null is "not applicable";
field semantics, issue paths, complete zero matches and undisplayed pages remain
separate. These are registry-description corrections, not field renames or new
query behavior. Full-Agent results and remaining limitations are recorded in the
bench's `planning/SEMANTIC-CONSISTENCY.md`.

Public aggregate queries with `limit:0` now omit `rows` and `page`, including
nested collections. Aggregates, effective scope, scan completeness and issues
remain available; requested detail keeps its normal pagination. The
`include_unreal` description distinguishes filtering an emitted Thing from
traversing descendant holders; it does not change the native traversal.

Field descriptions are exported from the executable registry, including notes
for types outside the common-field directory. Native counters, holding roles,
health summaries and conditions retain their distinct game semantics. Legacy
GraphQL, templates and resource reports remain compatibility/developer paths;
the public-query Agent uses text submission, not legacy value references.

Deterministic tests and independent native capture have exercised this code.
These checks do not establish complete-Agent answer quality or a release.
The companion bench tracks model comparisons and limitations. Sources require
rebuild and game restart to activate; this integration did not replace the
user's installed assemblies.

## Identity

- RimWorld package ID: `neko.rimworld.bridge`
- Assembly: `NekoRimWorld.dll`
- C# namespace: `Neko.RimWorld`
- Target: `.NET Framework 4.7.2`
- RimWorld references: `Krafs.Rimworld.Ref 1.6.4871`
- Harmony compile reference: `Lib.Harmony.Ref 2.4.1`

## Implemented scope

- Process-scoped, single-worker TCP client bound to loopback semantics.
- Protocol v2 with 4-byte big-endian framing and strict envelopes and generic JSON data.
- Mutual HMAC-SHA256 challenge/response; the pairing token is never sent.
- Per-save `colony_id`, per-load `load_epoch`, connection epochs, sequence
  validation, heartbeats, deadlines, and stale-session leases.
- Bounded inbound/control/telemetry queues. Network code never retains Verse
  objects; game state is read only on the RimWorld main thread.
- Object-first section templates and precise GraphQL share one nested schema.
- Pawn status, mood, health, equipment, profile, and explicit derived-stat queries.
- Mod settings for enable/disable, port, and a 32-128 character pairing token.

The bridge is disabled by default. It connects only to `127.0.0.1` and the
default port is `48919`.

## Automatic offline Def knowledge

The publisher also produces `ready.json` (`neko.defs.index.v1`) referencing an
immutable `snapshot-<id>.json`. The payload keeps every full Def record; the small
ready directory contains names/descriptions, UTF-8 byte offsets/lengths and SHA-256
per record, plus the whole payload hash and source metadata. The ready directory
is atomically replaced last. Readers attach this directory without reparsing the
full payload, then seek and validate only requested records. Existing readers can
still use `current.json`, which is retained as a compatibility copy. Old immutable
payloads remain available to consultations already using them.

The sub-second target applies to reader attachment after publication. Game startup,
Def enumeration, encoding, hashing and directory generation remain publisher work;
they are not claimed to complete in one second.

On the first `LoadedGame` or `StartedNewGame` in each game process, the Mod arms
an automatic export. Actual Def enumeration, metadata reads and field reflection
start on the next `GameComponentUpdate` on the game thread. No constructor-time
Def reads or `ExecuteWhenFinished` assumptions are needed. Export continues at
most eight work units / a cooperative 2 ms per rendered game frame, including
paused games. One Def traversal is not preemptible; this is not a hard frame-time
guarantee. Returning to the menu suspends extraction until another game is loaded.
No game time, pause setting, map, or Pawn is changed. The exporter runs even when
the TCP bridge is disabled and needs no model request, CLI, SQLite or network.

The canonical publication is `ready.json` plus its immutable
`snapshot-<id>.json` payload under `Environment.SpecialFolder.LocalApplicationData`
+ `neko-rimworld/knowledge`. Windows normally uses
`%LOCALAPPDATA%/neko-rimworld/knowledge/ready.json`. Set
`NEKO_RIMWORLD_KNOWLEDGE_DIR` in the game's environment to override the directory;
configure the local reader with the same directory. This is host configuration,
not a model-controlled path. The plugin reads this shared ready directory;
`current.json` remains a compatibility copy for legacy/offline import. This Mod
adds no wiki operation or protocol messages.

Every process regenerates on its first game entry, without fingerprint cache
reuse. Merely opening the main menu does not generate a new file. While generation
is pending, or if writing fails, an existing file remains an **older snapshot**;
its existence does not validate the current installation. `generated_at` records
the UTC generation start, with `game_version`, `language` and the active `mods`
list identifying the captured context. Consumers must check these metadata and
`complete`/`issues`, and must not treat an older file as current verification.
A successful generation replaces the file atomically from a flushed temporary
file in the same directory. There is one process-local writer; it only receives
plain DTOs. Failure leaves the previous file in place and logs a warning; no
non-atomic copy/delete fallback is attempted. Separate RimWorld processes should
use separate override directories when their active mod sets differ.

The format is `neko.defs.v1`, with top-level `format`, `game_version`, `language`,
`generated_at`, `mods: [{package_id,name}]`, `complete`, `provenance_complete`,
`issues`, `warnings` and `entries`. Each entry contains `key`, `type`, `def_name`,
`label`, `description`, `source_mod`, `package_id`, `facts: [{name,value}]`,
`related: [{key,label}]`, `diagnostics: [{path,kind,reason}]` and `truncated`.
Facts may also include `type` to preserve the CLR scalar type and distinguish actual
null from the string "null". Existing v1 readers may ignore this optional metadata.
`key` and related keys use the full runtime type name + `/` + `defName`, including
the namespace. `type` also contains the full name. All current Def databases are
enumerated through `GenDefDatabase`, with database types and typed keys deduplicated;
this is not a fixed eight-type catalog. Source mod metadata is the Def's owning
`modContentPack`, **not XML patch history**. Labels/descriptions use the active
language's loaded fields, not generated display getters.

Facts preserve the loaded Def object graph as bounded field paths and scalar values,
list/array/dictionary/set contents and typed Def references. Runtime `Type` values
are stored by their full type name; Def references are stored by typed key and also
link the related entry. The extractor reads inherited public/private instance fields,
excluding static, nonserialized, `[Unsaved(allowLoading:false)]`, compiler-generated
backing fields and root Def metadata already stored in the entry. Nested labels
and descriptions, readonly fields and loadable `[Unsaved(true)]` fields are kept;
field names beginning with `cached` are not automatically discarded. It never invokes arbitrary
properties/getters, `ToString` overrides or arbitrary custom enumerators. Unity assets,
Delegates and live Thing/Map/Game/WorldObject graphs are expected omissions, not
missing Def data. Object types are retained as `$type`; cycles use `$ref` with the target field path.
Dictionary keys and values use indexed pairs so key types and punctuation are preserved.
Exact framework containers are enumerated; custom containers and curves are read
through fields without invoking their enumerators. Real traversal,
entry or snapshot limits are recorded in `diagnostics` and set `truncated`; such an
entry makes the snapshot `complete:false`. Missing source Mod metadata only affects
`provenance_complete` and is reported in `warnings`. Null field values are explicitly
represented as the string `null`, rather than hiding failures.

Limits are process-safety guards rather than the model response budget: depth 128,
8,192 fields per object and 1,000,000 items per collection, 2,000,000 visited values and 200,000
facts per entry. Exact identity is rejected rather than shortened; ordinary fact
names/values and descriptions are bounded only to protect the snapshot. Each entry
is at most 8 MiB and the snapshot at most 512 MiB. There are at most 100000 entries,
8192 database types, 1000000 database visits and 256 snapshot messages. The model
still receives a small bounded result from `wiki`; these source limits must not be
confused with its response limit. An entry can lose trailing data only when a real
safety budget is hit, and then its `truncated` flag and diagnostics remain.

`KnowledgeSnapshot.cs` and `KnowledgeFields.cs` have no Verse dependency and are
covered by BridgeSmoke fixtures for safe field reads, typed references, bounds,
metadata, UTF-8, atomic replacement, failed-publication preservation and concurrent
writers. Production builds validate game reference APIs; real game lifecycle,
large modpacks and platform-specific atomic filesystem behavior still need in-game
validation. The extractor is independently implemented under this repository's
MIT license. No RimSearcher source is vendored; RimSearcher revision `4d821ae`
(MIT) was an architectural reference, not a runtime dependency.

## Deliberately not implemented

- No game actions, reflection RPC, autonomous colony management, screenshots,
  full-world mirroring, or high-frequency event patches.
- Independent new-game captures cover native query parity and bounded reads;
  they do not cover every Mod combination or a full host/save/load lifecycle.

## Runtime shape

```text
NekoRimWorldMod        process settings and singleton runtime
BridgeRuntime          one background TCP worker, bytes and DTOs only
NekoGameComponent      save/load identity and main-thread request pump
GameQueryEngine        schema, validation and serial field execution
TemplateCatalog        object selectors, merged section trees, exported tool schemas
PawnSchema             nested pawn nodes and bounded collection slices
PawnDetailsData        real game facts and query-local child handles
VerseGameData          individual live-game getters
Protocol               authentication, framing, validation, generic JSON payloads
```

`GameComponentUpdate()` handles at most one query per frame. A 12 ms cooperative
read budget begins at first live access; parsing/validation/serialization are
additional overhead, not a hard frame-time guarantee. Results that hit a deadline,
entity limit, invalid Mod value, or recoverable getter failure are returned as
`partial` with path-addressed issues rather than being presented as complete.

## Verification

Install the .NET 8 SDK. From this repository's root, on Linux or Windows:

```sh
dotnet build Source/NekoRimWorld.csproj -c Release
dotnet run --project Tests/BridgeSmoke/BridgeSmoke.csproj --framework net8.0
```

NuGet restores the pinned game/reference packages; a local game installation is
not required to compile or run these smoke tests. The build writes the Mod and
dependency DLLs to `1.6/Assemblies/`, which is ignored by Git. To stage a build
elsewhere, pass `-o /path/to/staging` to `dotnet build`. Running the Mod itself
requires RimWorld 1.6 and Harmony; do not overwrite assemblies loaded by a
running game.

BridgeSmoke runs a real loopback server/client exchange covering
mutual authentication, `session_open`, query correlation, typed nested JSON,
fragmented reads, and oversized inbound-frame rejection without launching the
game.

The cross-language wire contract and HMAC test vector are documented in
`PROTOCOL.md`.

Protocol v1 is rejected. Upgrade the Python plugin together with the Mod.
Ship the pinned Newtonsoft.Json 13.0.3 DLL from the build output alongside
NekoRimWorld.dll. GraphQL.NET 8.8.4 is bundled. Copy all generated dependency DLLs alongside the Mod;
consult the plugin DATA_PATH.md for syntax, pagination and resource limits.

Version 0.5.0 uses target + sections requests, replacing old type/args templates.
Pawn sections are status, mood, health, equipment and profile. Nested lists have
bounded slices with explicit coverage; memories have observation-local references.


## Version 0.6.0

Native Thing queries, ThingDef discovery, component interface projections,
IThingHolder contents, native storage groups and cached ResourceCounter reads.
Pawn equipment shares the same Thing graph. Map discovery is scoped and bounded;
held contents are a distinct relation. Unknown Mod components retain runtime
types with unsupported status. get_game_info templates and exact queries share
bounded inline type-fragment handling. describe_schema is metadata-only and the
Python plugin injects a compact directory rather than the complete SDL.

Compile-time target remains RimWorld 1.6.4871 / net472. Restart the game after
rebuilding; an already running Unity process keeps its previously loaded DLL.
Native Thing and component regressions run in Tests/BridgeSmoke/ThingQueryTests.cs;
these deterministic tests do not replace in-game Mod compatibility validation.

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

`inspect_space(from,to,detail="route")` uses the existing spatial request to check
reachability and, if true, run at most one native `FindPathNow`. Omitting `detail`
or using `summary` retains reachability-only behavior. Discovery advertises
`spatial_query.route_summary = native_path_summary.v1`; older Mods do not expose
the route parameter. No preliminary `look` or separate reach request is required.

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
