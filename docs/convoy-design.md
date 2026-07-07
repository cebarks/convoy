# Convoy — Mod Delivery & Management System

**Date:** 2026-07-05
**Status:** Design
**Replaces:** NarcoNet integration (`src/modsync.rs`)

## Problem

Quartermaster currently depends on NarcoNet, a third-party BepInEx mod, for client-side mod synchronization. This creates problems:

- **Dependency risk** — NarcoNet is maintained by a third party; upstream changes, abandonment, or bugs block Quartermaster.
- **Poor integration** — NarcoNet has its own config format (`config.yaml`), its own directory layout (`quma-<slug>/`), and its own protocol. Quartermaster wraps it but doesn't own it.
- **Architecture mismatch** — NarcoNet syncs at the file level (per-file hash comparison). Quartermaster thinks in mods (Forge IDs, versions, archives). The impedance mismatch causes complexity (runtime exclusion detection, per-file manifests, physical directory shuffling for groups).
- **Limited player experience** — NarcoNet syncs on server connect, not pre-launch. No mod discovery or optional mod browsing for players.

## Solution

**Convoy** is a clean-sheet replacement: a mod delivery and management system built into Quartermaster with a companion BepInEx client plugin. It owns the full stack — server API, sync protocol, and client — with no third-party dependencies.

### Core Principles

1. **Mod-level sync, file-level verification.** Sync decisions happen at the mod level (Forge ID + version). File hashes verify correctness after extraction.
2. **Two tiers: required and optional.** No per-mod enforcement/silent/restart flags. A mod is required or optional, determined by its group.
3. **Groups are organizational, DB-backed.** No filesystem layout (`quma-<slug>/` directories are gone). Groups bucket mods for admin convenience and set the tier for all their members.
4. **Pre-launch sync.** The client plugin syncs on game startup, before connecting to any server. No mid-session sync complexity.
5. **Self-contained client.** One BepInEx DLL, no external dependencies. Uses BepInEx Configuration Manager (F12) for optional mod toggles.

## Architecture

### Server Side (Quartermaster)

New module `src/convoy/` replaces `src/modsync.rs`.

#### Catalog (`catalog.rs`)

Builds a JSON catalog of all synced mods from DB state. Cached on disk using the same background-rebuild + dirty-flag pattern as `ModZipCache`. Invalidated whenever mods or groups change.

**Catalog format:**

```json
{
  "spt_version": "3.11.2",
  "groups": [
    {
      "slug": "default",
      "name": "Default",
      "tier": "required",
      "mods": [
        {
          "id": 42,
          "forge_id": 1234,
          "name": "SAIN",
          "version": "3.2.1",
          "file_checksums": {
            "BepInEx/plugins/SAIN/SAIN.dll": "def456...",
            "BepInEx/plugins/SAIN/config.json": "789abc..."
          }
        }
      ]
    },
    {
      "slug": "cosmetics",
      "name": "Cosmetic Mods",
      "tier": "optional",
      "mods": [...]
    }
  ],
  "exclusions": ["BepInEx/plugins/SAIN/BotTypes.json"]
}
```

#### Download (`download.rs`)

Handles batched mod archive downloads. Client sends the list of mod DB IDs it needs, server builds a ZIP on the fly from the installed files on disk (original archives are not retained after extraction) and streams the response. The ZIP is structured so each mod's files are at their correct relative paths (e.g., `BepInEx/plugins/SAIN/SAIN.dll`).

#### Groups (`groups.rs`)

Group CRUD backed by the `mod_groups` DB table. Groups have a name, slug, tier (required/optional), and an `exclude_headless` flag for headless client sync integration. (Groups are always displayed in alphabetical order.)

### API Endpoints

| Method | Path | Auth | Purpose |
|--------|------|------|---------|
| `GET` | `/quma/convoy/catalog` | None | Mod catalog for client sync (ETag supported) |
| `POST` | `/quma/convoy/download` | None | Batched mod archive ZIP download |
| `GET` | `/quma/convoy/mod/{id}/archive` | None | Single mod archive download (fallback) |

No auth on sync endpoints — access is gated by knowing the server address, which is controlled through the invite/join flow.

### Database Changes

**New table: `mod_groups`**

| Column | Type | Description |
|--------|------|-------------|
| `id` | INTEGER PK | Auto-increment |
| `name` | TEXT NOT NULL | Display name |
| `slug` | TEXT NOT NULL UNIQUE | URL-safe identifier |
| `tier` | TEXT NOT NULL | `'required'` or `'optional'` |
| `exclude_headless` | BOOLEAN NOT NULL DEFAULT 0 | Exclude from headless client sync |

**Modified table: `installed_mods`**

Add column: `group_id INTEGER REFERENCES mod_groups(id) ON DELETE SET NULL`

NULL `group_id` = belongs to the implicit "Default" required group.

### Config Changes

The `[modsync]` TOML section is replaced by `[convoy]`:

```toml
[convoy]
enabled = true
exclusions = ["BepInEx/plugins/SAIN/BotTypes.json"]
```

Groups and tiers live in the DB, not config. The only config-level settings are the master switch and exclusion patterns for runtime-generated files.

### Web UI

The modsync page (`/quma/modsync`) becomes the Convoy page (`/quma/convoy`).

**Groups management:**
- Create/edit/delete groups
- Set tier (required/optional) per group
- Assign mods to groups
- Set `exclude_headless` flag

