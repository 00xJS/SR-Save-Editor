# Multiplayer Character Editor for Saints Reborn (unofficial)

Changes the cash and the owned clothing of your multiplayer character in
[Saints Reborn](https://github.com/whompay/SaintsReborn). Not made by or
connected to the Saints Reborn project, Volition or Deep Silver.

## Use

1. Close Saints Reborn.
2. Run `MpCharacterEditor.exe`. Windows may warn that it is from an unknown
   publisher, because the program is not signed.
3. It finds the game and your character by itself. If it can't, choose the
   folder that contains `saintsrow.exe` (the `dist` folder).
4. Change the cash, tick the items you want to own, and press **Save**.

Every save keeps your previous character next to the file, as
`3_MPStorage.<date>-<time>.bak`. **Restore backup...** puts one of them back.

No character in the list? In the game, open MULTIPLAYER, change something in the
wardrobe and save, then start the editor again.

## What it needs

Windows 10 or 11. Nothing to install.

## What it does not do

It does not change what the character is wearing, the body shape, rank or
statistics. Items the character is wearing can't be unticked.

## Known problems

- Tested on one PC with one install of Saints Reborn (source at `f53b577`).
- Three Football Jersey styles (Aztec Bird, Mao Design, May Lion) are in the
  game's multiplayer store table, but the game removes them from the owned list
  when it loads the character. The editor leaves them out.
- The game itself sometimes loads a character with no shirt, pants or necklaces
  after a restart. This happened with Zoot Pants worn with suspenders and a
  tucked T-Shirt, and does not depend on this editor. Dressing again in the
  wardrobe and saving fixes it.

## How it works

The character is the file
`dist\game\545107D1\profile\<player name>\storage\3_MPStorage` (27,724 bytes,
big-endian):

| Offset | What |
|---|---|
| `0x0004` | cash in cents (32 bits) |
| `0x0010` | number of body sliders, then (value, index) pairs |
| `0x0414` | number of worn items, then 16-byte records: item id, variant id, slot, wear option |
| `0x0C18` | number of owned items, then 12-byte records: item id, variant id, 0 (room for 2048) |

Item names are read from your own game files (`customization_items.xtbl`,
`customization_stores.xtbl` and `US_Strings.txt` inside `misc.vpp_xbox2`); the
program contains no game data.

## Build

Run `build.bat`. It uses the C# compiler that comes with Windows.
