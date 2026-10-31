#!/bin/bash

# WhisperKeyboard Publish Script
# Kills running instance, builds, publishes, and creates macOS .app bundle

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
PROJECT="$PROJECT_DIR/src/WhisperKeyboard/WhisperKeyboard.csproj"

echo "==> Killing existing WhisperKeyboard processes..."
killall WhisperKeyboard 2>/dev/null || true
sleep 0.3

echo "==> Building..."
dotnet build "$PROJECT" -c Release -p:UseSharedCompilation=false

echo "==> Publishing..."
if [[ "$OSTYPE" == "darwin"* ]]; then
    # Self-contained so the .app launches without a separately-installed .NET runtime.
    # (Homebrew's dotnet@8 is keg-only and not in a TCC/loader-discoverable location.)
    dotnet publish "$PROJECT" -c Release -r osx-arm64 --self-contained true -p:UseSharedCompilation=false
    PUBLISH_DIR="$PROJECT_DIR/src/WhisperKeyboard/bin/Release/net8.0/osx-arm64/publish"

    echo "==> Creating macOS app bundle..."
    APP_NAME="WhisperKeyboard.app"
    APP_PATH="$PROJECT_DIR/publish/$APP_NAME"
    MACOS_RESOURCES="$PROJECT_DIR/src/WhisperKeyboard/macOS"

    # Remove old app bundle if it exists
    rm -rf "$APP_PATH"

    # Create app bundle directory structure
    mkdir -p "$APP_PATH/Contents/MacOS"
    mkdir -p "$APP_PATH/Contents/Resources"

    # Copy all published files to MacOS folder
    cp -R "$PUBLISH_DIR"/* "$APP_PATH/Contents/MacOS/"

    # Copy Info.plist
    if [[ -f "$MACOS_RESOURCES/Info.plist" ]]; then
        cp "$MACOS_RESOURCES/Info.plist" "$APP_PATH/Contents/Info.plist"
    else
        echo "Warning: Info.plist not found at $MACOS_RESOURCES/Info.plist"
    fi

    # Copy app icon
    if [[ -f "$MACOS_RESOURCES/AppIcon.icns" ]]; then
        cp "$MACOS_RESOURCES/AppIcon.icns" "$APP_PATH/Contents/Resources/"
    else
        echo "Warning: AppIcon.icns not found at $MACOS_RESOURCES/AppIcon.icns"
    fi

    # Bundle openal-soft (capture backend) so the app doesn't depend on a
    # separate Homebrew install at runtime. OpenALNative probes Contents/MacOS first.
    OAL_SRC=""
    for cand in \
        /opt/homebrew/opt/openal-soft/lib/libopenal.1.dylib \
        /usr/local/opt/openal-soft/lib/libopenal.1.dylib \
        /opt/homebrew/opt/openal-soft/lib/libopenal.dylib; do
        if [[ -f "$cand" ]]; then OAL_SRC="$cand"; break; fi
    done
    if [[ -n "$OAL_SRC" ]]; then
        cp "$OAL_SRC" "$APP_PATH/Contents/MacOS/libopenal.dylib"
        echo "==> Bundled openal-soft from $OAL_SRC"
    else
        echo "Warning: openal-soft not found — install with 'brew install openal-soft' (app will still try Homebrew paths at runtime)"
    fi

    # Code-sign the bundle with a stable identifier so macOS TCC (Accessibility,
    # Input Monitoring, Microphone) keys on a consistent identity. A real identity
    # keeps granted permissions across rebuilds; ad-hoc makes each rebuild a new app.
    # Uses CODESIGN_IDENTITY if set, else the first "Apple Development" identity in
    # the keychain (by hash, in case two share a name), else ad-hoc.
    SIGN_ID="${CODESIGN_IDENTITY:-}"
    SIGN_NAME="$SIGN_ID"
    if [[ -z "$SIGN_ID" ]]; then
        DEV_ID="$(security find-identity -v -p codesigning 2>/dev/null | grep -m1 '"Apple Development: ' || true)"
        if [[ -n "$DEV_ID" ]]; then
            SIGN_ID="$(awk '{print $2}' <<< "$DEV_ID")"
            SIGN_NAME="$(sed -E 's/.*"(.*)".*/\1/' <<< "$DEV_ID")"
        else
            SIGN_ID="-"
            SIGN_NAME="ad-hoc"
        fi
    fi
    echo "==> Code-signing app bundle (identity: $SIGN_NAME)..."
    codesign --force --deep --sign "$SIGN_ID" --identifier com.whisper-keyboard "$APP_PATH" \
        && codesign --verify --deep --strict "$APP_PATH" && echo "    signature OK" \
        || echo "    WARNING: codesign failed — permissions may need re-granting"

    echo "==> App bundle created: $APP_PATH"
else
    dotnet publish "$PROJECT" -c Release
    PUBLISH_DIR="$PROJECT_DIR/src/WhisperKeyboard/bin/Release/net8.0/publish"
    echo "==> Publish complete! Output: $PUBLISH_DIR"
fi
