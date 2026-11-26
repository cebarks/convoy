# Convoy

BepInEx 5 plugin for [SPT](https://www.sp-tarkov.com/) that syncs client-side mods from a [Quartermaster](https://github.com/cebarks/quartermaster) server. Runs on game startup, downloads/updates/removes mods to match the server's catalog, then lets the game continue.

## How it works

1. Fetches and validates the v2 catalog and pins its origin and generation
2. Diffs against local state to determine what needs to be installed, updated, or removed
3. Streams only missing or changed objects, reuses verified local bytes and resumes validated partial transfers. Sizes and SHA-256 must match before files or sync state are changed. Sync and repair never use a mod ZIP.
4. Prompts for a restart if any BepInEx plugin files changed (they're already loaded by the time Convoy runs)

Installed-file checks use up to the logical processor count reported by .NET minus two readers, with a minimum of one. Up to sixteen distinct objects can download concurrently; Convoy still verifies them before making any live file changes.
When the filesystem supports protected preparation, verification also writes the future install files, and Apply moves those exact files into place without copying or rehashing them.

Optional mod groups are toggled via BepInEx Configuration Manager (F12 in-game).

## Supported files and ownership

Sync supports canonical `BepInEx/` paths, package dependency DLLs directly under `EscapeFromTarkov_Data/Managed/`, and existing SPT bundle-cache exports. Other game-root paths, traversal, drive paths, backslashes, Windows aliases, case collisions and symlink/reparse destinations are rejected before mutation. Missing, corrupt, duplicate or conflicting delivery identities abort the operation without deleting installed files or committing state.

Existing matching files are usable without being adopted: state records `owned: false`. Differing unowned files and locally modified assemblies cause a conflict rather than an overwrite. Existing `BepInEx/config/` files are preserved, including when package defaults change. Only unchanged owned files no longer needed by any selected mod are removed. Shared dependencies survive removal of one owner. Legacy state ownership applies only to BepInEx, never Managed assemblies. Same-version advertised manifest changes trigger synchronization.

File replacement/deletion is preflighted and rolled back on ordinary I/O failures; state uses atomic replacement. This is not a crash-durable journal or protection against hostile concurrent local filesystem mutation. Do not run other installers during sync. Unique `.convoy-old-*` backups may remain when Windows holds a loaded DLL; unrelated backup files are not recursively deleted.

### Coordinated publisher upgrade

Upgrade this client and Quartermaster as a pair. A v2 client on a legacy publisher stops and shows the upgrade instruction without attempting legacy downloads or changing live files. An old client cannot use v2 delivery. Do not depend on self-update to cross this boundary. The existing setup ZIP is separate, not a safe overlay installer for a populated game directory.

Before cutover, preserve the previous binaries, server config and compatible database and the client plugin/configuration/ownership state. Roll back both ends with their compatible data, not by running old software against new state.

## Installation

Close the game. Install the standalone v2 package under `BepInEx/plugins/Convoy/`. For a local source build with `DeployAfterBuild=false`, the two inputs are `Convoy/bin/Release/Convoy.dll` and `Convoy/bin/Release/System.IO.Hashing.dll`, placed at `BepInEx/plugins/Convoy/Convoy.dll` and `BepInEx/plugins/Convoy/System.IO.Hashing.dll` in the package. Do not include game assemblies or regression artifacts. This is not a claim that a v2 build has been published. Release naming and tagging are separate operator decisions; the project derives the plugin version from Git tags.

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
  '-config={"BackendUrl":"http://127.0.0.1:1","Version":"live"}' '-token=convoy-regression'
```

The launcher arguments initialize the real SPT request handler; use a non-secret fixture token for this isolated process. The test uses ephemeral loopback servers and temporary game roots, not your SPT install. Mono tests do not prove native Unity startup or raid behavior.

For cross-repository publisher tests, build this harness, then run Quartermaster's `managed_dependency_publisher_delivery_setup_and_cache` integration test with `CONVOY_MONO_REGRESSION` set to the absolute `SyncRegression.exe` path and `MONO_PATH` set to this client's Release output. `CONVOY_FIXTURE_EXPORT` optionally captures synthetic catalog/archive fixtures in a private directory. The integration starts real Quartermaster handlers on loopback, runs the consumer against fresh and populated/repaired roots, and stops the listener. Without the environment variable the ordinary Rust test still verifies both archive routes, setup contents and warm-cache invalidation.

## License

[GNU Affero General Public License v3.0](LICENSE)
