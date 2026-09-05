# N.E.K.O RimWorld Bridge

Read-only RimWorld 1.6 bridge kernel for the N.E.K.O integration.

## Identity

- RimWorld package ID: `neko.rimworld.bridge`
- Assembly: `NekoRimWorld.dll`
- C# namespace: `Neko.RimWorld`
- Target: `.NET Framework 4.7.2`
- RimWorld references: `Krafs.Rimworld.Ref 1.6.4871`
- Harmony compile reference: `Lib.Harmony.Ref 2.4.1`

## Implemented scope

- Process-scoped, single-worker TCP client bound to loopback semantics.
- Protocol v1 with 4-byte big-endian framing and typed JSON DTOs.
- Mutual HMAC-SHA256 challenge/response; the pairing token is never sent.
- Per-save `colony_id`, per-load `load_epoch`, connection epochs, sequence
  validation, heartbeats, deadlines, and stale-session leases.
- Bounded inbound/control/telemetry queues. Network code never retains Verse
  objects; game state is read only on the RimWorld main thread.
- One bounded read-only query: `colony_digest`.
- Mod settings for enable/disable, port, and a 32-128 character pairing token.

The bridge is disabled by default. It connects only to `127.0.0.1` and the
default port is `48919`.

## Deliberately not implemented

- No game actions, reflection RPC, autonomous colony management, screenshots,
  full-world mirroring, or high-frequency event patches.
- No Python/N.E.K.O peer yet; this repository currently proves the RimWorld
  half and its wire contract.
- No claim of in-game runtime validation yet. Build and standalone loopback
  tests do not replace an actual Unity/Mono new-game/save/load/main-menu test.

## Runtime shape

```text
NekoRimWorldMod        process settings and singleton runtime
BridgeRuntime          one background TCP worker, bytes and DTOs only
NekoGameComponent      save/load identity and main-thread request pump
ColonyQueries          bounded game-domain reads
Protocol               authentication, framing, validation, typed DTOs
```

`GameComponentUpdate()` handles at most one query per frame and passes a 2 ms
cooperative deadline into the colony projection. Results that hit a deadline,
entity limit, invalid Mod value, or recoverable getter failure are returned as
`partial` with structured omissions rather than being presented as complete.

## Verification

Use the scripts at the environment root to build, verify, link, and launch the
isolated development profile.

```powershell
.\scripts\Build-RimWorldMod.ps1
.\scripts\Test-RimWorldBridge.ps1
.\scripts\Test-RimWorldDevEnv.ps1
```

`Test-RimWorldBridge.ps1` runs a real loopback server/client exchange covering
mutual authentication, `session_open`, query correlation, typed nested JSON,
fragmented reads, and oversized inbound-frame rejection without launching the
game.

The cross-language wire contract and HMAC test vector are documented in
`PROTOCOL.md`.
