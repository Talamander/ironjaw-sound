# NOMNOM entry

`IronjawSound.json` is the entry for [NOMNOM](https://github.com/KopterBuzz/NOMNOM), the registry that
NOMM (Nuclear Option Mod Manager) installs mods from. It follows NOMNOM's `SCHEMA.md` and passes its
`ValidationSchema.json` and the content checks of `Validate-JsonContent.ps1` (file name equals `id`,
id not taken, https URLs, archive file names, numeric versions). Submitted for 1.0.0 on 2026-10-14.

## Choices

- `id` `IronjawSound`: the mod has four plugin assemblies (`TerrainLabGameplay`, `TerrainLabNOPatch`,
  `TerrainLabRace`, `TerrainLabScatter`) plus the map data, so no single assembly name fits. The id is the
  folder a manual install uses (`BepInEx/plugins/IronjawSound/`), so NOMM and a manual install share one
  folder. All four plugins report version 1.0.0, the same as the release tag.
- `isClientOrServer` `Both`: everyone in a multiplayer game needs the map, the host included.
- `tags`: `Terrain` and `Map`, as the other map in NOMNOM uses.
- `dependencies`: `NOMapLoader` (MinecrackTyler), which loads custom maps. NOMM installs it first.
  NOMapLoader is not bundled. It is a plain dependency, not `type: addon` with `extends`, because
  NOMapLoader only scans its own folder; `TerrainLabNOPatch` registers `map_ironjawsound` from
  `plugins/IronjawSound/` itself. NOMNOM's hourly job keeps the dependency version current.
- No BepInEx dependency: NOMM installs BepInEx 5 itself.
- `imageUrl` and `imageHash`: the icon, `docs/media/icon-512.png` (PNG, 512x512, NOMNOM's limit), as a
  raw.githubusercontent.com URL pinned to a commit so the file behind it never changes. `imageHash` is
  the file's SHA-256 as plain lowercase hex with no `sha256:` prefix, the format NOMM's
  `PreviewImageLoader.kt` compares. To change the icon after the merge, use the "Update Mod Image URL"
  issue template (`HOWTO_UPDATE_MODIMAGE.md` in NOMNOM) with a new pinned URL.
- `gameVersion` `0.34.2`: the game build 1.0.0 was tested on, which reports Early Access 0.34.2 in the
  main menu (its Unity player version string still reads 0.34.1).

## How NOMM installs it

- NOMM downloads the artifact's `downloadUrl` (about 420 MB), checks it against `hash`, unpacks the whole
  archive into `BepInEx/disabledPlugins/IronjawSound/` and moves the folder to `BepInEx/plugins/IronjawSound/`
  when the mod is turned on. The zip has its files at the root, so nothing nests.
- `autoUpdateArtifacts` `"True"`: NOMNOM's hourly job reads the **first** asset of each new GitHub release
  (`Update-ModArtifact.ps1`) and turns the tag (`v1.0.1` becomes `1.0.1`) into the artifact version.
  GitHub lists release assets by name, not upload order, so no other asset may sort before
  `IronjawSound-<version>.zip`. The signer file is therefore uploaded as `SHA256SUMS.allowed_signers`
  (renamed on the 1.0.0 release on 2026-10-14). Keep tags plain `vX.Y.Z`.
- The early test builds (tags `v9-2026-10-08`, `v9-esc2-2026-10-08`, `missions-2026-10-09`) are draft
  releases since 2026-10-14, so NOMNOM never sees their non-numeric tags.
