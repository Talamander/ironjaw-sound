# Ironjaw Sound

A fictional fjord map for **Nuclear Option**, inspired by Prince William Sound, Alaska. Made by Talamander.

It comes with 11 missions, including a multiplayer **Escalation** mission. There are 7 airfields and 7 FARPs, plus towns, ports, bridges, lighthouses and power lines.

## Download

Get `IronjawSound-v9-2026-10-08.zip` from the [Releases](../../releases) page. Full step-by-step instructions are in `INSTALL.txt`, which is also inside the zip.

## What you need

| Item | Version | Where |
|---|---|---|
| Nuclear Option (Steam) | build 24724372 | Steam |
| BepInEx (Windows x64 build, also on Linux/Proton) | 5.4.23.5 | https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5 |
| NOMapLoader by MinecrackTyler | 1.0.5 | https://github.com/MinecrackTyler/NOMapLoader/releases/tag/v1.0.5 |
| This map | v9 (2026-10-08) | Releases page |

NOMapLoader is not included in the zip. Download it from its author's page.

## Quick install (Windows)

1. Quit the game. In Steam, right-click Nuclear Option, then choose Manage > Browse local files.
2. Unzip BepInEx 5.4.23.5 into that folder.
3. Unzip `IronjawSound-v9-2026-10-08.zip` into the same folder. Merge the BepInEx folder.
4. Put `NOMapLoader_1.0.5.dll` in `BepInEx\plugins\NOMapLoader\`, next to `map_ironjawsound`.
5. Only if you host: copy the folders in `IronjawSound-Missions\` to `%USERPROFILE%\AppData\LocalLow\Shockfront\NuclearOption\Missions\`.

On Linux (Proton), also set this Steam launch option: `WINEDLLOVERRIDES="winhttp=n,b" %command%`. See `INSTALL.txt`.

## Playing together

- **Host:** go to Multiplayer, host a game, and pick "Ironjaw Sound - Escalation".
- **Players who join:** they need the map and the three plugins. They do not need the mission files, because the host sends the mission.
- **Must match:** everyone needs the same map and plugin files. Compare them with `SHA256SUMS`.

## Uninstall

See section 5 of `INSTALL.txt`.

## Escalation update (2026-10-08, experimental)

The Escalation mission in the v9 zip has almost no ground war and very few AI aircraft. A rebuilt Escalation follows the stock Escalation pattern: aircraft and warhead factories on both sides, vehicle depots at Delta Gate and Cinder Gate, timed reinforcement convoys, and a tiered win (break the enemy forward depot, then destroy their aircraft factories). It also moves AI taxi routes off the runways.

Only the **host** needs it: download `IronjawSound-Escalation-esc2-2026-10-08.zip` from the [v9-esc2 pre-release](../../releases/tag/v9-esc2-2026-10-08), and replace the `Ironjaw Sound - Escalation` folder in your Missions folder with the one in the zip. Players who join get the mission from the host. This update has not been fully play-tested yet.

## Known issues

- In the v9 zip's missions, AI fixed-wing aircraft often fail to take off from the runway airbases. The Escalation update above includes a fix; the other missions get it in the next version.
- Two-player multiplayer is still being tested. If something breaks in multiplayer, open an issue here.
- A Nuclear Option update can break the map until it is rebuilt.

## Credits

- Map, plugins (TerrainLabGameplay, TerrainLabScatter, TerrainLabNOPatch) and missions: Talamander.
- Terrain is shaped from public-domain elevation data:
  - U.S. Geological Survey 3D Elevation Program;
  - NOAA National Centers for Environmental Information coastal relief models.
- NOMapLoader: MinecrackTyler.
- BepInEx: the BepInEx team.
- Nuclear Option: Shockfront. This is a fan-made mod, not affiliated with Shockfront. The stock buildings on the map are cloned at runtime from your own installed game. No game files are included.
