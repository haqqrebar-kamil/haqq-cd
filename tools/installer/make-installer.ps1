# Builds the HAQQ CAD test release that can be sent to other people:
#   1. HaqqCad.exe as ONE self-contained file (the other PC does not need .NET installed)
#   2. HAQQ-CAD-Setup-<date>.exe    - installer: the same program; under this name it installs itself
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

# Any error from here on is shown, written to Installer\make-installer-error.txt, and stops the script.
$errorFile = Join-Path $outDir 'make-installer-error.txt'
if (Test-Path $errorFile) { Remove-Item -Force $errorFile -ErrorAction SilentlyContinue }
trap {
    $message = "ERROR: $($_.Exception.Message)`r`n$($_.InvocationInfo.PositionMessage)"
    Write-Host ''
    Write-Host $message -ForegroundColor Red
    try { Set-Content -Path $errorFile -Value $message -Encoding UTF8 } catch { }
    exit 1
}
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Force -Path $publish | Out-Null

# --- automatic updates -------------------------------------------------------------------------------
# update-url.txt (next to make-installer.cmd) holds the https address of the web folder you upload to,
# for example  https://www.example.com/haqqcad/  . With it, every copy of this build checks that folder
# for a newer build each time it starts. Without it, the build is made without the update check.
$build     = Get-Date -Format 'yyyyMMddHHmm'
$urlFile   = Join-Path (Split-Path -Parent $source) 'update-url.txt'
# Windows hides file extensions, so a file made in Notepad is often really named update-url.txt.txt.
if (-not (Test-Path $urlFile) -and (Test-Path ($urlFile + '.txt'))) { $urlFile = $urlFile + '.txt' }
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
# The publish folder was emptied before the build, so the program file being there is the proof of success.
$buildFailed = (-not (Test-Path $exe)) -or (($null -ne $proc.ExitCode) -and ($proc.ExitCode -ne 0))
if ($buildFailed) {
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
$zipTemp = Join-Path $work "HAQQ-CAD-Portable-$stamp.zip"
Compress-Archive -Path $exe, $readmePath -DestinationPath $zipTemp
try {
    Copy-Item -Force $zipTemp $zip
} catch {
    Write-Host "  The old portable zip is in use (open, or still uploading) and was not replaced: $zip" -ForegroundColor Yellow
}

Write-Host 'Step 3 of 3: installer...'
# The program installs itself when its file name starts with HAQQ-CAD-Setup (see SelfInstaller.cs),
# so the installer is simply a copy of the program under that name.
$setupName = "HAQQ-CAD-Setup-$stamp.exe"
Get-ChildItem -Path $outDir -Filter 'HAQQ-CAD-Setup-*.exe' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
Copy-Item -Force $exe (Join-Path $outDir $setupName)
$setupOk = Test-Path (Join-Path $outDir $setupName)
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
else { Write-Host '  The installer exe could not be copied. Send the portable zip instead.' -ForegroundColor Yellow }
if ($updateUrl) {
    Write-Host ''
    Write-Host "TO PUBLISH THIS BUILD TO YOUR TESTERS: upload the two files in 'Upload to web folder' to $updateUrl" -ForegroundColor Cyan
    Write-Host '  (HaqqCad.exe first, then haqq-cad-update.json). Their copies update the next time they start HAQQ CAD.'
}
exit 0
