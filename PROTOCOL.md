> **v1.4 query contract:** wire v2 envelopes are unchanged. `query_catalog.public_query` advertises `public_query.v1`; `query` takes `{requests:[{from,args?,where?,select?,aggregate?,group_by?,order_by?,limit?,offset?}]}`. `describe_query` takes `{types:[...]}` and returns metadata only. Query results include effective scope, scan/field completeness, fresh-observation paging and measures with explicit units. Legacy `observed_values.v1` applies only to legacy operations.

# N.E.K.O RimWorld protocol v2

The executable contract is Source/Protocol.cs and the Python plugin protocol.py.
The complete v2 request/response contract, ownership and extension procedure are
in [DATA_PATH.md](../../N.E.K.O/plugin/plugins/neko_rimworld/DATA_PATH.md).

Transport: 4-byte unsigned big-endian length followed by UTF-8 JSON. Loopback
only. Mod is the client; Python is the listener. Mutual authentication uses
hello/challenge/authenticate/welcome, HMAC-SHA256, connection epoch and both
nonces. Authentication domain: neko-rimworld-bridge/v2. Capability: query.execute.v2.

Session messages, heartbeat, request correlation and strictly increasing
per-direction sequence numbers are unchanged in purpose. colony_id identifies
the save and load_epoch identifies this load. One query_result belongs to one
session and sample. Only the protocol-v2 wire shape is accepted.

query_request payload: operation (nonempty text), arguments (JSON object).
query_result payload: status, optional data object, issues array; non-data
statuses additionally require error_code and message. Game field meanings are
owned by query readers; protocol code does not validate a gameplay DTO union.

Current operations: query_catalog, describe_schema, template_query, graphql and resource_report. colony_snapshot
is removed. query_result may include a generic execution object containing the
expanded query, variables, schema ID and template-root bindings. See DATA_PATH.md
for supported fields, limits, pagination and error paths.

Version 0.5.0 template arguments are requests[{target:{type,id?},sections:[...]}].
Selectors: pawn + required id, colony, or colonists with discovery filters.
Sections are applicable to the target and merge by exact target identity.
Nested data remains generic JSON; maximum wire depth is 32. No type/args fallback.
## Historical resource evidence extension (1.2 development)

Wire version and authentication are unchanged. `query_catalog` now advertises
`resource_parameters` and `resource_contract` (`resource_evidence.v2`). A
`query_request` with operation `resource_report` uses that JSON parameter schema.
The normal `query_result` envelope retains sample and request correlation.

The resource domain reports game-computed per-definition quantities for ground,
native storage cells, free-colonist inventory/worn/equipment/carried items and
cached resource counters. Physical scopes use stable Thing IDs for deduplication;
cached values never enter the physical union. The union is restricted to the
selected map/scopes and is not a universal colony total.

Ground defaults to fog-filtered; native stored counts, pawn scopes and cached
counts are not made visible-only by that flag. Partial physical quantities are
null with explicit observed lower bounds. Missing cached keys differ from zero.
Conflicting item identity fails the report. The12ms read budget, bounded scans
and bounded response remain in force; no Verse handles reach network workers.

This extension does not remove GraphQL, templates or schema discovery. Older
plugins ignore the additional catalog properties; new plugins only advertise
resource mode when the catalog supports it.

V2 groups each definition's readings under `rows[].scopes`: physical readings
contain `units`, `known_units`, `item_count`, `status`, and `fog_filter`; cached
readings contain `units`, `entry_present`, and `fog_filter`. Top-level coverage
retains scan diagnostics. `row_page.total_rows/returned_rows` describe selected
definition rows across scopes, never a count of ground categories. This replaces
v1's parallel quantity maps, cached presence field and `view`; v1 field meanings
are not silently reused. Physical membership and counting rules are unchanged.

`thing_defs` also accepts optional boolean `isMedicine`, mapped to template
target `is_medicine`. It filters native `ThingDef.IsMedicine`, intersects other
selectors and participates in cursor identity; false is a filter, not omission.


## Current v1.3 development contracts

Wire v2 and authentication remain unchanged. The catalog advertises
resource_evidence.v3, selection_contract (schema_conditions.v1), and
result_description_contract (observed_values.v1). Capability comes from the
connected catalog, not a guessed package version.

Resource v3 removes focus and dedicated medicine arguments. definition_where
operates on ThingDef, instance_where on physical Thing; exact names intersect
conditions. Cached has no Thing instance and therefore rejects instance_where
in a mixed request. include_total optionally publishes request-wide physical
scope totals and an ID union; cached never enters it. Pagination does not turn
a request-wide quantity into a page subtotal.

Collection producers expose where against their element type. The shared
compiler validates all clauses/field arguments before game reads, filters
before pagination, binds cursors to conditions, and treats unavailable field
values as unknown rather than false. Scalar/object/collection/possible-type
operations apply uniformly across public fields. Transport nodes/items are
windows, not alternative filtering roots. Same-type reference traversal uses
bounded cycle-aware reach; no private reflection or inferred reverse relation.

execution.evidence describes existing returned values: nodes, interned contexts
and subjects, with concrete-path overrides for heterogeneous data. Actual
resolver response paths establish canonical fields, including aliases and
inline fragments. Effective arguments/defaults and collection windows remain
bound to scalar and empty-list references. Metadata-only queries cannot be
presented as live state. Resource structural values retain map, selection,
source status and row pagination context; measurement values retain units,
state, subject and physical scope. Descriptions do not trigger extra game reads.

These are development contracts. Earlier stored envelopes are not rewritten.
No game action, additional network service or automatic investigation workflow
is introduced. Activation uses a normally restarted Mod, never a hot DLL swap.
