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
run_both weapon:beretta=7/457 weapon:m16=40/500 weapon:grenade=1/30 food=the_fist,large_softdrink stash=1 cash=5000
run_both weapon:grenade=none weapon:molotov=1/50 food= cash=123456
echo "ok: both editors wrote identical saves"
