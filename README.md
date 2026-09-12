# N.E.K.O RimWorld Bridge

**Evaluation status:** the current v1.3 candidate did not meet the Agent quality objective. This experimental implementation is retained for review; it is not promoted to the stable branch or released.

Read-only RimWorld 1.6 bridge kernel for the N.E.K.O integration.

## Current development state

This checkout contains the unreleased v1.3 development candidate; version
metadata remains1.0.0 until validation and release. Resource reports publish
resource_evidence.v3, with shared definition/instance conditions and optional
whole-request physical totals. No focus preset or dedicated medicine selector
exists in the new catalog. Native ThingDef.IsMedicine is an ordinary field.

All public query result types use observed_values.v1 descriptions for faithful
references. The same schema-driven condition engine serves native, template
and frozen execution. See [PROTOCOL.md](PROTOCOL.md). Software tests and local
presentation checks are not a claim of improved complete-Agent answers.

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
- No claim of in-game runtime validation yet. Build and standalone loopback
  tests do not replace an actual Unity/Mono new-game/save/load/main-menu test.

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
