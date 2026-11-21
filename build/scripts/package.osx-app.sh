#!/usr/bin/env bash

set -e
set -o
set -u
set pipefail

cd build

case "$RUNTIME" in
    osx-arm64) arch=arm64;;
    osx-x64) arch=x86_64;;
    *) echo "Unknown runtime $RUNTIME"; exit 1;;
esac

rm -rf KeePassModern.app
mkdir -p KeePassModern.app/Contents/MacOS
mkdir -p KeePassModern.app/Contents/Resources
cp -R KeePassModern/ KeePassModern.app/Contents/MacOS/
rm -rf KeePassModern

# 只读数据放入 Contents/Resources，并清理所有调试符号 (.dSYM / .pdb / .dbg)
if [[ -d KeePassModern.app/Contents/MacOS/resources ]]; then
    cp -R KeePassModern.app/Contents/MacOS/resources/ KeePassModern.app/Contents/Resources/
    rm -rf KeePassModern.app/Contents/MacOS/resources
fi
rm -rf KeePassModern.app/Contents/MacOS/*.dSYM KeePassModern.app/Contents/MacOS/*.dsym KeePassModern.app/Contents/MacOS/*.pdb KeePassModern.app/Contents/MacOS/*.dbg

# SkiaSharp、HarfBuzz 与 Avalonia 原生 dylib 为双架构 Universal 二进制；使用 lipo -thin 仅保留当前架构以减小约 25% 体积
for lib in KeePassModern.app/Contents/MacOS/*.dylib; do
    if [[ -f "$lib" ]] && [[ "$(lipo -archs "$lib")" == *" "* ]]; then
        lipo -thin "$arch" "$lib" -output "$lib.thin" && mv "$lib.thin" "$lib"
    fi
done

ICON_SOURCE="../src/KeePassModern/Assets/Branding/app_icon_macos.png"
ICONSET_DIR="App.iconset"
rm -rf "$ICONSET_DIR"
mkdir -p "$ICONSET_DIR"
sips -z 16 16     "$ICON_SOURCE" --out "$ICONSET_DIR/icon_16x16.png" >/dev/null
sips -z 32 32     "$ICON_SOURCE" --out "$ICONSET_DIR/icon_16x16@2x.png" >/dev/null
sips -z 32 32     "$ICON_SOURCE" --out "$ICONSET_DIR/icon_32x32.png" >/dev/null
sips -z 64 64     "$ICON_SOURCE" --out "$ICONSET_DIR/icon_32x32@2x.png" >/dev/null
sips -z 128 128   "$ICON_SOURCE" --out "$ICONSET_DIR/icon_128x128.png" >/dev/null
sips -z 256 256   "$ICON_SOURCE" --out "$ICONSET_DIR/icon_128x128@2x.png" >/dev/null
sips -z 256 256   "$ICON_SOURCE" --out "$ICONSET_DIR/icon_256x256.png" >/dev/null
sips -z 512 512   "$ICON_SOURCE" --out "$ICONSET_DIR/icon_256x256@2x.png" >/dev/null
sips -z 512 512   "$ICON_SOURCE" --out "$ICONSET_DIR/icon_512x512.png" >/dev/null
sips -z 1024 1024 "$ICON_SOURCE" --out "$ICONSET_DIR/icon_512x512@2x.png" >/dev/null
iconutil -c icns "$ICONSET_DIR" -o KeePassModern.app/Contents/Resources/App.icns
rm -rf "$ICONSET_DIR"
sed "s/KeePassModern_VERSION/$VERSION/g" resources/app/App.plist > KeePassModern.app/Contents/Info.plist
find resources/app -maxdepth 1 -type d -name "*.lproj" -exec cp -R {} KeePassModern.app/Contents/Resources/ \;

# 由内向外签名（替代已废弃的 codesign --deep）：先签内部所有 Mach-O 动态库，再签整个 .app Bundle
sign_args=(--force --sign "${CODESIGN_IDENTITY:--}")
if [[ -n "${CODESIGN_IDENTITY:-}" ]]; then
    sign_args+=(--options runtime --timestamp)
fi
while IFS= read -r file; do
    if [[ "$file" != "KeePassModern.app/Contents/MacOS/KeePassModern" ]] && file -b "$file" | grep -q '^Mach-O'; then
        codesign "${sign_args[@]}" "$file"
    fi
done < <(find KeePassModern.app/Contents -type f | awk '{ print gsub("/", "/"), $0 }' | sort -rn | cut -d' ' -f2-)
codesign "${sign_args[@]}" KeePassModern.app
codesign --verify --deep --strict KeePassModern.app

# 使用 ditto 打包 .zip（保留符号链接、扩展属性与权限，便于应用内热更新直接解压）
ditto -c -k --sequesterRsrc --keepParent KeePassModern.app "KeePassModern_$VERSION.$RUNTIME.zip"

# 创建 ULMO (LZMA) 高压缩率 DMG（比 UDZO 体积小约 1/3）
DMG_NAME="KeePassModern_$VERSION.$RUNTIME.dmg"
echo "Creating DMG: $DMG_NAME"
rm -f "$DMG_NAME"

DMG_SOURCE="$(mktemp -d "${TMPDIR:-/tmp}/keepassmodern-dmg-source.XXXXXX")"
cleanup() {
    rm -rf "$DMG_SOURCE"
}
trap cleanup EXIT

cp -R "KeePassModern.app" "$DMG_SOURCE/"
ln -s /Applications "$DMG_SOURCE/Applications"

create_dmg() {
    hdiutil create -volname "KeePassModern" \
        -srcfolder "$DMG_SOURCE" \
        -ov -format ULMO \
        "$DMG_NAME"
}

attempt=1
max_attempts=3
until create_dmg; do
    if [ "$attempt" -ge "$max_attempts" ]; then
        echo "Failed to create DMG after $attempt attempts."
        exit 1
    fi

    echo "hdiutil create failed, retrying in 5 seconds ($attempt/$max_attempts)..."
    sleep 5
    attempt=$((attempt + 1))
done

if [[ -n "${CODESIGN_IDENTITY:-}" ]]; then
    codesign --force --timestamp --sign "$CODESIGN_IDENTITY" "$DMG_NAME"
fi

echo "Done packaging for $RUNTIME. Zip and DMG created."
