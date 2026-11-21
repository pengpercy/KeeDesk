Remove-Item -Path build\KeePassModern\*.pdb -Force -ErrorAction SilentlyContinue

Compress-Archive -Path build\KeePassModern -DestinationPath "build\KeePassModern_${env:VERSION}.${env:RUNTIME}.zip" -Force
