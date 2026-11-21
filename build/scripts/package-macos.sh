#!/usr/bin/env bash
set -euo pipefail

# macOS .app / .dmg 打包脚本：
# 自动生成标准 App Bundle，包含 zh-Hans.lproj / en.lproj 本地化资源、AppIcon.icns、Info.plist 及 .kdbx 文件关联。
# 用法：bash build/scripts/package-macos.sh <publish_dir> <output_dir> <rid> [version]

PUBLISH_DIR="${1:-publish/local}"
OUT_DIR="${2:-publish/dist}"
RID="${3:-osx-x64}"
VERSION="${4:-$(tr -d '[:space:]' < VERSION)}"

APP_NAME="KeePassModern"
BUNDLE_ID="com.pengpercy.keepassmodern"
APP_DIR="${OUT_DIR}/${APP_NAME}.app"
CONTENTS_DIR="${APP_DIR}/Contents"
MACOS_DIR="${CONTENTS_DIR}/MacOS"
RESOURCES_DIR="${CONTENTS_DIR}/Resources"

rm -rf "${APP_DIR}"
mkdir -p "${MACOS_DIR}" "${RESOURCES_DIR}/zh-Hans.lproj" "${RESOURCES_DIR}/en.lproj"

# 1. 拷贝 NativeAOT 可执行文件与原生动态库
cp -R "${PUBLISH_DIR}/"* "${MACOS_DIR}/"
chmod +x "${MACOS_DIR}/${APP_NAME}"

# 2. 生成标准多分辨率 AppIcon.icns（使用遵守 Apple HIG 透明边距规范的 1024×1024 macOS 专属图标）
ICON_PNG="src/KeePassModern/Assets/Branding/app_icon_macos.png"
if [[ -f "${ICON_PNG}" ]] && command -v sips >/dev/null 2>&1 && command -v iconutil >/dev/null 2>&1; then
    ICONSET_DIR="$(mktemp -d)/AppIcon.iconset"
    mkdir -p "${ICONSET_DIR}"
    for sz in 16 32 128 256 512; do
        sips -z "${sz}" "${sz}" "${ICON_PNG}" --out "${ICONSET_DIR}/icon_${sz}x${sz}.png" >/dev/null
        sz2=$((sz * 2))
        sips -z "${sz2}" "${sz2}" "${ICON_PNG}" --out "${ICONSET_DIR}/icon_${sz}x${sz}@2x.png" >/dev/null
    done
    iconutil -c icns "${ICONSET_DIR}" -o "${RESOURCES_DIR}/AppIcon.icns"
    rm -rf "$(dirname "${ICONSET_DIR}")"
fi

# 3. 拷贝仓库中的 zh-Hans.lproj 与 en.lproj 本地化资源（单一事实源，便于在 IDE Solution 中直接维护）
cp -R "build/macos/Resources/zh-Hans.lproj" "${RESOURCES_DIR}/"
cp -R "build/macos/Resources/en.lproj" "${RESOURCES_DIR}/"

# 4. 生成 Info.plist（声明 CFBundleLocalizations、.kdbx 关联与高分屏支持）
cat > "${CONTENTS_DIR}/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>${APP_NAME}</string>
    <key>CFBundleDisplayName</key>
    <string>${APP_NAME}</string>
    <key>CFBundleIdentifier</key>
    <string>${BUNDLE_ID}</string>
    <key>CFBundleVersion</key>
    <string>${VERSION}</string>
    <key>CFBundleShortVersionString</key>
    <string>${VERSION}</string>
    <key>CFBundleExecutable</key>
    <string>${APP_NAME}</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleIconFile</key>
    <string>AppIcon</string>
    <key>CFBundleDevelopmentRegion</key>
    <string>zh-Hans</string>
    <key>CFBundleLocalizations</key>
    <array>
        <string>zh-Hans</string>
        <string>en</string>
    </array>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSPrincipalClass</key>
    <string>NSApplication</string>
    <key>NSAccessibilityUsageDescription</key>
    <string>KeePassModern 需要辅助功能权限以执行跨窗口自动输入（Auto-Type）。</string>
    <key>CFBundleDocumentTypes</key>
    <array>
        <dict>
            <key>CFBundleTypeExtensions</key>
            <array>
                <string>kdbx</string>
            </array>
            <key>CFBundleTypeName</key>
            <string>KeePass Password Database</string>
            <key>CFBundleTypeRole</key>
            <string>Editor</string>
            <key>LSHandlerRank</key>
            <string>Alternate</string>
        </dict>
    </array>
</dict>
</plist>
EOF

# 5. Ad-hoc 签名并生成可分发 .dmg
if command -v codesign >/dev/null 2>&1; then
    codesign --force --deep --sign - "${APP_DIR}" >/dev/null 2>&1 || true
fi

DMG_PATH="${OUT_DIR}/${APP_NAME}-${VERSION}-${RID}.dmg"
rm -f "${DMG_PATH}"
if command -v hdiutil >/dev/null 2>&1; then
    STAGE_DIR="$(mktemp -d)"
    cp -R "${APP_DIR}" "${STAGE_DIR}/"
    ln -s /Applications "${STAGE_DIR}/Applications"
    hdiutil create -volname "${APP_NAME}" -srcfolder "${STAGE_DIR}" -ov -format UDZO "${DMG_PATH}" >/dev/null
    rm -rf "${STAGE_DIR}"
    echo "Packed DMG: ${DMG_PATH}"
fi

echo "Packed App Bundle: ${APP_DIR}"
