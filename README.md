# RaidLimit PoC

## This plugin is experimental!

**I built the plugin in around a week as a proof of concept to see if it was possible to limit raids effectively in Rust. It's not an easy task since Rust is such a complex and dynamic game. The plugin is not a perfect raid limiting solution, but it does effectively limit raids. There are some loop holes that players will most likely find and abuse, the best way to find any abusers is to monitor raids with logging tools and admins. There might be certain instances where the plugin doesn't allow players to raid a building if it was built by different players from different teams (a raided base or a decayed base someone else has taken over).**

**Anyone is welcome to test out and try the plugin but I doubt I will be putting any more work into it.**

### How does it work?
- Allowed 2 raids every 24 hours
- All players that are associated with each other share the same raid limit cooldowns
- Raid limit groups/associations are automatically created through teams. Associations last for the entire wipe, be aware of who you team with!
- Attacking an enemy base with an explosive will cost 1 raid point
- Left click while holding a hammer or toolgun while looking at a building to check if you can raid it
- Raid blocked explosives will not be refunded!
---
- Raids against your own/ally building are FREE
- Raids against an enemy/enemy ally that has already raided you are FREE for X hours (use hammer or toolgun to check time remaining on free raid)
- You can raid any enemy/enemy ally base anywhere on the map if you have used a raid point on one of their bases already
- Buildings with no TC are FREE to raid
- Eco raiding is FREE (spears, grenades, eoka, fire, etc)

## Configuration

There is no config file, the only 2 config variables are at the top of the plugin file.

 - `RAID_LIMIT` - Number of raids a raid group is limited to
 - `RAID_LIMIT_RESET_HOURS` - Number of hours it takes for an individual raid point to reset

## UI

The Raid Limit UI is on the left side of the hot bar showing the number of raid points available.

`RAID LIMIT: (AVAILABLE_POINTS / RAID_LIMIT)`

## Wipe

Data files are automatically reset when a wipe is detected.

- `/oxide/data/RaidLimit/PlayerAssociations.json`
- `/oxide/data/RaidLimit/RaidLimits.json`

## Player Commands

- `/rlc` - Check your raid limit cooldowns
- `/rla` - Check the players in your raid limit group/association

## Admin Commands

- `/rl.live` - View the raid groups active on the server (similar to admin radar)

## Server Commands

- `rl.reset` - Force reset raid limits for all players
