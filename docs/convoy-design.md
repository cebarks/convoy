# Convoy v2 design

The Quartermaster publisher and BepInEx client are a paired upgrade. See [standalone installation and rollback](../README.md#coordinated-publisher-upgrade). ZIPs are only for the independent setup/invite/bootstrap flows; normal sync and repair never fall back to the legacy archive API.

## Publisher

A background worker takes a scoped snapshot of tracked packages and verifies their raw objects before admitting the resulting generation. A generation identifies a deterministic catalog; an object identifies a SHA-256 digest and a 64-bit length. Only the enabled current, ready generation admits catalog or object requests. The immutable store is bounded by `max_store_bytes`; initial copy/hash and startup verification take time proportional to the unique published bytes. Reserve space for the active and candidate bytes as well as the original sources. Unchanged objects can be reused across managed publications. The admin rehash action enqueues a republish for out-of-band changes; wait for Ready before retrying.

| Method | Path under `/quma` | Purpose |
| --- | --- | --- |
| GET | `/convoy/v2/catalog` | Current manifest and generation ETag; revalidation supports 304 |
| GET | `/convoy/v2/generations/{generation}/objects/{sha256}` | Full object or a validated open-ended byte range |
| POST | `/convoy/report` | Existing sync callback |
| POST | `/convoy/rehash` | Existing protected admin action, now queued |

The old catalog, batch ZIP, and single ZIP delivery return 410 when enabled; disabled delivery returns 503. A stale generation returns 409, and transient preparation/body admission responses use 503 with bounded retry guidance. Unknown object digests are not filesystem lookups.

## Client

The client pins the origin and generation, validates the complete manifest before selection, and plans required or optional choices at mod granularity. It stages only needed digests in private bounded storage. A strong `If-Range` validator authenticates appending to a persisted prefix, and full SHA-256 and length checks precede application. A complete verified digest survives a failed session and is revalidated before reuse. A final conditional generation request and local state checks fence application. No-op reuse needs no object GETs. Ownership uses xxHash3 for continuity without adopting pre-existing matching files. Apply preserves local config and retained shared files, rolls back ordinary I/O failures, and saves state atomically; this is not a multi-file crash journal.

Missing v2 gives a paired-upgrade instruction without falling back or changing live files. Keep previous binaries, configuration and a compatible database/state backup for paired rollback; do not assume that old binaries interpret new data. Native startup panels and restart behavior require separate verification on a supported disposable game install.
