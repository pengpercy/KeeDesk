#!/usr/bin/env bash

set -e
set -o
set -u
set pipefail

arch=
appimage_arch=
target=
case "$RUNTIME" in
    linux-x64)
        arch=amd64
        appimage_arch=x86_64
        target=x86_64;;
    linux-arm64)
        arch=arm64
        appimage_arch=aarch64
        target=aarch64;;
    *)
        echo "Unknown runtime $RUNTIME"
        exit 1;;
esac

host_arch=$(uname -m)
case "$host_arch" in
    x86_64|aarch64) ;;
    *) echo "Unsupported packaging host architecture: $host_arch"; exit 1;;
esac
APPIMAGETOOL_URL="https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$host_arch.AppImage"

cd build

ICON_SOURCE="../src/KeeDesk/Assets/Branding/app_icon.png"
ICON_OUT="resources/_common/icons/KeeDesk.png"
mkdir -p "resources/_common/icons"

if [[ -f "$ICON_SOURCE" ]]; then
    if command -v ffmpeg >/dev/null 2>&1; then
        echo "Generating Linux icon from source: $ICON_SOURCE"
        ffmpeg -y -hide_banner -loglevel error -i "$ICON_SOURCE" -vf "scale=256:256:flags=lanczos" -frames:v 1 "$ICON_OUT"
    else
        cp "$ICON_SOURCE" "$ICON_OUT"
    fi
fi

if [[ ! -f "appimagetool" ]]; then
    curl -fL -o appimagetool "$APPIMAGETOOL_URL"
    chmod +x appimagetool
fi

rm -f KeeDesk/*.dbg
chmod +x KeeDesk/KeeDesk

rm -rf KeeDesk.AppDir
mkdir -p KeeDesk.AppDir/opt
mkdir -p KeeDesk.AppDir/usr/share/metainfo
mkdir -p KeeDesk.AppDir/usr/share/applications

cp -r KeeDesk KeeDesk.AppDir/opt/KeeDesk
desktop-file-install resources/_common/applications/KeeDesk.desktop --dir KeeDesk.AppDir/usr/share/applications \
    --set-icon com.KeeDesk.app --set-key=Exec --set-value=AppRun
mv KeeDesk.AppDir/usr/share/applications/{KeeDesk,com.KeeDesk.app}.desktop
cp resources/_common/icons/KeeDesk.png KeeDesk.AppDir/com.KeeDesk.app.png
ln -rsf KeeDesk.AppDir/opt/KeeDesk/KeeDesk KeeDesk.AppDir/AppRun
ln -rsf KeeDesk.AppDir/usr/share/applications/com.KeeDesk.app.desktop KeeDesk.AppDir
cp resources/appimage/KeeDesk.appdata.xml KeeDesk.AppDir/usr/share/metainfo/com.KeeDesk.app.appdata.xml

# AppImage users need read access and the same execute access as the packaging user.
chmod -R a+rX KeeDesk.AppDir
ARCH="$appimage_arch" ./appimagetool -v KeeDesk.AppDir "KeeDesk-$VERSION.linux.$appimage_arch.AppImage"

rm -rf resources/deb/opt resources/deb/usr
mkdir -p resources/deb/opt/KeeDesk/
mkdir -p resources/deb/usr/bin
mkdir -p resources/deb/usr/share/applications
mkdir -p resources/deb/usr/share/icons
cp -a KeeDesk/. resources/deb/opt/KeeDesk/
ln -rsf resources/deb/opt/KeeDesk/KeeDesk resources/deb/usr/bin
cp -r resources/_common/applications resources/deb/usr/share
cp -r resources/_common/icons resources/deb/usr/share

installed_size=$(du -sk resources/deb | cut -f1)
sed -i -e "s/^Version:.*/Version: $VERSION/" \
    -e "s/^Architecture:.*/Architecture: $arch/" \
    -e "s/^Installed-Size:.*/Installed-Size: $installed_size/" \
    resources/deb/DEBIAN/control

# xz 压缩使 AOT 二进制 deb 包比 gzip 小约 30%
dpkg-deb -Zxz --root-owner-group --build resources/deb "KeeDesk_$VERSION-1_$arch.deb"

rpmbuild -bb --target="$target" resources/rpm/SPECS/build.spec --define "_topdir $(pwd)/resources/rpm" --define "_version $VERSION"
mv "resources/rpm/RPMS/$target/KeeDesk-$VERSION-1.$target.rpm" ./
