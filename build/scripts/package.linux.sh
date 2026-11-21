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

APPIMAGETOOL_URL=https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage

cd build

ICON_SOURCE="../src/KeePassModern/Assets/Branding/app_icon.png"
ICON_OUT="resources/_common/icons/KeePassModern.png"
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
    curl -o appimagetool -L "$APPIMAGETOOL_URL"
    chmod +x appimagetool
fi

rm -f KeePassModern/*.dbg
chmod +x KeePassModern/KeePassModern

rm -rf KeePassModern.AppDir
mkdir -p KeePassModern.AppDir/opt
mkdir -p KeePassModern.AppDir/usr/share/metainfo
mkdir -p KeePassModern.AppDir/usr/share/applications

cp -r KeePassModern KeePassModern.AppDir/opt/KeePassModern
desktop-file-install resources/_common/applications/KeePassModern.desktop --dir KeePassModern.AppDir/usr/share/applications \
    --set-icon com.KeePassModern.app --set-key=Exec --set-value=AppRun
mv KeePassModern.AppDir/usr/share/applications/{KeePassModern,com.KeePassModern.app}.desktop
cp resources/_common/icons/KeePassModern.png KeePassModern.AppDir/com.KeePassModern.app.png
ln -rsf KeePassModern.AppDir/opt/KeePassModern/KeePassModern KeePassModern.AppDir/AppRun
ln -rsf KeePassModern.AppDir/usr/share/applications/com.KeePassModern.app.desktop KeePassModern.AppDir
cp resources/appimage/KeePassModern.appdata.xml KeePassModern.AppDir/usr/share/metainfo/com.KeePassModern.app.appdata.xml

ARCH="$appimage_arch" ./appimagetool -v KeePassModern.AppDir "KeePassModern-$VERSION.linux.$appimage_arch.AppImage"

rm -rf resources/deb/opt resources/deb/usr
mkdir -p resources/deb/opt/KeePassModern/
mkdir -p resources/deb/usr/bin
mkdir -p resources/deb/usr/share/applications
mkdir -p resources/deb/usr/share/icons
cp -a KeePassModern/. resources/deb/opt/KeePassModern/
ln -rsf resources/deb/opt/KeePassModern/KeePassModern resources/deb/usr/bin
cp -r resources/_common/applications resources/deb/usr/share
cp -r resources/_common/icons resources/deb/usr/share

installed_size=$(du -sk resources/deb | cut -f1)
sed -i -e "s/^Version:.*/Version: $VERSION/" \
    -e "s/^Architecture:.*/Architecture: $arch/" \
    -e "s/^Installed-Size:.*/Installed-Size: $installed_size/" \
    resources/deb/DEBIAN/control

# xz 压缩使 AOT 二进制 deb 包比 gzip 小约 30%
dpkg-deb -Zxz --root-owner-group --build resources/deb "KeePassModern_$VERSION-1_$arch.deb"

rpmbuild -bb --target="$target" resources/rpm/SPECS/build.spec --define "_topdir $(pwd)/resources/rpm" --define "_version $VERSION"
mv "resources/rpm/RPMS/$target/KeePassModern-$VERSION-1.$target.rpm" ./
