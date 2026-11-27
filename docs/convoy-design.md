# Convoy v3 design

The Quartermaster publisher and BepInEx client are a paired upgrade. See [standalone installation and rollback](../README.md#coordinated-publisher-upgrade). ZIPs are only for the independent setup/invite/bootstrap flows; normal sync and repair never fall back to the legacy archive API.

## Publisher

A background worker takes a scoped snapshot of tracked packages and verifies their raw objects before admitting the resulting generation. A generation identifies a deterministic catalog; an object identifies a seed-zero XXH3-128 digest and a nonnegative 64-bit length. Digests are 32 lowercase hex digits in unsigned high-to-low order. Only the enabled current, ready generation admits requests. The immutable store is bounded by `max_store_bytes`; reserve space for retained legacy, active/pinned and candidate bytes as well as original sources. Legacy receipts are not served or reused as v3 proof. Pending legacy client baselines block publication until an operator explicitly trusts selected current files. The admin rehash action is not a legacy rebaseline bypass. XXH3-128 is non-cryptographic; publisher/transport trust remains separate.

| Method | Path under `/quma` | Purpose |
| --- | --- | --- |
| GET | `/convoy/v3/catalog` | Current manifest and generation ETag; revalidation supports 304 |
| GET | `/convoy/v3/generations/{generation}/objects/{digest}` | Full object or a validated open-ended byte range |
| POST | `/convoy/report` | Existing sync callback |
| POST | `/convoy/rehash` | Existing protected admin action, now queued |

The v2 catalog/object routes and old catalog/ZIP delivery return 410 when enabled; disabled delivery returns 503. A stale generation returns 409, and transient preparation/body admission responses use 503 with bounded retry guidance. Unknown object digests are not filesystem lookups.

## Client

The client pins the origin and generation, validates the complete manifest before selection, and plans required or optional choices at mod granularity. It stages only needed digests in private bounded storage. Strong `If-Range` validators bind a persisted prefix to the object identity; full XXH3-128 and length checks precede application. The v3 spool namespace shares the existing root lock with older processes but cannot reuse their identities. A complete verified object survives a failed session and is revalidated before reuse. A final conditional generation request and local state checks fence application. No-op reuse needs no object GETs. Apply preserves local config and retained shared files, rolls back ordinary I/O failures, and saves state atomically; this is not a multi-file crash journal.

Legacy client records are automatically rebaselined from currently installed bytes under the root lock, without per-file approval. Distinct legacy paths are observed in parallel; old hashes, lengths and ownership remain as historical evidence. The baseline does not assert old checksum equivalence or catalog equality and does not grant ownership to unowned files. Missing paths receive no byte proof. Configs remain preserved, and excluded, removed or unselected records cannot create a pending-review deadlock. State, origin, publication and local-byte checks fence the state-only commit; ordinary mod-level planning then uses the committed snapshot. See the [ownership policy](../README.md#supported-files-and-ownership). The publisher's operator rebaseline policy is separate and unchanged.

Missing v3 gives a paired-upgrade instruction without falling back or changing live files. Keep previous binaries, configuration, publisher objects and a compatible database/state backup for paired rollback; do not assume old binaries interpret new data. Initial explicit rebaseline and publication can cause downtime. Native startup panels and restart behavior require separate verification on a supported disposable game install.

Initial installed-file observations and the pre-apply reread each use at most `Math.Max(1, Environment.ProcessorCount - 2)` workers over distinct local paths, based on the logical processor count reported by .NET. All readers join before recovery, the final publisher fence, or live apply; ownership decisions, live writes, and rollback remain serial. The transport stages up to sixteen distinct objects at a time using Convoy-specific HTTP connection groups, without changing the game's connection limits or the server protocol.

Read-only path-scan scopes index each encountered directory once, including case-insensitive collisions even when an exact spelling also exists. Indexes are thread-safe for parallel observations and discarded at the end of each scan. Root/component attributes remain live; precommit uses a fresh index, while post-publication identity checks, live writes and rollback resolve paths without the scan cache. An unchanged pack still uses presence/revision checks rather than full-content hashing. Planning reports file-scan progress and checks cancellation during path validation.

The length and XXH3-128 check for a staged object can also write those exact bytes to protected install files, one per changed destination. After the final fence, Apply publishes each open file by handle (rename on native Windows, atomic placement of an anonymous inode on supported Linux x64 filesystems). The resumable cache is not reopened during placement and is still checked before reuse. This uses the same disk reservation as a verified copy, not a third payload copy. Wine, unsupported operations, or insufficient handle budget use the original verified-copy path; the optimization holds at most 4,096 prepared files per operation.
