# Changelog

All notable changes to Ironjaw Sound. Versions follow semantic versioning from 1.0.0 on. Earlier builds were
named after the terrain build (v9) and a date.

## 1.0.3 (2026-10-24)

### New
- **Escalation North** mission. Escalation with the Primeva aircraft industry moved far from the front: one factory
  complex on the Graylock plain and one in the Brekka Hills, the enrichment plant at the head of Graylock Fjord, each
  with a long-range SAM battery and point defence. Graylock Strip is harder to capture. The factories of each complex
  stand hundreds of metres apart, so one strike no longer takes out a whole complex. Thanks to migmadmarine for the
  idea.
- Every Ironjaw Sound mission has its own picture in the mission picker: a map of the area with the objectives, the
  ships and their routes, the airbases and (for the races) the course, in place of the stock image.

### Changed
- **Ostrin Confrontation** no longer ends after a few minutes. The Primeva destroyer in the narrows could reach the
  Boscali factories with its anti-ship missiles, and the Boscali destroyer and frigate could shell Graylock and Brekka
  from the sea. Each is now a corvette, and the Boscali carrier group sits south of Kittiwake. The factories of both
  sides are spread out and each complex has its own air defence.
- **Gannet Sound Defence**: the Primeva carrier and landing group start much further out and the landing run comes
  later. Corvettes instead of a destroyer and frigate escort the Primeva carrier, which could burn the Rook station
  with anti-ship missiles from over 20 km. Rook Island has a long-range SAM battery, two Boltstrike sites and more
  point defence.
- **Port Ansel Defence**: the Primeva assault fleet used to sail straight to within gun range of the port and shell
  the tank farm in about six minutes. It now waits about 30 km out, east of Ansel Reach, and moves in after ten
  minutes, and the second wave comes later. A second frigate replaces its destroyer, whose railgun could reach the
  port from 25 km. Port Ansel has a long-range SAM battery, two Boltstrike sites, guns at the tank farm and a second
  corvette.
- **Sill Strike**: the Boscali group is the carrier with two corvettes, further back; sinking the carrier is the
  objective. The Sill has a long-range SAM battery, fjord pickets and point defence, with Boltstrikes at Brekka and
  Graylock.
- **SAM Hunt**: seven Primeva SAM sites instead of four (new ones on Ironjaw Crown, on the south shore of the narrows
  and above Dunmore Crossing), plus two mobile pickets.
- Thanks to Antithesis for the detailed balance reports.

### Fixed
- With NOCustomMaps installed next to Ironjaw Sound, its maps (Swiss Alps and others) were missing from the mission
  editor's New Mission panel. TerrainLabNOPatch now hands the map tiles back to the game's own row so NOCustomMaps can
  list them all together, and every tile selects its own map. Setting: `NOCustomMapsCompat` in the `[General]`
  section of the TerrainLabNOPatch config. Thanks to migmadmarine for the report and Javoski for the help.
- Houses and blocks of flats in some towns lay on their side. Their stock models are turned inside the stock map, and
  only part of that turn was copied. Every stock building now stands upright.

### Plugins
- All four plugins are version 1.0.3. TerrainLabNOPatch and TerrainLabGameplay have code changes. The map bundle is the same file as in
  1.0.0 to 1.0.2, so players on all four versions can play together. The changed missions update themselves on the
  next start unless you edited them.

## 1.0.2 (2026-10-19)

### Fixed
- Escalation: the Primeva AI now captures bases. Its starting armour column sat in the closed Cinder Gate basin,
  about 30 km of road from the nearest base, and never arrived in time, so Boscali took Hollin Mine and Dunmore
  Crossing and Primeva took nothing. The column now starts on the Notch road, about the same distance from Dunmore
  Crossing as the Boscali column is from Hollin Mine. Thanks to GryphusOne for the report.
- The bright white outline along shores that face the sun is much weaker. The shore foam of the water shader is
  turned down on Ironjaw Sound (new `ShoreFoam` setting in the `[7.Ocean]` section of the TerrainLabNOPatch config;
  empty it to get the stock foam back). The water colour is unchanged.

### Plugins
- All four plugins are version 1.0.2. Only TerrainLabNOPatch has code changes. The map bundle is the same file as in
  1.0.0 and 1.0.1, so players on all three versions can play together. The Escalation mission updates itself on the
  next start unless you edited it.

## 1.0.1 (2026-10-17)

### Fixed
- Trees and rocks no longer stand on helipads, FARPs, landing zones, factories, fuel tanks and other buildings that
  a mission places. TerrainLabGameplay clears them at runtime on Ironjaw Sound, so this also covers missions made by
  other people. Settings are in the `[TreeClear]` section of the TerrainLabGameplay config.
- Nuke and bomb scorch decals now draw on the Ironjaw Sound terrain, as they do on the stock maps. The terrain tiles
  were missing the rendering layer that decals need.

### Plugins
- All four plugins are version 1.0.1. TerrainLabRace has no changes. The map bundle is the same file as in 1.0.0, so
  1.0.0 and 1.0.1 players can play together.

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
