# N.E.K.O RimWorld Bridge Protocol v1

`Protocol.cs` is the executable authority for this contract. This document is
the interoperability guide for the future Python peer.

## Transport

- TCP over IPv4 loopback only; the plugin listens and the RimWorld Mod connects.
- Default port: `48919`, configurable at both ends.
- Each frame is `uint32` big-endian payload length followed by UTF-8 JSON.
- Mod-to-plugin frame limit: 1 MiB.
- Plugin-to-Mod frame limit: 64 KiB.
- JSON has one object root. Protocol v1 rejects unknown envelope, payload, and
  typed-data fields instead of guessing their meaning.

Every UUID is exactly 32 lowercase hexadecimal characters (`Guid` format `N`).
Every nonce and HMAC proof is exactly 64 lowercase hexadecimal characters.
Sequence numbers start at zero and increase independently in each direction for
the lifetime of one TCP connection.

## Mutual authentication

The pairing token is 32-128 characters and is never transmitted. Both sides use
its UTF-8 bytes as the HMAC-SHA256 key.

```text
Mod                                      Plugin
 |-- hello(client_nonce) ----------------->|
 |<-- challenge(client_nonce,
 |              server_nonce,
 |              server_proof) -------------|
 |-- authenticate(client_nonce,
 |                 server_nonce,
 |                 client_proof) ---------->|
 |<-- welcome(status="ok") -----------------|
```

The exact UTF-8 HMAC messages are:

```text
neko-rimworld-bridge/v1/server\n{connection_epoch}\n{client_nonce}\n{server_nonce}
neko-rimworld-bridge/v1/client\n{connection_epoch}\n{client_nonce}\n{server_nonce}
```

The Mod sends no session or colony data before it has verified `server_proof`
and received a successful `welcome`. Proof comparison must be fixed-time.

Cross-language test vector:

```text
token            = 0123456789abcdef0123456789abcdef
connection_epoch = 11111111111111111111111111111111
client_nonce     = aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
server_nonce     = bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
server_proof     = 69826430fc5cebe86d61f08a38749ab5c0d57e3f1459ac21559a98dd7015068e
client_proof     = 7d8c68b95d01f6d08b10b2a06266d867f64729069f058be50979e56a9cec5204
```

## Envelope

All fields are present; values without a session use empty IDs and `-1` for
game/snapshot counters.

```json
{
  "v": 1,
  "type": "query_request",
  "connection_epoch": "32-lowercase-hex",
  "colony_id": "32-lowercase-hex",
  "load_epoch": "32-lowercase-hex",
  "seq": 2,
  "request_id": "32-lowercase-hex",
  "game_tick": 12345,
  "snapshot_sequence": 0,
  "payload": {
    "query": "colony_digest"
  }
}
```

`colony_id` persists in the save. `load_epoch` changes on every new-game or
load-game activation. A request is valid only for the current connection,
colony, and load epoch.

## Direction and lifecycle

Before authentication, only the four authentication messages are valid. After
authentication:

| Mod to plugin | Plugin to Mod |
|---|---|
| `session_open` | `query_request` |
| `session_stale` | `heartbeat` |
| `heartbeat` | `protocol_error` |
| `query_result` | |
| `protocol_error` | |

The Mod emits a heartbeat every two seconds. The plugin must send authenticated
traffic regularly; the Mod closes a connection after ten seconds without a
valid inbound message. A loaded-game session becomes stale when its main-thread
component has not pumped for five seconds.

## Read-only query v1

The only query is `colony_digest`. A successful result uses:

```json
{
  "status": "ok",
  "data": {
    "kind": "colony_digest",
    "colony_digest": {},
    "omissions": []
  }
}
```

If a deadline, entity limit, invalid Mod value, or recoverable getter failure
prevents a complete view, status is `partial` and `omissions` is non-empty.
Other terminal statuses are `not_found`, `stale_session`, `unsupported`,
`busy`, and `failed` and carry `error_code` plus `message` instead of data.

Events and gameplay actions are intentionally outside protocol v1's implemented
capability set.
