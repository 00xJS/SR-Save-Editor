#!/bin/bash
# Builds "Saints Reborn Save Editor.app" and the sredit command-line tool into mac/build.
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build
APP="build/Saints Reborn Save Editor.app"
mkdir -p "$APP/Contents/MacOS"
swiftc -O -swift-version 5 -parse-as-library Sources/Core/*.swift Sources/App/*.swift -o "$APP/Contents/MacOS/SaveEditor"
swiftc -O -swift-version 5 Sources/Core/*.swift Sources/CLI/main.swift -o build/sredit
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>Saints Reborn Save Editor</string>
  <key>CFBundleIdentifier</key><string>io.github.00xjs.saintsreborn-save-editor</string>
  <key>CFBundleExecutable</key><string>SaveEditor</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>2.0</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict></plist>
PLIST
echo "Built $APP"
