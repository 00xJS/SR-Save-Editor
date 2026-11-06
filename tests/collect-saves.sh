#!/bin/bash
# Copies every real save it can find (story saves, online characters, the
# editor's backups and their content headers) into tests/saves, one copy of
# each distinct file, so the checks run against saves the game really wrote.
# tests/saves is in .gitignore: these are your own saves, keep them local.
#
#   tests/collect-saves.sh [more folders to search...]
#
# Searches the game's dist folder (C:\Games\SaintsReborn\dist on Windows,
# ~/SaintsReborn/dist on the Mac, or $SR_DIST), its backups next to it, and
# any folders given.
set -euo pipefail
cd "$(dirname "$0")"
OUT=saves
mkdir -p "$OUT"

roots=()
for r in "${SR_DIST:-}" /c/Games/SaintsReborn "$HOME/SaintsReborn" /c/Games/SaintsReborn-backups "$@"; do
  [ -n "$r" ] && [ -d "$r" ] && roots+=("$r")
done
[ ${#roots[@]} -gt 0 ] || { echo "no game folder found; pass one" >&2; exit 1; }

if command -v sha256sum >/dev/null; then sum() { sha256sum "$1" | cut -c1-16; }
else sum() { shasum -a 256 "$1" | cut -c1-16; }; fi

manifest="$OUT/manifest.tsv"
touch "$manifest"
added=0; seen=0
while IFS= read -r -d '' f; do
  seen=$((seen + 1))
  h="$(sum "$f")"
  grep -q "^$h	" "$manifest" && continue
  case "$f" in
    *.header) kind=header ;;
    *MPStorage*) kind=online ;;
    *) kind=story ;;
  esac
  # Story saves keep their slot folder name: the game reads the version and date from it.
  slot="$(basename "$(dirname "$f")")"
  dest="$OUT/$kind/$h"
  mkdir -p "$dest"
  if [ "$kind" = story ]; then mkdir -p "$dest/$slot"; cp -p "$f" "$dest/$slot/game.dat"; name="$slot/game.dat"
  else cp -p "$f" "$dest/$(basename "$f")"; name="$(basename "$f")"; fi
  printf '%s\t%s\t%s\t%s\n' "$h" "$kind" "$h/$name" "$f" >> "$manifest"
  added=$((added + 1))
done < <(find "${roots[@]}" -path '*/.git' -prune -o -type f \
  \( -name 'game.dat' -o -name 'game.dat.*' -o -name '3_MPStorage' -o -name '3_MPStorage.*' -o -name '*.header' \) -print0 2>/dev/null)

echo "$seen files looked at, $added new; $(wc -l < "$manifest" | tr -d ' ') distinct saves in tests/$OUT"
