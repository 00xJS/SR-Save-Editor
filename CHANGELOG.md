# Changelog

## 0.3.0

Mac and Windows.

- The weapon wheel shows the game's own icons, read from your game files.
- The wardrobe: change what the player wears in the story save, and what the
  online character wears; the clothing owned in the story, too. Hair, facial hair, eyes, hats, eyewear, shirts, coats,
  necklaces, medallions, pendants, bracelets, watches, rings, underwear, pants,
  socks and shoes. Belts, piercings and tattoos are not in yet.
- The garage: see the cars by name and condition, repair one, add another
  like one, remove one, or change the model.
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
