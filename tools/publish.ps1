# Builds portable, self-contained win-x64 folders for the server and client.
#   powershell -File tools/publish.ps1 [-Out dist]
param([string]$Out = "dist")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

dotnet publish src/Voxel.Server -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -o "$Out/server" --nologo
dotnet publish src/Voxel.Client -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -o "$Out/client" --nologo

# Both apps discover /data (and the client /shaders) next to the executable.
robocopy data "$Out/server/data" /MIR /NJH /NJS /NDL /NFL | Out-Null
robocopy data "$Out/client/data" /MIR /NJH /NJS /NDL /NFL | Out-Null
robocopy shaders "$Out/client/shaders" /MIR /NJH /NJS /NDL /NFL | Out-Null

Write-Host ""
Write-Host "published:"
Write-Host "  $Out/server/Voxel.Server.exe   (world lands in $Out/server/worlds/)"
Write-Host "  $Out/client/Voxel.Client.exe   [--server ws://host:8081]"
