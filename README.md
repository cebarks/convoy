# Convoy

BepInEx 5 plugin for [SPT](https://www.sp-tarkov.com/) that syncs client-side mods from a [Quartermaster](https://github.com/cebarks/quartermaster) server. Runs on game startup, downloads/updates/removes mods to match the server's catalog, then lets the game continue.

## How it works

1. Fetches the mod catalog from your Quartermaster server (with ETag caching)
2. Diffs against local state to determine what needs to be installed, updated, or removed
3. Downloads changed mods as a batch ZIP and stages only catalog-listed files. Every requested file must be present and match its xxHash3 checksum before files or sync state are changed.
4. Prompts for a restart if any BepInEx plugin files changed (they're already loaded by the time Convoy runs)

Optional mod groups are toggled via BepInEx Configuration Manager (F12 in-game).

## Supported files and ownership

Sync supports canonical `BepInEx/` paths, package dependency DLLs directly under `EscapeFromTarkov_Data/Managed/`, and existing SPT bundle-cache exports. Other game-root paths, traversal, drive paths, backslashes, Windows aliases, case collisions and symlink/reparse destinations are rejected before mutation. Missing, corrupt, duplicate or unadvertised archive contents abort the operation without deleting installed files or committing state.

Existing matching files are usable without being adopted: state records `owned: false`. Differing unowned files and locally modified assemblies cause a conflict rather than an overwrite. Existing `BepInEx/config/` files are preserved, including when package defaults change. Only unchanged owned files no longer needed by any selected mod are removed. Shared dependencies survive removal of one owner. Legacy state ownership applies only to BepInEx, never Managed assemblies. Same-version advertised manifest changes trigger synchronization.

File replacement/deletion is preflighted and rolled back on ordinary I/O failures; state uses atomic replacement. This is not a crash-durable journal or protection against hostile concurrent local filesystem mutation. Do not run other installers during sync. Unique `.convoy-old-*` backups may remain when Windows holds a loaded DLL; unrelated backup files are not recursively deleted.

### Coordinated publisher upgrade

There is no capability handshake. Release this client as a distinct mod version, upgrade it via the old publisher's existing BepInEx-only Convoy bootstrap/self-update operation, and restart clients **before** enabling Managed publication. Do not combine dependency-bearing mod updates with the bootstrap. Old clients accept roots without these ownership protections and miss same-version manifest changes. New clients reject old-server archives with unadvertised dependencies, so dependency installations must wait until publisher rollout finishes. BepInEx-only old-server archives remain supported. Mixed-version enforcement requires a separately reviewed protocol policy; these changes do not silently omit dependencies to accommodate old clients.

The independent player setup ZIP can include the dependency DLLs, but is not a safe overlay installer for populated game directories. Existing setup-installed or manually repaired matching DLLs remain unowned. No game reference assemblies are distributed in source tests.

## Installation

Copy the contents of `Build/BepInEx/plugins/Convoy/` (includes `Convoy.dll` and `System.IO.Hashing.dll`) to `BepInEx/plugins/Convoy/` in your SPT install directory.

On first run, Convoy creates its config at `BepInEx/config/io.cebarks.convoy.cfg` — set `ServerUrl` to your Quartermaster instance (e.g. `http://192.168.1.50:9190`).

## Building

Requires .NET SDK (targets net472).

```bash
dotnet build                              # debug build
dotnet build -c Release                   # release build
dotnet build -p:TarkovDir=/path/to/spt/   # override game path
```

Build output: `Build/BepInEx/plugins/Convoy/Convoy.dll`

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
