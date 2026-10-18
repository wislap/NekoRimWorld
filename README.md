# N.E.K.O RimWorld Bridge v3.2.0

Players: [中文安装与使用](README.zh-CN.md) · [English installation and use](README.en.md).

Installable packages, checksums and validation status are listed on the
[GitHub Releases page](https://github.com/wislap/NekoRimWorld/releases).
Use the `NekoRimWorld-<version>.zip` asset for installation. GitHub's automatic
source archives contain the development sources. Harmony and the matching
N.E.K.O RimWorld plugin are installed separately; see the user guides above.

Maintainers: build an installable package from this repository using PowerShell 7.2+
and a .NET SDK capable of building net8.0 and net472:

```powershell
pwsh -NoProfile -File Tools/Build-Package.ps1
```

The script performs locked restore, an isolated Release build and net8.0 bridge
smoke tests. It writes `artifacts/packages/NekoRimWorld-3.2.0/` containing the Mod
ZIP, SHA-256, file manifest and validation logs. Use `-OutputDirectory <directory>`
to choose another parent directory. Existing version directories are rejected;
failed attempts keep their logs and work files for inspection. It does not change
`1.6/Assemblies`, deploy to a game installation, commit, upload or publish.

Only reviewed runtime DLLs, metadata, English/Simplified Chinese language files,
user guides and license/provenance records enter the ZIP. Game/framework/Harmony
reference assemblies, test runners, personal configuration, source and bench are
excluded. Dependency upgrades require reviewing `Tools/RuntimeDependencies.json`;
licenses are copied from NuGet, with the GraphQL-Parser license pinned in
`ThirdParty/`. Optional `About/Preview.png` and `About/ModIcon.png` are included
when present. A preview image is optional; the game recommends an aspect ratio of
2.468:1. See the game's bundled `ModUpdating.txt` for its folder and metadata rules.

Run `python -m unittest discover -s Tests/Package -v` for standalone packaging
acceptance, archive hashes/dependencies/licenses, and rejection of invalid
translations, missing dependency links/licenses or existing output. It requires
only Python's standard library alongside the build tools, retains evidence and
prints its location; `NEKO_MOD_PACKAGE_TEST_OUTPUT` selects a new evidence folder.

Product version: **3.2.0**. v3.2 includes the selected native situation
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
The resource scope keeps the exact requested labels/definition names and resolved
native definitions in `selection`; `selected_definitions_only` means a zero or
complete result covers those definitions, not all food or another business
category. This read excludes fogged contents and adds existing public map-trader
stock, so its source totals cannot silently replace a differently scoped count.
Trader stock declares `price_basis: market_value_reference_not_negotiated_quote`
beside its rows. Only explicit-context `evaluate_trade` supplies negotiated native
prices; quantity reads do not request a quote.

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
