# Builds the HAQQ CAD test release that can be sent to other people:
#   1. HaqqCad.exe as ONE self-contained file (the other PC does not need .NET installed)
#   2. HAQQ-CAD-Setup-<date>.exe    - installer: copies the program, adds Desktop and Start-menu shortcuts
#   3. HAQQ-CAD-Portable-<date>.zip - no installation: unzip and double-click HaqqCad.exe
# Run it through make-installer.cmd. Everything is written to the "Installer" folder next to that file.
$ErrorActionPreference = 'Stop'

$source  = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)      # ...\HAQQ.CAD
$outDir  = Join-Path (Split-Path -Parent $source) 'Installer'
$stamp   = Get-Date -Format 'yyyy-MM-dd'
$work    = Join-Path $env:TEMP 'haqqcad-installer'
$publish = Join-Path $work 'publish'
$log     = Join-Path $outDir 'installer.log'

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Force -Path $publish | Out-Null

# --- automatic updates -------------------------------------------------------------------------------
# update-url.txt (next to make-installer.cmd) holds the https address of the web folder you upload to,
# for example  https://www.example.com/haqqcad/  . With it, every copy of this build checks that folder
# for a newer build each time it starts. Without it, the build is made without the update check.
$build     = Get-Date -Format 'yyyyMMddHHmm'
$urlFile   = Join-Path (Split-Path -Parent $source) 'update-url.txt'
$channel   = Join-Path $source 'src\HAQQ.CAD.Desktop\update-channel.txt'
$updateUrl = $null
if (Test-Path $urlFile) {
    $updateUrl = (Get-Content $urlFile | Where-Object { $_.Trim() -ne '' } | Select-Object -First 1)
    if ($updateUrl) { $updateUrl = $updateUrl.Trim() }
    if ($updateUrl -and -not $updateUrl.StartsWith('https://')) {
        Write-Host "update-url.txt must contain an address that starts with https:// . Automatic updates are OFF for this build." -ForegroundColor Yellow
        $updateUrl = $null
    }
    if ($updateUrl -and -not $updateUrl.EndsWith('/')) { $updateUrl = $updateUrl + '/' }
}
if (Test-Path $channel) { Remove-Item -Force $channel }
if ($updateUrl) {
    Set-Content -Path $channel -Value @($build, ($updateUrl + 'haqq-cad-update.json')) -Encoding Ascii
    Write-Host "Automatic updates: ON  (build $build, folder $updateUrl)"
} else {
    Write-Host 'Automatic updates: OFF (no update-url.txt). See READ ME - updates.txt.'
}

