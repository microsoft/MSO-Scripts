Param 
(
    [string]$NetBlameBuild,
    [string]$WorkingDir,
    [string]$OutputZip
)

$WorkingDir = "$WorkingDir\MSO-Scripts"

# Create working directory to copy artifacts
New-Item -ItemType Directory -Path $WorkingDir

# Copy all main scripts 
Get-ChildItem -Path "src" -File | Copy-Item -Destination $WorkingDir

# Copy BETA scripts 
Copy-Item -Path "src\BETA" -Destination "$WorkingDir\BETA" -Recurse

# Copy ADDIN binaries from NetBlame build
$AddinDir = "$WorkingDir\BETA\ADDIN"
Get-ChildItem -Path $NetBlameBuild -File | Copy-Item -Destination $AddinDir

$Platforms = 'arm64', 'x64'
$Binaries = 'msdia140.dll', 'perfcore.dll', 'perf_nt.dll', 'perf_dynamic.dll', 'symcache.dll', 'symsrv.dll' 

ForEach ($platform in $Platforms) {
   $PlatformDir = "$AddinDir\$platform"
   New-Item -ItemType Directory -Path "$PlatformDir\wpt"
   ForEach ($binary in $Binaries) {
      Copy-Item -Path "$NetBlameBuild\$platform\wpt\$binary" -Destination "$PlatformDir\wpt"
   }
}

# Copy remaing data
Copy-Item -Path "src\OETW" -Destination "$WorkingDir\OETW" -Recurse
Copy-Item -Path "src\PreWin10" -Destination "$WorkingDir\PreWin10" -Recurse
Copy-Item -Path "src\WPAP" -Destination "$WorkingDir\WPAP" -Recurse
Copy-Item -Path "src\WPRP" -Destination "$WorkingDir\WPRP" -Recurse

Compress-Archive -Path $WorkingDir -DestinationPath $OutputZip
