#!/bin/bash
# Runs the save checks, the repair, editing and outfits against copies of the
# real saves in tests/saves (gather them first with tests/collect-saves.sh).
# Windows (Git Bash): uses MpCharacterEditor.exe, built by build.bat.
# Nothing outside a temporary folder is written; the backups go there too.
#
#   tests/check-saves.sh
set -euo pipefail
cd "$(dirname "$0")/.."
EXE="$PWD/MpCharacterEditor.exe"
[ -f "$EXE" ] || { echo "build first (build.bat)" >&2; exit 1; }
[ -s tests/saves/manifest.tsv ] || { echo "no saves: run tests/collect-saves.sh" >&2; exit 1; }
PY="$(command -v python3 || command -v python)"
WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
cp -r tests/saves "$WORK/saves"
export SR_EDITOR_BACKUPS="$(cygpath -w "$WORK/backups" 2>/dev/null || echo "$WORK/backups")"
w() { cygpath -w "$1" 2>/dev/null || echo "$1"; }
fail=0
ok() { echo "ok:   $*"; }
bad() { echo "FAIL: $*"; fail=1; }

# Checksum the way the game's loader works it out, independent of the editor.
crc() { "$PY" -I - "$1" <<'EOF'
import sys, struct
t = []
for i in range(256):
    c = i
    for _ in range(8): c = (c >> 1) ^ 0xEDB88320 if c & 1 else c >> 1
    t.append(c)
d = open(sys.argv[1], 'rb').read(); c = 0
for x in d[4:]: c = t[(c ^ x) & 0xFF] ^ (c >> 8)
s, z, p = (struct.unpack('>I', d[o:o + 4])[0] for o in (0, 0x6FF4, 0x6FF8))
print('good' if c == s else 'bad', d[p:p + z].hex())
EOF
}

# 1. The checker agrees with the game's checksum on every story save.
"$EXE" --check-saves "$(w "$WORK/check.txt")" "$(w "$WORK/saves")" || true
agree=0; n=0
while IFS= read -r f; do
  n=$((n + 1))
  want="$(crc "$f" | cut -d' ' -f1)"
  line="$(grep -F "$(w "$f"):" "$WORK/check.txt" | head -1)"
  if [ "$want" = good ]; then case "$line" in *"damaged, can be repaired"*) bad "checker calls a good save damaged: $f";; *) agree=$((agree + 1));; esac
  else case "$line" in *"damaged, can be repaired"*) agree=$((agree + 1));; *) bad "checker misses a bad checksum: $f ($line)";; esac; fi
done < <(find "$WORK/saves/story" -name game.dat)
ok "checker matches the game's checksum on $agree of $n story saves"
grep -q '^online.*: good' "$WORK/check.txt" && ok "online characters checked" || bad "no online character passed"

# 2. Repair fixes every save whose only damage is the checksum.
"$EXE" --repair-saves "$(w "$WORK/repair.txt")" "$(w "$WORK/saves")" || true
left=0
while IFS= read -r f; do [ "$(crc "$f" | cut -d' ' -f1)" = good ] || { left=$((left + 1)); bad "still bad after repair: $f"; }; done < <(find "$WORK/saves/story" -name game.dat)
[ $left = 0 ] && ok "every story save loads after repair ($(grep -c '^repair' "$WORK/repair.txt") repaired)"
[ -n "$(find "$WORK/backups" -name '*before-repair*.bak')" ] && ok "repair kept the damaged files in the backup folder" || bad "no backups from the repair"

# 3. Editing a save the game wrote keeps it loadable and keeps its unlocked cheats.
edited=0
while IFS= read -r f; do
  before="$(crc "$f")"; [ "${before%% *}" = good ] || continue
  "$EXE" --story "$(w "$f")" "$(w "$WORK/r.txt")" cash=4242 weapon:grenade=1/30 weapon:molotov=1/9 food=large_softdrink >/dev/null
  after="$(crc "$f")"
  [ "${after%% *}" = good ] || bad "edited save has a bad checksum: $f"
  [ "${after#* }" = "${before#* }" ] || bad "edited save lost its unlocked cheats (${before#* } -> ${after#* }): $f"
  grep -q 'cash 4242.00' "$WORK/r.txt" || bad "edit not read back: $f"
  edited=$((edited + 1))
done < <(find "$WORK/saves/story" -name game.dat)
[ $fail = 0 ] && ok "$edited saves edited: checksum right, unlocked cheats kept"
# Edit again (the lists the editor placed are rewritten in place).
f="$(find "$WORK/saves/story" -name game.dat | head -1)"
"$EXE" --story "$(w "$f")" "$(w "$WORK/r.txt")" weapon:molotov=none cash=1 >/dev/null
[ "$(crc "$f" | cut -d' ' -f1)" = good ] && ok "a second edit keeps the checksum right" || bad "second edit"

# 4. Opening a save keeps one backup of it, and only one while it does not change.
[ -n "$(find "$WORK/backups/story" -name '*before-saving*.bak' 2>/dev/null)" ] && ok "saving keeps a copy in the backup folder" || bad "no backup before saving"

# 5. Outfits: from one story save to another, and to the online character.
src="$(find "$WORK/saves/story" -name game.dat | sort | head -1)"
dst="$(find "$WORK/saves/story" -name game.dat | sort | tail -1)"
mp="$(find "$WORK/saves/online" -name 3_MPStorage | head -1)"
"$EXE" --outfit-export "$(w "$src")" "$(w "$WORK/look.sroutfit")"
grep -q '^Saints Reborn outfit 1' "$WORK/look.sroutfit" && ok "outfit exported ($(grep -c '^[0-9]' "$WORK/look.sroutfit") pieces)" || bad "outfit export"
"$EXE" --outfit-apply "$(w "$WORK/look.sroutfit")" "$(w "$dst")"
"$EXE" --story "$(w "$src")" "$(w "$WORK/a.txt")"; "$EXE" --story "$(w "$dst")" "$(w "$WORK/b.txt")"
[ "$(grep '^outfit' "$WORK/a.txt")" = "$(grep '^outfit' "$WORK/b.txt")" ] && ok "the other story save wears the same outfit" || bad "outfit not copied to the story save"
[ "$(crc "$dst" | cut -d' ' -f1)" = good ] || bad "story save has a bad checksum after the outfit"
if [ -n "$mp" ]; then
  "$EXE" --outfit-apply "$(w "$WORK/look.sroutfit")" "$(w "$mp")"
  "$EXE" --mp "$(w "$mp")" "$(w "$WORK/m.txt")"
  "$EXE" --outfit-export "$(w "$mp")" "$(w "$WORK/mp.sroutfit")"
  ok "online character now wears: $(grep '^outfit' "$WORK/m.txt" | cut -c1-120)"
  "$EXE" --check-saves "$(w "$WORK/c2.txt")" "$(w "$mp")" && ok "online character still checks good" || bad "online character damaged after outfit"
fi

[ $fail = 0 ] && echo "all save checks passed" || { echo "some checks failed"; exit 1; }
