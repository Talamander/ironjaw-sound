# Changelog

All notable changes to Ironjaw Sound. Versions follow semantic versioning from 1.0.0 on. Earlier builds were
named after the terrain build (v9) and a date.

## 1.0.0 (2026-10-13)

First packaged release. One zip installs everything into one folder, by hand or with NOMM.

### New
- **Supply Line** mission. Fjord Confrontation with a supply war on top: carry crates under a helicopter to push
  captures, build supply dumps at the landing zones, and earn income from the dumps you hold.
- **Air Race** missions: Fjord Sprint, Glacier Run and Grand Tour. The new TerrainLabRace plugin gives the host an
  air start grid, gate rings, a race timer, splits and results (`!race grid`, `!race go`, `!race reset`).
- Missions install themselves. On start, TerrainLabGameplay copies the "Ironjaw Sound - *" missions into the game's
  mission folder and updates the ones from earlier releases. Missions you edited are left alone.
- The log names the release on start ("Ironjaw Sound 1.0.0") and checks that every file of the release is present
  with the right size.

### Map
- Hollin Mine: the trees and rocks are cleared from the valley floor and the mine site is built out, so the landing
  zone, pads and supply dump are usable.
- New road links, including a track out of the Cinder Gate basin, so ground units can reach the rest of the road
  network. A Primeva armour column now drives the Tarn Head to Hollin road in Fjord Confrontation and Supply Line.

### Missions (fixes since v9, first shipped in the 2026-10-08 and 2026-10-09 mission updates)
- Escalation rebuilt on the pattern of the stock Escalation missions.
- AI aircraft no longer spawn inside the ground: spawn buildings sit on the apron surface and revetments are
  raised.
- AI aircraft taxi on paved apron and connector lanes and take off from the runway intersection. Nobody taxis on a
  runway against take-off traffic.
- Ships in Port Ansel Defence stay on water.
- Mountain-top radar network in Escalation, Fjord Confrontation, Ostrin Confrontation and Island Hop.

### Install and performance
- Single folder `BepInEx/plugins/IronjawSound`. The map bundle no longer goes into the NOMapLoader folder:
  TerrainLabNOPatch registers it with NOMapLoader itself.
- Much quieter log. Diagnostic dumps and periodic statistics are off by default (tree and terrain material dumps,
  albedo statistics, ocean properties, camera ticks, rock draw statistics, per-building ground checks).
- No config files ship. Every setting has a safe default, and your own settings are never overwritten.
- All four plugins are version 1.0.0.

## v9 (2026-10-08)

Ninth terrain build, first shared build. Map bundle, TerrainLabGameplay, TerrainLabScatter and TerrainLabNOPatch,
12 missions, installed by hand into `BepInEx/plugins/NOMapLoader` and three plugin folders.
