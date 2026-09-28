# Builds the browser and packs it into the installer: dist\LiteBro-Setup.exe
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$build = Join-Path $root 'build'

# The version is never edited by hand: commit 4f0130c was 1.9.7, and every commit on main since adds one
# (--first-parent: a merged PR counts once, however many commits it has)
$since = git -C $root rev-list --count --first-parent 4f0130c5c25c846cabc49732a3e3dfcc758ca087..HEAD
if ($LASTEXITCODE) { throw 'the version comes from git: build from a clone of the repository' }
$version = "1.9.$(7 + [int]$since)"
# FileVersion too: else the file's version text gets a fourth number, 1.9.x.0

Remove-Item $build -Recurse -Force -ErrorAction SilentlyContinue
dotnet build "$root\src\Browser\LiteBro.csproj" -c Release -o "$build\app" --nologo -v q -p:Version=$version -p:FileVersion=$version
if ($LASTEXITCODE) { throw 'browser build failed' }

# Forward slashes in entry names, which the installer's ZipArchive expects
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open("$build\payload.zip", 'Create')
try {
    Get-ChildItem "$build\app" -Recurse -File | Where-Object { $_.Extension -notin '.xml', '.pdb' } | ForEach-Object {
        $name = $_.FullName.Substring("$build\app\".Length).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $name, 'Optimal')
    }
}
finally { $zip.Dispose() }

dotnet build "$root\src\Setup\Setup.csproj" -c Release -o "$build\setup" --nologo -v q -p:Version=$version -p:FileVersion=$version
if ($LASTEXITCODE) { throw 'installer build failed' }
"LiteBro $version"
New-Item -ItemType Directory -Force "$root\dist" | Out-Null
Copy-Item "$build\setup\LiteBro-Setup.exe" "$root\dist\" -Force
Get-Item "$root\dist\LiteBro-Setup.exe" | Select-Object FullName, @{ n = 'KB'; e = { [math]::Round($_.Length / 1KB) } }
