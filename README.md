# N.E.K.O RimWorld Bridge

Read-only RimWorld 1.6 bridge for N.E.K.O. The current checkout contains the
v1.6.1 query/execution revision; package version remains 1.0.0.

The Agent uses `query` / `describe_query` with `public_query.v2`, canonical
schema-derived fields, related collections and whole-selection aggregates.
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
