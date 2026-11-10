#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
ACTION="${1:-run}"
PROFILE="${ZONE_PROFILE:-debug}"
ARCH="${ZONE_ARCH:-$(uname -m)}"
case "$ACTION" in build|run|test|package) ;; *) echo 'Usage: scripts/macos.sh [run|build|test|package]' >&2; exit 2;; esac
case "$PROFILE" in debug|release) ;; *) echo 'ZONE_PROFILE must be debug or release' >&2; exit 2;; esac
case "$ARCH" in arm64|x86_64) ;; *) echo 'ZONE_ARCH must be arm64 or x86_64' >&2; exit 2;; esac
if [[ "$ACTION" == package ]]; then PROFILE=release; fi
SDK="${ZONE_MACOS_SDK:-$(xcrun --show-sdk-path)}"
# Some CLT installations include the macOS 27 SDK without its SwiftUI macro plugin.
# Prefer the installed 26 SDK in that configuration; full Xcode uses its default SDK.
if [[ -z "${ZONE_MACOS_SDK:-}" && "$(xcode-select -p)" == */CommandLineTools && -d "$(dirname "$SDK")/MacOSX26.sdk" ]]; then
  SDK="$(dirname "$SDK")/MacOSX26.sdk"
fi
SCRATCH="$ROOT/target/swift-$ARCH-$PROFILE"
SWIFT_ARGS=(--package-path "$ROOT/apps/macos" --scratch-path "$SCRATCH" --configuration "$PROFILE" --arch "$ARCH" --sdk "$SDK")

if [[ "$ACTION" == test ]]; then
  # Explicitly load Swift Testing's plugin on Command Line Tools installations.
  PLUGIN="$(dirname "$(xcrun --find swift)")/../lib/swift/host/plugins/testing/libTestingMacros.dylib"
  if [[ -f "$PLUGIN" ]]; then
    swift test --disable-xctest "${SWIFT_ARGS[@]}" -Xswiftc -load-plugin-library -Xswiftc "$PLUGIN"
  else
    swift test --disable-xctest "${SWIFT_ARGS[@]}"
  fi
  exit 0
fi

swift build "${SWIFT_ARGS[@]}"
BIN_PATH="$(swift build "${SWIFT_ARGS[@]}" --show-bin-path)"
APP="$ROOT/target/native-$ARCH-$PROFILE/Zone.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN_PATH/Zone" "$APP/Contents/MacOS/Zone"
cp "$ROOT/apps/macos/Info.plist" "$APP/Contents/Info.plist"
cp "$ROOT/apps/macos/Resources/Zone.icns" "$APP/Contents/Resources/Zone.icns"
if [[ -n "${ZONE_SIGN_IDENTITY:-}" && "$ZONE_SIGN_IDENTITY" != - ]]; then
  codesign --force --options runtime --timestamp --sign "$ZONE_SIGN_IDENTITY" "$APP"
else
  codesign --force --sign - "$APP"
fi
codesign --verify --strict "$APP"
echo "Built $APP"

if [[ "$ACTION" == run ]]; then
  # Open this build rather than another copy registered under the same bundle ID.
  # The app and data lock reject a second active instance.
  open -n "$APP"
elif [[ "$ACTION" == package ]]; then
  VERSION=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$APP/Contents/Info.plist")
  mkdir -p "$ROOT/artifacts"
  STAGE="$(mktemp -d "${TMPDIR:-/tmp}/zone-dmg.XXXXXX")"
  trap 'rm -rf "$STAGE"' EXIT
  ditto "$APP" "$STAGE/Zone.app"
  ln -s /Applications "$STAGE/Applications"
  OUTPUT="$ROOT/artifacts/Zone-$VERSION-macos-$ARCH.dmg"
  hdiutil create -volname Zone -srcfolder "$STAGE" -format UDZO -ov "$OUTPUT"
  echo "Packaged $OUTPUT"
fi
