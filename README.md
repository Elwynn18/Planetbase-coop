# Planetbase Co-op

Online co-op for [Planetbase](https://store.steampowered.com/app/403190/Planetbase/) (Madruga Works, 2015):
two or more players run one colony together. One player hosts; everyone else joins and plays in the
host's colony. A [BepInEx 5](https://github.com/BepInEx/BepInEx) plugin, MIT licensed.

**Status: alpha.** It passes automated tests with two copies of the game on one PC (direct IP), and one
first playtest by two people over Steam (version 0.1.0; its bugs are fixed in 0.2.0, see
[Changes](#changes)). Expect bugs; see [Known issues](#known-issues).

## Requirements

- Planetbase **1.4.2** from Steam (Windows build, Steam build id 20301361). Everyone needs the exact same
  game version: the mod compares versions on connect and refuses a mismatch with a message saying what
  differs.
- Windows, or Linux with Proton (tested with Proton 9.0). Linux players run the same Windows build
  through Proton, so Windows and Linux players can play together.
- [BepInEx 5.4.23.5, Windows x86 (32-bit)](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5):
  the file `BepInEx_win_x86_5.4.23.5.zip`. Planetbase is a 32-bit game, so the x64 build will not load.
- The same `PlanetbaseCoop.dll` for every player (the host's exact file). Different builds are refused.
- No other mods that change the game's own files. The version check compares the game's
  `Assembly-CSharp.dll` byte for byte, so a mod that patches it on disk shows up as "Different game
  build". Steam > Properties > Installed Files > Verify integrity restores the original file.

## Install on Windows

1. In Steam: right-click Planetbase > Manage > Browse local files. This opens the game folder
   (the one with `Planetbase.exe`). Call it `<game dir>`.
2. Unzip `BepInEx_win_x86_5.4.23.5.zip` into `<game dir>`. You should now have
   `<game dir>\BepInEx\`, `<game dir>\winhttp.dll` and `<game dir>\doorstop_config.ini` next to `Planetbase.exe`.
3. Start the game once and quit. BepInEx creates `<game dir>\BepInEx\plugins\`.
4. Copy `PlanetbaseCoop.dll` into `<game dir>\BepInEx\plugins\`.
5. Start the game. Press **F8** on the main menu: the "Planetbase Co-op" panel opens.

## Install on Linux (Steam + Proton)

1. Steps 1 to 4 above, on the game folder (Steam > Manage > Browse local files).
2. Steam: right-click Planetbase > Properties > General > Launch options, enter:

   ```
   WINEDLLOVERRIDES="winhttp=n,b" %command%
   ```

   Without this, Wine uses its own `winhttp.dll` instead of BepInEx's and the mod never loads.
3. Start the game and press **F8** on the main menu.

To uninstall (both systems): delete `winhttp.dll` from `<game dir>` (BepInEx then stays inactive), or
remove `BepInEx\`, `winhttp.dll`, `doorstop_config.ini` and `.doorstop_version`. Clear the launch option
on Linux.

## Play

The host loads (or starts) a colony; joiners stay on the main menu.

**With Steam (no IP, no port forwarding):**

1. Host: in the colony, press F8 > **Host with Steam (invite friends)** > **Invite Steam friends**.
2. Joiner: accept the invite in Steam, or right-click the host in the Steam friends list > **Join Game**.
   If the game is closed, Steam starts it and it joins once it reaches the main menu.

**With an IP address:**

1. Host: in the colony, press F8 > **Host (IP)** (port 47610 by default).
2. Joiner: on the main menu, press F8, type the host's IP and port, **Join (IP)**.
3. Over the internet the host must let the joiner in: forward TCP port 47610 on the router to the host's
   PC and allow it in the firewall (Windows asks the first time). A VPN that puts you on one network
   (Tailscale, ZeroTier, ...) also works: use the host's VPN address. On the same LAN, use the host's
   local address.

Joining loads the host's colony on the joiner. Then:

- Everyone builds, links, places components, recycles, turns buildings on and off, changes priorities,
  game speed and the alert level, sets landing permissions and manufacturing limits, and trades with
  merchant ships. Joiners' actions are sent to the host, which carries them out; the result comes back
  to everyone.
- Trading together: when several players have the same merchant's window open, what one moves into
  the transaction boxes appears in the others' windows at once. Whoever presses Trade concludes it,
  and the other windows close.
- While someone places a building, or a component inside a building (bed, table, workbench...), the
  others see its preview (green or red, as for the player placing it) with that player's name above it.
  It is only a picture: it doesn't block anyone's placement.
- Meteors, lightning, sandstorms, blizzards and solar flares show for everyone. Their damage is worked
  out on the host only.
- The colony runs on the host only. Joiners see the host's colonists, robots, resources, ships,
  construction progress and notifications.
- **Only the host can save.** The host's save holds the whole colony, so you can continue later by
  hosting that save again.
- If the host quits, joiners are disconnected and return to the menu.

## Known issues

- Steam invites have not been tested between two real accounts yet. If they fail, use the IP option.
- Not played over the internet yet, and only tested with two players.
- Lightning, blizzards and solar flares reach joiners through the same code as meteors and sandstorms,
  but only meteors and sandstorms have been tested so far.
- The anti-meteor laser shoots meteors down for everyone, but joiners don't see its beam, only the
  meteor vanishing.
- If a game error happens on a joiner's PC, the mod no longer lets the game show its "ERROR ... Quit"
  page: it notes the error in the log, shows a short notice, and sends the error to the host's log. If
  something looks wrong after that notice, leave and rejoin.
- Only the host's actions run instantly; a joiner's action takes one round trip to the host.
- A building placed right at the edge of the allowed area next to the colony ship can show as blocked for
  a joiner while the host could still place it. Move it a little.
- Camera, selection and open windows are per player (by design).

## Test checklist (for a friend trying it with you)

Do these in order and note what happens at each step. Both players: same game version, same
`PlanetbaseCoop.dll`, F8 panel shows the same "Game 1.4.2 (...), mod 0.2.0 (...)" line at the bottom.

1. Both: start the game, press F8 on the main menu. Compare the bottom line of the panel. Same text?
2. Host: load a colony, F8 > Host with Steam > Invite Steam friends > invite the joiner.
3. Joiner: accept the invite. Do you load into the host's colony within a minute?
4. Joiner: place a small building (a solar panel) and link it. Does the host see it, and do colonists
   build it?
5. Host: place a building. Does the joiner see it appear and progress?
6. Joiner: change game speed. Does it change for both?
7. Both: watch a colonist walk for a minute. Is it at the same place on both screens?
8. Wait for a merchant ship (or turn landing permissions on). Both open its trade window. Host: click an
   item into a transaction box. Does it appear in the joiner's window? Joiner: click one in. Does it
   appear for the host? Joiner: press Trade. Does the host's window close and do colonists start
   carrying the goods?
   Also: an accumulator's gauge, the colonist counter (top left) and any meteor or sandstorm should
   look the same on both screens, and each player should see the other's building and component previews (with their
   name) while they choose where to put a building.
9. Joiner: try to save. You should see "Only the host can save."
10. Repeat steps 2-3 with the IP option instead of Steam (host's address and port 47610).
11. Host: quit. Does the joiner get a "Disconnected" message and return to the menu?

If something goes wrong, send the host these files from both PCs **before starting the game again**
(the game overwrites them at each start): `<game dir>\BepInEx\LogOutput.log` and the player logs
(Windows: `%USERPROFILE%\AppData\LocalLow\Madruga Works\Planetbase\Player.log` and `Player-prev.log`).
Since 0.2.0 the host's `LogOutput.log` also receives the errors caught on joiners' PCs.

## Changes

**0.2.0** (fixes from the first playtest; host and joiners must all update, 0.1.0 and 0.2.0 refuse each other)

- Joiners now see meteors, lightning, sandstorms, blizzards and solar flares.
- Accumulators and water tanks show their real level on joiners' screens (they looked full).
- The colonist counter on joiners' screens showed 2147483647 (wrapped on several lines); it now counts.
- Shared trade window (see Play).
- Other players' building and component previews are visible (see Play).
- A game error on a joiner's PC no longer forces them to quit (see Known issues), and reaches the
  host's log.
- When a joiner's trade failed on the host (the base no longer had what was offered), the base could
  lose the coins in that offer; fixed.

## Build from source

```
./build.sh <game dir> <folder that contains BepInEx/core>
```

Builds in the `mcr.microsoft.com/dotnet/sdk:8.0` Docker image against the game's own assemblies
(mounted read-only, never copied into the repo). Output: `out/PlanetbaseCoop.dll`.

## How it works

Host-authoritative mirror. The host runs the only simulation. A joiner receives the host's colony as a
save built in memory with the game's own serializers and loads it through the game's own loader; its
simulation is then frozen and it only draws what the host sends: object lifecycle (created/destroyed,
as the game's own XML), small state records (positions, construction, resource holders, ships,
indicators), and a census every 5 seconds that repairs any drift. Player actions are intercepted at the
game's commit points (placing a module, linking, the trade window's "trade" button...) and replayed on
the host by object id.

## Tests

`test/lab.py` runs isolated copies of the game under Proton, each inside its own headless gamescope
(no window ever appears on the desktop), with an in-game bot. `test/suite.py` runs every scenario,
including mutation twins (one protection turned off, the matching check must fail). See the write-up
in the Co-op Anything hub, `games/planetbase/README.md`.

## License

MIT. Contains no game code or assets; it builds against your own copy of the game.