**Per-mod view:**
- Which group each mod belongs to
- Effective tier

**Catalog preview:**
- View the generated catalog JSON

### Catalog Invalidation

Same pattern as `ModZipCache`: an `invalidate()` method triggers a background rebuild via `tokio::task::spawn_blocking`. Uses `AtomicBool` flags for `rebuilding` and `dirty` so concurrent invalidations coalesce. Called after any mod install/update/remove/group change.

---

### Client Side (BepInEx Plugin)

**`Convoy.dll`** — a BepInEx 5 plugin written in C#.

#### Components

- **`ConvoyPlugin.cs`** — BepInEx plugin entry point. Registers config entries, kicks off sync on `Awake()`.
- **`ConvoyConfig.cs`** — BepInEx `ConfigEntry` definitions. One `bool` toggle per optional group (auto-populated from catalog). Section name = group display name, entry description lists member mods. Also stores the server URL.
- **`SyncEngine.cs`** — Core sync logic: fetch catalog, diff against local state, download batch ZIP, extract, verify, update state.
- **`State.cs`** — Reads/writes `BepInEx/config/Convoy/state.json` tracking installed mods.

#### Local State Format

```json
{
  "server_url": "http://192.168.1.50:9190",
  "last_catalog_etag": "\"a1b2c3\"",
  "mods": [
    {
      "id": 42,
      "version": "3.2.1",
      "files": [
        {"path": "BepInEx/plugins/SAIN/SAIN.dll", "hash": "def456..."}
      ]
    }
  ]
}
```

#### Sync Flow

1. Read `state.json` (or initialize empty on first run)
2. `GET /quma/convoy/catalog` with `If-None-Match` header (ETag from last fetch)
   - `304 Not Modified` → nothing changed, skip sync
   - `200 OK` → new catalog, continue
3. Read BepInEx config for optional group toggles
4. Diff local state against catalog:
   - Required mod missing → install
   - Required mod version differs → update
   - Local mod not in catalog → remove
   - Optional group toggled on, mod missing → install
   - Optional group toggled off, mod installed → remove
5. If no changes → done, game continues
6. Log summary to BepInEx console
7. `POST /quma/convoy/download` with `{"mods": [42, 43]}` (mod DB IDs to download)
8. Extract archives, verify file hashes against catalog's `file_checksums`
9. Delete files for removed mods
10. Update `state.json` with new ETag and mod state
11. If any files changed → prompt restart (BepInEx plugins are already loaded, new DLLs need a fresh launch)

#### Optional Mod UI (BepInEx Configuration Manager)

Players open F12 to access the BepInEx Configuration Manager. Convoy registers a config section per optional group:

- **Section:** group display name (e.g., "Cosmetic Mods")
- **Entry:** single bool toggle, default off
- **Description:** lists all mods in the group with name and version

Players toggle groups on/off. Changes take effect on next game launch.

#### First-Run / Join Flow

When a new player joins via the Quartermaster web UI, the join flow zip includes:
- `Convoy.dll` (the BepInEx plugin)
- Pre-seeded config with the server URL

On first launch, Convoy sees an empty `state.json` and performs a full sync — downloading all required mods and any optional groups the player has toggled on (none by default).

---

## Migration

### What Gets Removed

- `src/modsync.rs` — entire file
- `ensure_mod_layout()` and all `quma-<slug>/` directory management
- `ModSyncConfig`, `ModSyncGroup`, `ModSyncOverride` from `src/config.rs`
- NarcoNet detection (`find_narconet_dir`, `is_modsync_installed`, `NARCONET_FORGE_MOD_ID`)
- NarcoNet `config.yaml` generation
- `src/web/handlers/modsync.rs`
- Modsync templates (`templates/modsync/`)

### What Gets Migrated

Migration runs in this order:

1. **Filesystem un-layout:** A one-time migration moves files out of `quma-<slug>/` directories back to their original locations (un-does `ensure_mod_layout`) and updates `installed_files` paths in the DB. This runs first because the modsync code that created the layout is being removed.
2. **Groups:** DB migration reads `[modsync.groups]` from the config TOML, creates `mod_groups` table, inserts rows, and sets `group_id` on matching `installed_mods` entries.
3. **Config:** `[modsync]` section is read, migrated to `[convoy]` (keeping `enabled` and `exclusions`), and the old section is removed on next config write.
4. **`exclude_headless`:** Preserved as a flag on `mod_groups`.
5. **Runtime exclusion detection:** `collect_runtime_exclusions()` logic moves into the convoy catalog builder, populating the `exclusions` list.
6. **Join flow:** Updated to bundle `Convoy.dll` instead of NarcoNet's client plugin.

### Breaking Changes

- Players must install the Convoy plugin (delivered via the updated join flow). NarcoNet is no longer supported.
- Server admins see the modsync page replaced by the convoy page. Existing group config is auto-migrated.
- The `[modsync]` TOML section is replaced by `[convoy]`. Old config is auto-migrated on first load.

## Scope Boundary

This spec covers the server-side Quartermaster changes AND the client-side BepInEx plugin. The C# plugin is a separate project/repository but is designed and built as part of this effort.

Out of scope:
- Delta/binary diff transfers (whole mod archives for now)
- Versioned manifests / changelog-based sync
- Mid-session sync or background update checking
- Web-based mod browsing for players (F12 config panel is the UI)
