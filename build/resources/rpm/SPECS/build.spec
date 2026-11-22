Name: KeeDesk
Version: %_version
Release: 1
Summary: Modern Cross-Platform Password Manager
License: MIT
URL: https://github.com/pengpercy/KeeDesk
Source: https://github.com/pengpercy/KeeDesk/archive/refs/tags/v%_version.tar.gz
Requires: libX11.so.6()(%{__isa_bits}bit)
Requires: libSM.so.6()(%{__isa_bits}bit)
Requires: libicu
Requires: xdg-utils
Obsoletes: KeePassModern <= %{version}

%define _build_id_links none
# xz payload: about 30% smaller than the gzip default
%define _binary_payload w9.xzdio

%description
Modern Cross-Platform Password Manager (KDBX 3.1/4.0/4.1)

%install
mkdir -p %{buildroot}/opt/KeeDesk
mkdir -p %{buildroot}/%{_bindir}
mkdir -p %{buildroot}/usr/share/applications
mkdir -p %{buildroot}/usr/share/icons
cp -r %{_topdir}/../../KeeDesk/* %{buildroot}/opt/KeeDesk/
ln -rsf %{buildroot}/opt/KeeDesk/KeeDesk %{buildroot}/%{_bindir}
cp -r %{_topdir}/../_common/applications %{buildroot}/%{_datadir}
cp -r %{_topdir}/../_common/icons %{buildroot}/%{_datadir}
chmod 755 -R %{buildroot}/opt/KeeDesk
chmod 755 %{buildroot}/%{_datadir}/applications/KeeDesk.desktop

%files
%dir /opt/KeeDesk/
/opt/KeeDesk/*
/usr/share/applications/KeeDesk.desktop
/usr/share/icons/*
%{_bindir}/KeeDesk

%changelog
# skip
