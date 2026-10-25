# Changelog

Player-visible changes per version, written for the Workshop. Internal
work (docs, tooling, process) stays out. Costs are quoted from the
blueprints and verified at each release.

## 0.5.0

Every crossing is instant now, on both teleporters, and runs on the
mod's own machinery.

### New

- Both plates of a ring pair pulse their transport rings at the same
  moment when someone crosses, so each end marks the trip together.
- A path platform can be built directly over a ring now.

### Changed

- Every crossing is instant. A beaver steps onto a plate and
  rematerializes at its destination. In 0.4.0, every crossing was a
  fixed short flight, with the traveler hidden on wormholes and a
  glowing teardrop crossing at tubeway speed on rings.
- The Ring Station occupies a single flat tile now, down from two.
- The ring plate reads as a metal disc set into its dirt tile now,
  instead of rendering as the dirt tile itself.
- Extract goes four times further. One extract covers forty crossings
  on both teleporters, up from ten, and a full store holds 25 extract
  instead of 100.
- The wormhole siphon moves one extract at a time, down from five, and
  a source keeps 4 in reserve, down from 20.

### Fixed

- The 0.4.0 bug where a beaver or bot could get stuck mid-crossing or
  wander off through the air.
- Placing a teleporter beside a district center or another teleporter
  no longer fails with a false warning about a second district.
- Saves from 0.4.0 keep loading.

## 0.4.0

Ring Station pairs: teleport platforms that need no path between them.

### New

- Ring Station, the mod's second teleport building. Built as a pair on
  your paths. One extract covers ten crossings, shared between the two
  halves from the built ring's store.
- The remote half is free and needs no paths. Place it from the built
  ring's panel, anywhere within 30 tiles, even across a gap or a river,
  or at different elevation.
- Ring crossings run at tubeway speed. The plate reads as a path tile,
  and its route inscription lights while the pair can carry someone.
- Pause a ring pair from the built ring's panel, or with levers, timers,
  and sensors. The remote follows its partner.

### Changed

- Wormhole crossings are now a fixed short flight with the beaver
  hidden, instead of an instant jump. Same for rings.
- The teleporter is now named Wormhole Station, and the pair halves are
  the Ring Station and the Remote Ring Station.
- The two teleport buildings now sit together in the build menu, ahead
  of the tubeways.

## 0.3.0

Teleporters can be paused.

### New

- Pause a teleporter from its panel, or with the P key. A paused
  teleporter's wormhole is off and nothing moves its extract. No
  travel, no deliveries, and the siphon neither pulls nor gives.
  Unpausing puts everything back.
- Levers, timers, and sensors can pause teleporters automatically,
  like any other building.

## 0.2.0

Teleporters now run on extract and live on one district.

### New

- Extract-powered stations. One extract covers ten crossings, drawn at
  the station the beaver walks into. An idle network costs nothing, and
  the vanilla empty-building flow reclaims extract.
- One district owns the teleporters. The first station placed on a
  district's paths binds them there. A ghost on any other district's
  paths turns red.
- Stations work away from paths. One on unclaimed ground joins the
  network through the wormhole.
- Wormhole siphon. A station that runs low pulls 5 extract from a
  source you set in a path-connected station's panel. Sources keep 20
  in reserve.
- A dark network is truly dark. When no path-connected station holds
  extract, the wormholes close until one is refilled.
- Status icons: a station says whether it is out of extract, offline,
  or cut off.
- Click a station to highlight its whole network.
- New panel: extract and transits rows, the station count, and the
  siphon switch.

### Changed

- Costs 5000 science (was 500) and 50 extract (was 500).
- Dry or disconnected stations hide their horizon.

### Removed

- Connect and disconnect buttons. Stations join on their own; demolish
  a station to remove it.
- The matter transfer button. The wormhole siphon replaces it.

### Fixed

- 0.1.x saves with teleporters on several districts load with every
  network offline. Demolish one district's teleporters and the rest
  join the survivor's network on their own.
