# Convoy

BepInEx 5 plugin for [SPT](https://www.sp-tarkov.com/) that syncs client-side mods from a [Quartermaster](https://github.com/cebarks/quartermaster) server. Runs on game startup, downloads/updates/removes mods to match the server's catalog, then lets the game continue.

## How it works

1. Fetches and validates the v3 catalog and pins its origin and generation
2. Diffs against local state to determine what needs to be installed, updated, or removed
3. Streams only missing or changed objects, reuses verified local bytes and resumes validated partial transfers. Length and XXH3-128 must match before files or sync state are changed. Sync and repair never use a mod ZIP.
4. Prompts for a restart if any BepInEx plugin files changed (they're already loaded by the time Convoy runs)

Startup uses revision and file-presence checks, not a full rehash of an unchanged pack. Read-only path scans index directory names once per scan instead of repeatedly enumerating thousands of siblings; live path, alias and reparse checks remain in place before mutation. The startup dialog reports scan progress and can be cancelled.

When content checks are needed, they use up to the logical processor count reported by .NET minus two readers, with a minimum of one. Up to sixteen distinct objects can download concurrently; Convoy still verifies them before making any live file changes.
When the filesystem supports protected preparation, verification also writes the future install files, and Apply moves those exact files into place without copying or rehashing them.

Optional mod groups are toggled via BepInEx Configuration Manager (F12 in-game).

## Supported files and ownership

Sync supports canonical `BepInEx/` paths, package dependency DLLs directly under `EscapeFromTarkov_Data/Managed/`, and existing SPT bundle-cache exports. Other game-root paths, traversal, drive paths, backslashes, Windows aliases, case collisions and symlink/reparse destinations are rejected before mutation. Missing, corrupt, duplicate or conflicting delivery identities abort the operation without deleting installed files or committing state.

Existing matching files are usable without being adopted: state records `owned: false`. Differing unowned files and locally modified assemblies require explicit replacement consent and an exact recovery backup, or remain conflicts. Existing `BepInEx/config/` files are preserved, including when package defaults change. Only unchanged owned files no longer needed by any selected mod are removed. Shared dependencies survive removal of one owner. Same-version advertised manifest changes trigger synchronization.

Legacy SHA-256/XXH3-64 records are migrated automatically: Convoy accepts current installed bytes as the new XXH3-128 baseline, with no per-file review or selection. This is a one-time trust decision, **not** proof that files still match their old checksums or the server catalog. Historical hashes and prior ownership are retained; matching unowned files are not adopted. Missing files receive no byte proof and can be restored by normal sync. Excluded, removed, disabled and skipped records do not leave an unresolved migration gate. Migration itself changes only state, never installed files; normal mod-level confirmation and ownership rules still control subsequent installs, updates and removals. Config files are preserved. A changed origin, generation, state or observed file aborts the baseline commit. Null legacy ownership keeps its prior BepInEx-only meaning, never ownership of a Managed assembly.

XXH3-128 is a non-cryptographic corruption checksum, not authentication or collision-resistant protection against an attacker. Trust the publisher and transport separately. Seed-zero digests use 32 lowercase hex digits in unsigned high-to-low order; every object also has a nonnegative 64-bit length.

File replacement/deletion is preflighted and rolled back on ordinary I/O failures; state uses atomic replacement. This is not a crash-durable journal or protection against hostile concurrent local filesystem mutation. Do not run other installers during sync. Unique `.convoy-old-*` backups may remain when Windows holds a loaded DLL; unrelated backup files are not recursively deleted.

### Coordinated publisher upgrade

Upgrade this v3 client and Quartermaster as a pair. The new publisher does not serve v2; a new client on an old publisher stops without attempting legacy downloads or changing live files. The endpoints are `/quma/convoy/v3/catalog` and `/quma/convoy/v3/generations/{generation}/objects/{digest}`. Expect downtime until the administrator explicitly rebaselines selected tracked legacy assets and the verified v3 generation becomes ready. Do not depend on self-update to cross this boundary. The existing setup ZIP is separate, not a safe overlay installer for a populated game directory.

Before cutover, preserve the previous binaries, server config and compatible database and the client plugin/configuration/ownership state. Roll back both ends with their compatible data, not by running old software against new state.

## Installation

Close the game. Install the standalone [Convoy v0.8.0 package](https://github.com/cebarks/convoy/releases/tag/v0.8.0) with [Quartermaster v0.6.0](https://github.com/cebarks/quartermaster/releases/tag/v0.6.0). For a local source build with `DeployAfterBuild=false`, the two inputs are `Convoy/bin/Release/Convoy.dll` and `Convoy/bin/Release/System.IO.Hashing.dll`, placed at `BepInEx/plugins/Convoy/Convoy.dll` and `BepInEx/plugins/Convoy/System.IO.Hashing.dll` in the package. Do not include game assemblies or regression artifacts. The project derives the plugin version from Git tags.

Convoy uses the active SPT server origin. Keep existing client configuration. Allow room for private transfer staging and temporary apply/rollback copies. Retry/relaunch after an interruption; a verified completed object is not fetched again. Let the server finish initial publication, and ask the administrator to rehash and republish following out-of-band content edits.

## Building

Requires .NET SDK (targets net472).

```bash
dotnet build Convoy/Convoy.csproj -c Release -p:TarkovDir=/path/to/spt/ -p:DeployAfterBuild=false
```

See the standalone package layout above. The safe local build does not deploy to a game installation.

By default the project resolves game DLLs by looking two directories up from the project root (assumes you cloned inside the SPT directory tree). Set `TarkovDir` or the `TARKOV_DIR` env var to override.

CI builds use stub assemblies (`-p:UseStubs=true`) to avoid needing copyrighted game DLLs.

## Sync regression test

Build Convoy in Release against a real SPT installation with `DeployAfterBuild=false`, then run the loopback test with Mono:

```bash
dotnet build tests/SyncRegression/SyncRegression.csproj -c Release
MONO_PATH="$PWD/Convoy/bin/Release" mono tests/SyncRegression/bin/Release/SyncRegression.exe \
  '-config={"BackendUrl":"http://127.0.0.1:1","Version":"live"}' '-token=convoy-regression-fixture'
```

The launcher arguments initialize the real SPT request handler; use a non-secret fixture token for this isolated process. The test uses ephemeral loopback servers and temporary game roots, not your SPT install. Mono tests do not prove native Unity startup or raid behavior.

For the real cross-repository bridge, build this harness, then run from the Quartermaster worktree (set `CLIENT` to this worktree's absolute path):

```bash
CONVOY_REQUIRE_LIVE=1 \
CONVOY_MONO_REGRESSION="$CLIENT/tests/SyncRegression/bin/Release/SyncRegression.exe" \
MONO_PATH="$CLIENT/Convoy/bin/Release" \
cargo test --test convoy_v2_e2e -- --nocapture
```

The historical test binary name is retained; its delivery contract is v3. It uses real loopback publisher handlers and isolated game roots. Set `CONVOY_V2_LARGE=1` to opt into the resource-intensive large/resume case; without it that case is not exercised. Native Windows/Unity behavior requires separate qualification.

## License

[GNU Affero General Public License v3.0](LICENSE)
