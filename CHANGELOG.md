# Changelog

## 0.3.0

Mac and Windows.

- The weapon wheel shows the game's own icons, read from your game files.
- Restore backup for story saves (and for the online character on Mac).
- `tests/compare.sh` checks that both versions write identical saves.

## 0.2.0

Mac version (`mac/`), a native app, and the same new features in the Windows version.

- Story saves: cash, respect, the weapon wheel (weapon per slot, ammo), carried
  food, the crib weapon cache, neighbourhood owners and homies.
- A searchable list of the game's cheat codes.
- Online character: cash and owned clothing.
- Keeps a dated backup on every save and refuses to save while the game is running.

## 0.1.0

First version.

- Finds the Saints Reborn install and the multiplayer character by itself.
- Edits cash.
- Lists every item sold in multiplayer by name, with search and a category
  filter; ticking an item adds it to the character's owned items.
- Shows what the character is wearing.
- Keeps a dated backup on every save, and can restore one.
- Refuses to save while the game is running.
