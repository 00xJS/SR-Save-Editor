# Save Editor for Saints Reborn (unofficial)

Edits your saves in [Saints Reborn](https://github.com/whompay/SaintsReborn),
on **Mac** or **Windows**. Not made by or connected to the Saints Reborn
project, Volition or Deep Silver.

Both versions do the same things:

- **Story save:** cash and respect, the weapon wheel (weapon per slot, ammo,
  food), the crib weapon cache, neighbourhoods and homies.
- **Cheat codes:** a list of the game's phone cheats.
- **Online character:** cash and owned clothing.

## Mac

1. Close Saints Reborn.
2. Open **Saints Reborn Save Editor** (from the Releases page, or build it with
   `mac/build.sh`). The first time, right-click the app and choose **Open**,
   because the app is not signed by Apple.
3. It finds the game in `~/SaintsReborn` by itself. If yours is somewhere else,
   press **Open…** and choose the `dist` folder.
4. Pick a screen on the left, change what you want, and press **Save**.

On the **Weapons** screen, click a slot on the wheel to choose the weapon for
it, and a quick slot to choose food. The ammo for the selected weapon is under
the wheel.

Needs macOS 13 or later. Building needs the Xcode command line tools.

## Windows

1. Close Saints Reborn.
2. Run `MpCharacterEditor.exe` (from the Releases page, or build it with
   `build.bat`). Windows may warn that it is from an unknown publisher, because
   the program is not signed.
3. It finds the game by itself. If it can't, choose the folder that contains
   `saintsrow.exe` (the `dist` folder).
4. Pick **Story save** or **Online character**, change what you want, and press
   **Save**.

The **Weapons** tab works the same way as on the Mac: click a slot on the wheel.

Needs Windows 10 or 11. Nothing to install.

## Backups

Every save keeps the previous file next to it, as `game.dat.<date>-<time>.bak`
or `3_MPStorage.<date>-<time>.bak`. To go back, delete the new file and remove
the date and `.bak` from the name of the old one.

No online character in the list? In the game, open MULTIPLAYER, change
something in the wardrobe and save, then start the editor again.

## How it works

Names of weapons, food, clothing and cheats are read from your own game files
(`misc.vpp_xbox2`); the editor contains no game data or artwork.

The story save is
`dist/game/<profile>/545107D1/00000001/<slot>/game.dat` and the online
character is `dist/game/545107D1/profile/<player name>/storage/3_MPStorage`.
Both are big-endian.

| Story save | What |
|---|---|
| `0x0090` | respect |
| `0x00A0` | cash in cents |
| `0x0144` | carried weapons: count, pointer to 16-byte records (name, count, loaded, spare) |
| `0x0240` | carried food: count, pointer to names |
| `0x6E88` | homies: count, pointer to 12-byte records (name, 0, unlocked) |
| `0x6E90` | neighbourhoods: count, pointer to 12-byte records (name, owner, flags) |
| `0x6F8C` | crib weapon cache: count, pointer to 8-byte records (name hash, flags) |

| Online character | What |
|---|---|
| `0x0004` | cash in cents |
| `0x0414` | number of worn items, then 16-byte records: item id, variant id, slot, wear option |
| `0x0C18` | number of owned items, then 12-byte records: item id, variant id, 0 (room for 2048) |

## Fair play

The online character is used in public and ranked matches. As far as is known,
multiplayer cash only buys clothing, so it gives no advantage in a match. If you
play with others, be open about using it.

## License

MIT, see `LICENSE`.
