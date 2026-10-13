# Changelog

Player-visible changes per version, written for the Workshop. Internal
work (docs, tooling, process) stays out. Costs are quoted from the
blueprints and verified at each release.

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
