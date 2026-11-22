Remove-Item -Path build\KeeDesk\*.pdb -Force -ErrorAction SilentlyContinue

Compress-Archive -Path build\KeeDesk -DestinationPath "build\KeeDesk_${env:VERSION}.${env:RUNTIME}.zip" -Force
