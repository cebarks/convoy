# Changelog

## Unreleased

### Fixed

- Install package-tracked direct Managed DLL dependencies advertised by Quartermaster, including same-version manifest additions.
- Validate supported canonical roots, case aliases, reparse destinations, complete checksums and archive membership before changes; reject unadvertised entries rather than ignoring them.
- Preserve existing matching unowned files without adopting them, preserve BepInEx configuration, reject conflicting local assemblies, and remove only unchanged owned files no retained mod needs.
- Preflight replacements/removals, roll back ordinary I/O failures, and atomically replace sync state. Redownload operates only on installed selections rather than enabling every optional group.
- Reject incompatible file/directory transitions before mutation; attempt every rollback recovery action and report original plus recovery failures, retaining any unrestored backup for recovery.
- Validate complete catalog manifests and duplicate mod identities before normal planning or either redownload mode, including no-op selections.
- Stop recursively deleting arbitrary `.convoy-old` files; loaded-DLL backups use unique names and may remain until manually inspected after process exit.

### Upgrade requirements

- Requires coordinated client-first rollout with the expanded Quartermaster publisher. Old clients have no ownership safeguards for Managed paths; new clients reject unadvertised old-server archive entries. The existing BepInEx-only Convoy bootstrap remains available before publisher upgrade. No capability negotiation or mixed-client enforcement is added. See README for limits; this is not a crash-durable journal and native Unity behavior requires separate verification.
