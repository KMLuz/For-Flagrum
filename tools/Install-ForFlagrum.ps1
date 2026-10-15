# Builds ForFlagrum from this source folder and installs it to %LOCALAPPDATA%\ForFlagrum\app,
# with shortcuts on the desktop and in the Start menu.
# Mods and settings live in %LOCALAPPDATA%\Flagrum and are not touched.
# Run it through Install-ForFlagrum.bat in the folder above this one.

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$install = Join-Path $env:LOCALAPPDATA 'ForFlagrum'
$app = Join-Path $install 'app'
$staging = Join-Path $install 'app-new'
$buildLog = Join-Path $install 'build.log'

function Stop-WithError([string] $message) {
    Write-Host ''
    Write-Host "  $message" -ForegroundColor Red
    Write-Host ''
    Read-Host 'Press Enter to close'
    exit 1
}

Write-Host ''
Write-Host '  ForFlagrum setup' -ForegroundColor Yellow
Write-Host '  ================'
Write-Host "  Builds ForFlagrum from: $root"
Write-Host "  and installs it to:     $app"
Write-Host ''

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Stop-WithError "The .NET SDK isn't installed. Get it from https://dotnet.microsoft.com/download and run this again."
}

# Both the original Flagrum and ForFlagrum run as Flagrum.exe
if (Get-Process -Name 'Flagrum' -ErrorAction SilentlyContinue) {
    Stop-WithError 'Flagrum or ForFlagrum is still running. Close it and run this again.'
}

if (Test-Path $staging) {
    Remove-Item $staging -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $install | Out-Null

Write-Host '  Building... this takes a few minutes the first time.'
# Only show errors on screen (the code has many harmless warnings), and keep the full output in a log file
& dotnet publish (Join-Path $root 'src\Flagrum\Flagrum.csproj') -c Release -r win-x64 --self-contained false `
    -o $staging --nologo -tl:off -v quiet -clp:ErrorsOnly -fl "-flp:logfile=$buildLog;verbosity=normal"
if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host '  If this folder was unzipped over an older copy of ForFlagrum, old files left behind can break the' -ForegroundColor Yellow
    Write-Host '  build. Delete the folder, unzip the download into a new, empty folder and run this again.' -ForegroundColor Yellow
    Write-Host "  The full build output is in $buildLog"
    Stop-WithError "The build failed (see the errors above). Your installed ForFlagrum hasn't been changed."
}

# The original Flagrum releases ship these native libraries next to the app
Copy-Item (Join-Path $root 'libs\*.dll') $staging -Force

# Only replace the installed version once the new one has built successfully
if (Test-Path $app) {
    try {
        Remove-Item $app -Recurse -Force
    }
    catch {
        Stop-WithError "Couldn't replace the old version in $app. Close anything using it and try again."
    }
}

Move-Item $staging $app

$exe = Join-Path $app 'Flagrum.exe'
$shell = New-Object -ComObject WScript.Shell
foreach ($folder in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {
    $shortcut = $shell.CreateShortcut((Join-Path $folder 'ForFlagrum.lnk'))
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $app
    $shortcut.IconLocation = "$exe,0"
    $shortcut.Description = 'ForFlagrum'
    $shortcut.Save()
}

Write-Host ''
Write-Host '  Done! Start ForFlagrum from the shortcut on your desktop or in the Start menu.' -ForegroundColor Green
Write-Host '  To update later, download the new version, unzip it into a new, empty folder'
Write-Host '  and run Install-ForFlagrum.bat again.'
Write-Host ''
Read-Host 'Press Enter to close'