Write-Host 'Step 1 of 3: building HAQQ CAD as a single file (the first time this downloads about 150 MB from Microsoft)...'
$project = Join-Path $source 'src\HAQQ.CAD.Desktop\HAQQ.CAD.Desktop.csproj'
# Build messages go to installer.log.
$publishArgs = "publish `"$project`" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o `"$publish`""
$proc = Start-Process -FilePath 'dotnet' -ArgumentList $publishArgs -NoNewWindow -Wait -PassThru `
    -RedirectStandardOutput $log -RedirectStandardError (Join-Path $outDir 'installer-errors.log')
if (Test-Path $channel) { Remove-Item -Force $channel }      # never leave it for the normal development build
$exe = Join-Path $publish 'HaqqCad.exe'
if ($proc.ExitCode -ne 0 -or -not (Test-Path $exe)) {
    Write-Host ''
    Write-Host "BUILD FAILED. Details are in: $log" -ForegroundColor Red
    exit 1
}

$readme = @"
HAQQ CAD - test build of $stamp

This is an early test version. Please do not use it for production drawings yet.

HOW TO START
  Installer:  run HAQQ-CAD-Setup-$stamp.exe. It copies the program to your user folder and puts
              a "HAQQ CAD" shortcut on the Desktop and in the Start menu. No administrator rights needed.
  Portable:   unzip HAQQ-CAD-Portable-$stamp.zip anywhere and double-click HaqqCad.exe.

WINDOWS WARNING
  The program is not digitally signed yet, so Windows may show "Windows protected your PC".
  Click "More info", then "Run anyway".

REMOVING IT
  Delete the folder  %LOCALAPPDATA%\Programs\HAQQ CAD  and the two "HAQQ CAD" shortcuts.

FEEDBACK
  Tell us what is missing or wrong, and include the build date above.
  If the program closes with an error, send the file error.log from the program folder.
"@
$readmePath = Join-Path $publish 'READ ME.txt'
Set-Content -Path $readmePath -Value $readme -Encoding UTF8

Write-Host 'Step 2 of 3: portable zip...'
$zip = Join-Path $outDir "HAQQ-CAD-Portable-$stamp.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path $exe, $readmePath -DestinationPath $zip

Write-Host 'Step 3 of 3: installer...'
$install = @'
@echo off
title HAQQ CAD Setup
set "DEST=%LOCALAPPDATA%\Programs\HAQQ CAD"
echo Installing HAQQ CAD to "%DEST%" ...
taskkill /im HaqqCad.exe 1>NUL 2>NUL
if not exist "%DEST%" mkdir "%DEST%"
copy /y "%~dp0HaqqCad.exe" "%DEST%\HaqqCad.exe" 1>NUL
if errorlevel 1 (
  echo.
  echo Could not copy the program. If HAQQ CAD is open, close it and run this setup again.
  pause
  exit /b 1
)
copy /y "%~dp0READ ME.txt" "%DEST%\READ ME.txt" 1>NUL
powershell -NoProfile -ExecutionPolicy Bypass -Command "$t = Join-Path $env:LOCALAPPDATA 'Programs\HAQQ CAD'; $s = New-Object -ComObject WScript.Shell; foreach ($f in 'Desktop','Programs') { $l = $s.CreateShortcut((Join-Path ([Environment]::GetFolderPath($f)) 'HAQQ CAD.lnk')); $l.TargetPath = Join-Path $t 'HaqqCad.exe'; $l.WorkingDirectory = $t; $l.Save() }"
start "" "%DEST%\HaqqCad.exe"
'@
Set-Content -Path (Join-Path $publish 'install.cmd') -Value $install -Encoding Ascii

$setupName = "HAQQ-CAD-Setup-$stamp.exe"
$setupTemp = Join-Path $work $setupName
$sed = @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=1
HideExtractAnimation=0
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=%InstallPrompt%
DisplayLicense=%DisplayLicense%
FinishMessage=%FinishMessage%
TargetName=%TargetName%
FriendlyName=%FriendlyName%
AppLaunched=%AppLaunched%
PostInstallCmd=%PostInstallCmd%
AdminQuietInstCmd=%AdminQuietInstCmd%
UserQuietInstCmd=%UserQuietInstCmd%
SourceFiles=SourceFiles
[Strings]
InstallPrompt=Install HAQQ CAD (test build of $stamp) for this Windows user?
DisplayLicense=
FinishMessage=
TargetName=$setupTemp
FriendlyName=HAQQ CAD Setup
AppLaunched=cmd /c install.cmd
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
FILE0="HaqqCad.exe"
FILE1="install.cmd"
FILE2="READ ME.txt"
[SourceFiles]
SourceFiles0=$publish\
[SourceFiles0]
%FILE0%=
%FILE1%=
%FILE2%=
"@
$sedPath = Join-Path $work 'setup.sed'
Set-Content -Path $sedPath -Value $sed -Encoding Default

$setupOk = $false
$iexpress = Join-Path $env:SystemRoot 'System32\iexpress.exe'
if (Test-Path $iexpress) {
    $p = Start-Process -FilePath $iexpress -ArgumentList '/N', '/Q', "`"$sedPath`"" -Wait -PassThru
    if (Test-Path $setupTemp) {
        Copy-Item -Force $setupTemp (Join-Path $outDir $setupName)
        $setupOk = $true
    }
}
Copy-Item -Force $readmePath (Join-Path $outDir 'READ ME.txt')

# Files for the update folder on the web server.
$uploadDir = Join-Path $outDir 'Upload to web folder'
if (Test-Path $uploadDir) { Remove-Item -Recurse -Force $uploadDir }
if ($updateUrl) {
    New-Item -ItemType Directory -Force -Path $uploadDir | Out-Null
    Copy-Item -Force $exe (Join-Path $uploadDir 'HaqqCad.exe')
    $hash = (Get-FileHash -Algorithm SHA256 -Path $exe).Hash
    $manifest = "{`n  `"build`": $build,`n  `"file`": `"HaqqCad.exe`",`n  `"sha256`": `"$hash`"`n}`n"
    [System.IO.File]::WriteAllText((Join-Path $uploadDir 'haqq-cad-update.json'), $manifest)
}

Write-Host ''
Write-Host "Done. Files are in: $outDir" -ForegroundColor Green
Write-Host "  HAQQ-CAD-Portable-$stamp.zip   (unzip and run)"
if ($setupOk) { Write-Host "  $setupName   (installer)" }
else { Write-Host '  The installer exe could not be made on this PC (IExpress did not produce it). Send the portable zip instead.' -ForegroundColor Yellow }
if ($updateUrl) {
    Write-Host ''
    Write-Host "TO PUBLISH THIS BUILD TO YOUR TESTERS: upload the two files in 'Upload to web folder' to $updateUrl" -ForegroundColor Cyan
    Write-Host '  (HaqqCad.exe first, then haqq-cad-update.json). Their copies update the next time they start HAQQ CAD.'
}
exit 0
