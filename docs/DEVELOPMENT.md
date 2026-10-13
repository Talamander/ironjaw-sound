# Ironjaw Sound for developers

## What is in a release

`IronjawSound-<version>.zip` unzips into `BepInEx/plugins/IronjawSound/`. Everything sits in that one folder:

| File | What it is |
|---|---|
| `map_ironjawsound` | The map: a Unity 2022.3 asset bundle with the terrain, its textures, the map prefab and its `MapDetails`. |
| `terrainlab_rocks`, `rocks_ironjawsound.bin` | Rock and boulder meshes, and where they stand on the map. |
| `trees/` | The snow spruce tree mesh and textures. |
| `buildings/IronjawSound.json` | Where the copies of stock buildings (houses, apartment and office blocks, industrial sheds, wind turbines, power pylons) are placed. |
| `courses/opt-1-v9/` | The three air race courses. |
| `Missions/` | The 15 missions, and `known-old-hashes.txt` (hashes of mission files from earlier releases, see below). |
| `TerrainLabNOPatch.dll` | Registers the map with NOMapLoader, gives it its terrain look, trees, ocean and grass, and checks the release files. |
| `TerrainLabGameplay.dll` | Installs the missions, places copies of the game's own buildings and sits them on the slope, names the map, and logs a short mission summary. |
| `TerrainLabScatter.dll` | Draws the rocks and gives them colliders. |
| `TerrainLabRace.dll` | Runs the air races. |
| `IronjawSound.release.json` | Name, version, date and the size and SHA-256 of every file in the release. |
| `README.txt`, `CHANGELOG.md`, `LICENSE`, `THIRD-PARTY-NOTICES.md` | Text. |

All four plugins only act on Ironjaw Sound. They check the loaded map's prefab name (`IronjawSound_*`) and do nothing
on any other map.

### How the map gets loaded

NOMapLoader 1.0.5 only loads bundles from its own folder. A mod manager installs every mod into its own folder,
so TerrainLabNOPatch loads `map_ironjawsound` itself, the same way NOMapLoader does, and adds the map to
NOMapLoader's map table (`NOMapLoader.Plugin.CustomMaps`). NOMapLoader reads that table later, when the main menu
and the mission editor build their map lists. See `plugins/TerrainLabNOPatch/src/Release.cs`.

### How the missions get installed

On start, TerrainLabGameplay copies every `Missions/Ironjaw Sound - *` folder into the game's mission folder
(`MissionGroup.UserGroup`; on a dedicated server, the folder the server config sets). For each mission file:

- missing: it is copied;
- the same as ours: nothing happens;
- a file from an earlier release (its hash is in `known-old-hashes.txt` or in
  `BepInEx/config/talamander.terrainlab.missions.state`): it is replaced;
- anything else: the player edited it, so the whole mission folder is kept and the log says so.

It never deletes anything. `InstallMissions = false` in `talamander.terrainlab.gameplay.cfg` turns it off. See
`plugins/TerrainLabGameplay/src/MissionInstaller.cs`.

## Building the plugins

The plugins target .NET Standard 2.1 and build against the game's own DLLs and BepInEx 5.4.23.5. Nothing from the
game or BepInEx is copied into the output.

```sh
cd plugins/TerrainLabNOPatch
dotnet build -c Release \
  -p:GameManagedDir="/path/to/Nuclear Option/NuclearOption_Data/Managed" \
  -p:BepInExCoreDir="/path/to/Nuclear Option/BepInEx/core"
```

The DLL lands in `bin/Release/netstandard2.1/`. Do the same for the other three plugins.

The map bundle, rocks, trees and missions are made in a separate terrain workspace from the elevation data, with
Python scripts, headless Blender and Unity in batch mode. That workspace is not in this repository.

## How a release is made and signed

1. The plugins are built, the version is set in every plugin and in `IronjawSound.release.json`, and
   `CHANGELOG.md` gets a new section.
2. The package script collects the files, checks them, writes the zip (same file order and dates every time),
   unzips it again to check it, and writes `SHA256SUMS`.
3. `SHA256SUMS` is signed with the release key:
   `ssh-keygen -Y sign -f <private key> -n file SHA256SUMS` writes `SHA256SUMS.sig`.
4. The signature is checked against [allowed_signers](../allowed_signers) before anything is uploaded.
5. The GitHub release gets the zip first, then `SHA256SUMS`, `SHA256SUMS.sig` and `allowed_signers`. The zip is
   the first asset because NOMM and NOMNOM take the first asset as the mod.
6. The NOMNOM manifest (`packaging/nomnom/IronjawSound.json`) gets a new entry at the top of `artifacts`, with the
   zip's URL and `sha256:` hash.

The key is an Ed25519 SSH key used only for Ironjaw Sound releases (principal `ironjaw-sound-release`, namespace
`file`). Its fingerprint is `SHA256:CQyIGTBz+aBQZyUJLL7gJOx5/pK+q500i8x5azawxJg`. If the key ever has to change, the
release notes of the first release with the new key say so, the new public key is added to `allowed_signers`, and
the old key stays listed so older releases still verify.
