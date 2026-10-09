#!/bin/bash
# Checks that the Mac and Windows editors write byte-identical saves for the
# same edits. Runs on a Mac with the Xcode command line tools, dotnet, and
# Game Porting Toolkit (its Wine runs the Windows program).
#
#   tests/compare.sh <game.dat to copy> [dist folder with the game files]
set -euo pipefail
cd "$(dirname "$0")/.."
SAVE="${1:?game.dat to copy}"; DIST="${2:-$HOME/SaintsReborn/dist}"
WINE="/Applications/Game Porting Toolkit.app/Contents/Resources/wine/bin/wine64"
WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT

./mac/build.sh >/dev/null
cat > "$WORK/check.csproj" <<CSPROJ
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>WinExe</OutputType><TargetFramework>net48</TargetFramework><LangVersion>5</LangVersion>
    <AssemblyName>MpCharacterEditor</AssemblyName><EnableDefaultCompileItems>false</EnableDefaultCompileItems><GenerateAssemblyInfo>false</GenerateAssemblyInfo></PropertyGroup>
  <ItemGroup><Compile Include="$PWD/MpCharacterEditor.cs" /><Compile Include="$PWD/Core.cs" />
    <Reference Include="System.Windows.Forms" /><Reference Include="System.Drawing" /><Reference Include="System.Xml" />
    <PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.3" PrivateAssets="all" /></ItemGroup>
</Project>
CSPROJ
(cd "$WORK" && DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet build -c Release -nologo -v q >/dev/null)
EXE="$WORK/bin/Release/net48/MpCharacterEditor.exe"
export WINEPREFIX="$WORK/prefix" WINEDEBUG=-all

mkdir -p "$WORK/mac/game" "$WORK/win/game"
for side in mac win; do cp "$SAVE" "$WORK/$side/game/game.dat"; ln -s "$DIST/game/packfiles" "$WORK/$side/game/packfiles"; done
run_both() {
  ./mac/build/sredit set "$WORK/mac/game/game.dat" "$@" >/dev/null
  "$WINE" "$EXE" --story "Z:$WORK/win/game/game.dat" "Z:$WORK/report.txt" "$@" 2>/dev/null
  cmp "$WORK/mac/game/game.dat" "$WORK/win/game/game.dat"
}
run_both weapon:beretta=7/457 weapon:m16=40/500 weapon:grenade=1/30 food=the_fist,large_softdrink stash:all=1 cash=5000
run_both weapon:grenade=none weapon:molotov=1/50 food= cash=123456
# Wardrobe: a hat, a coat, a different pair of pants, and take the mustache off.
run_both wear:228=205/203 wear:331=53/32 wear:192=348/497 wear:1=none
run_both wear:228=206/203 wear:7=none
# Clothing owned: buy a puffy coat, give up the cowboy hat, put on a hat that was not owned.
run_both own:333/80=1 own:224/193=0 wear:224=193/198
# Garage: another of the last car, a new model for one, one gone; then again once the garage is in our block.
run_both vehicle:add=5 vehicle:4=car_2dr_sports04 vehicle:2=none
run_both vehicle:1=none cash=4242
run_both vehicle:4=repair vehicle:3=repair

# The online character, when one is next to the save.
MP="$(dirname "$SAVE")/3_MPStorage"
if [ -f "$MP" ]; then
  for side in mac win; do cp "$MP" "$WORK/$side/game/3_MPStorage"; done
  ./mac/build/sredit mp "$WORK/mac/game/3_MPStorage" wear:228=206/203 wear:331=53/32 cash=777 >/dev/null
  "$WINE" "$EXE" --mp "Z:$WORK/win/game/3_MPStorage" "Z:$WORK/report.txt" wear:228=206/203 wear:331=53/32 cash=777 2>/dev/null
  cmp "$WORK/mac/game/3_MPStorage" "$WORK/win/game/3_MPStorage"
fi
echo "ok: both editors wrote identical saves"
