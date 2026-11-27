# Changelog

## Unreleased

## [0.8.1] - 2026-11-27

### Changed

- Automatically accept currently installed legacy files as the new checksum baseline, retaining historical hashes and ownership. Remove per-file legacy review and its sync-blocking approval gate; ordinary mod-level confirmation, config preservation and download verification remain.

### Fixed

- Index directory names within read-only path scans instead of rescanning every sibling for each file, avoiding quadratic startup work for large modpacks. Keep fresh path checks at mutation boundaries and show cancellable installed-file scan progress.

## [0.8.0] - 2026-11-26

### Changed

- Check and re-verify installed files with up to the logical processor count reported by .NET minus two parallel readers (minimum one), and download up to sixteen distinct objects at a time. Verified installation and rollback remain serial.
- Prepare install files while verifying each object, then publish the verified files without copying or hashing them during Apply. Installations that cannot protect prepared files keep verification during the copy.

### Breaking

- Require the paired Convoy v3 publisher in Quartermaster v0.6.0. File proofs, objects, generations and transfer checkpoints use canonical XXH3-128 identities and byte lengths; no legacy ZIP or checksum fallback is used. Install the standalone client package with the game closed.
- Keep legacy state pending until explicit player review. Matching approvals retain ownership and historical evidence; differing plugin DLLs require separate backup-and-replace consent. Disabled or skipped optional files cannot be approved or replaced.

### Fixed

- Allow game installations beneath linked parent directories while retaining protections on the game root and internal destinations.
- Install package-tracked direct Managed DLL dependencies advertised by Quartermaster, including same-version manifest additions.
- Validate supported canonical roots, case aliases, reparse destinations, complete checksums and delivery identities before changes; reject conflicting entries rather than ignoring them.
- Preserve existing matching unowned files without adopting them, preserve BepInEx configuration, reject conflicting local assemblies, and remove only unchanged owned files no retained mod needs.
- Preflight replacements/removals, roll back ordinary I/O failures, and atomically replace sync state. Redownload operates only on installed selections rather than enabling every optional group.
- Reject incompatible file/directory transitions before mutation; attempt every rollback recovery action and report original plus recovery failures, retaining any unrestored backup for recovery.
- Validate complete catalog manifests and duplicate mod identities before normal planning or either redownload mode, including no-op selections.
- Stop recursively deleting arbitrary `.convoy-old` files; loaded-DLL backups use unique names and may remain until manually inspected after process exit.

### Upgrade requirements

- Upgrade Quartermaster and the standalone Convoy client together. Legacy automatic sync and repair delivery routes are retired; setup/bootstrap ZIP delivery is independent and unchanged. For rollback, restore both old binaries and the compatible saved configuration, DB and client state. See the README for the paired cutover and rollback steps.
