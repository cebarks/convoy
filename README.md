# Convoy

BepInEx 5 plugin for [SPT](https://www.sp-tarkov.com/) that syncs client-side mods from a [Quartermaster](https://github.com/cebarks/quartermaster) server. Runs on game startup, downloads/updates/removes mods to match the server's catalog, then lets the game continue.

## How it works

1. Fetches the mod catalog from your Quartermaster server (with ETag caching)
2. Diffs against local state to determine what needs to be installed, updated, or removed
3. Downloads changed mods as a batch ZIP and stages only catalog-listed files. Every requested file must be present and match its xxHash3 checksum before files or sync state are changed.
4. Prompts for a restart if any BepInEx plugin files changed (they're already loaded by the time Convoy runs)

Optional mod groups are toggled via BepInEx Configuration Manager (F12 in-game).

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

The launcher arguments initialize SPT's request handler; the test uses an ephemeral loopback server and temporary game roots, not your SPT install.

## License

[GNU Affero General Public License v3.0](LICENSE)
