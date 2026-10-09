# Save Editor for Saints Reborn (unofficial)

Edits your saves in [Saints Reborn](https://github.com/whompay/SaintsReborn),
on **Mac** or **Windows**. Not made by or connected to the Saints Reborn
project, Volition or Deep Silver.

Both versions do the same things:

- **Story save:** cash and respect, the weapon wheel (weapon per slot, ammo,
  food), the wardrobe (what the player wears and the clothing owned), the
  garage, the crib weapon cache, neighbourhoods and homies.
- **Cheat codes:** a list of the game's phone cheats.
- **Online character:** cash, owned clothing, and what the character wears.

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
the wheel. **Restore backup…** puts an earlier copy of the file back.

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
or `3_MPStorage.<date>-<time>.bak`. **Restore backup…** puts one of them back
(and keeps the file you had as another backup).

No online character in the list? In the game, open MULTIPLAYER, change
something in the wardrobe and save, then start the editor again.

## How it works

Names of weapons, food, clothing and cheats, and the icons on the weapon wheel,
are read from your own game files (`misc.vpp_xbox2` and `pegfiles.vpp_xbox2`);
the editor contains no game data or artwork. See `THIRD_PARTY_NOTICES.md`.

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
| `0x064C` | outfit: count, then 16-byte records in place (item id, variant id, slot, wear option); room for 128 |
| `0x0E50` | clothing owned: count, then 12-byte records in place (item id, variant id, 0); room for 2048 |
| `0x6E88` | homies: count, pointer to 12-byte records (name, 0, unlocked) |
| `0x6E98` | garage: count, pointer to 1160-byte records; `0x6EA0` is the next car number |

A car record: `0x04` car number, `0x08` model name hash, `0x14` the parts and
paint chosen (not decoded), `0x158` repair bill (float), `0x15C` hit points
(ten times the table's `Max_Hitpoints`, 5000 when not given), `0x160` a count
and then one value per part (panels up to a fifth of the hit points, tyres 450,
glass 1, larger ones for the engine and doors), `0x268` damage flags, `0x2A4`
the number of dents, then 24-byte dents to the end.
| `0x6E90` | neighbourhoods: count, pointer to 12-byte records (name, owner, flags) |
| `0x6F8C` | crib weapon cache: count, pointer to 8-byte records (name hash, flags) |

| Online character | What |
|---|---|
| `0x0004` | cash in cents |
| `0x0414` | number of worn items, then 16-byte records: item id, variant id, slot, wear option |
| `0x0C18` | number of owned items, then 12-byte records: item id, variant id, 0 (room for 2048) |

### The wardrobe

Pick a body slot on the left, then the item on the right. **Clothing owned**
is the list the game's wardrobe shows; what is worn is always in it. An item's wear
option (hood up, rolled sleeves, which finger) decides the slot it goes in; the
game's own slot table (`player_cust.xtbl`) says which. The slot numbers the
saves use were read from real saves. The coat's number (7) is the one gap
between the shirts and the underwear and has not been seen in a save yet; if a
coat put on with the editor is gone when the game loads, that is why. Belts,
piercings, tattoos and skin are left as they are, because their numbers are
not known yet.

### The garage

The cars are listed by name with their condition. **Repair** does what the
mechanic does: full hit points, no repair bill, no dents, and every part back
up. The save does not say what kind each part is (panel, tyre, glass), so a
damaged part is raised to the most its kind could hold and the game holds it
to the part's own maximum; a part the game knocked all the way down to 1 is
left as it is. **Another like it** adds a copy with the next car number;
**Remove** takes one out; the model can be changed. A copy or a changed model
keeps the paint and parts of the car it came from, and the game may reset ones
that don't fit the new model. The paint and parts themselves are not decoded
yet.

## Fair play

The online character is used in public and ranked matches. As far as is known,
multiplayer cash only buys clothing, so it gives no advantage in a match. If you
play with others, be open about using it.

## License

MIT, see `LICENSE`.
