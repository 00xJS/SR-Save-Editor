# Changelog

## Unreleased

Windows.

- Story save: **Start at**, where the game puts you when the save loads (a
  crib, the church, or a district save point), from the game's own list.
- Garage: the random looks of a model (variant, flashers, light bars, hoods
  and so on) and the police unit number.
- Fixed: an error when opening the wardrobe for the first time.
- Fixed (Mac and Windows): saves changed by the editor showed as damaged in
  the game, because the checksum at the start of the file was not updated.
- Fixed (Mac and Windows): changing weapons, food or the garage wiped the
  story save's unlocked cheats.
- A backup of every save as soon as it is opened, in the editor's own folder.
- **Check saves…**: what the game would make of each save, and a repair for
  damaged ones (a wrong checksum, or the newest good backup).
- Outfits: save one to a file, load it, or copy it to other story saves and
  the online character.
- `tests/collect-saves.sh` gathers your saves for testing;
  `tests/check-saves.sh` runs the checks, repair, edits and outfits on them.

## 0.3.0

Mac and Windows.

- The weapon wheel shows the game's own icons, read from your game files.
- The wardrobe: change what the player wears in the story save, and what the
  online character wears; the clothing owned in the story, too. Hair, facial hair, eyes, hats, eyewear, shirts, coats,
  necklaces, medallions, pendants, bracelets, watches, rings, underwear, pants,
  socks and shoes. Belts, piercings and tattoos are not in yet.
- The garage: see the cars by name and condition, repair one, add another
  like one, remove one, change the model, and change the paint and parts
  from the game's own lists.
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
