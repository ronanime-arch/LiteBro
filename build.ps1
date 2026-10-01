# Builds the browser and packs it into the installer: dist\LiteBro-Setup.exe
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$build = Join-Path $root 'build'

# The version is never edited by hand: commit 4f0130c was 1.9.7, and every commit on main since adds one
# (--first-parent: a merged PR counts once, however many commits it has)
$since = git -C $root rev-list --count --first-parent 4f0130c5c25c846cabc49732a3e3dfcc758ca087..HEAD
if ($LASTEXITCODE) { throw 'the version comes from git: build from a clone of the repository' }
$version = "1.9.$(7 + [int]$since)"

# uBlock Origin Lite, built in (AdBlock.cs): the Chromium package of a pinned release from its official repository,
# checked by the commit it must be at. Kept in .cache between builds. A newer release: change both lines.
$ubolTag = 'uBOLite_2025.831.1814'
$ubolCommit = 'c435af056fcd1f0ad2096d9a4dd870d9bf1efa94'
$ubol = Join-Path $root ".cache\$ubolTag"
if (-not (Test-Path "$ubol\chromium\manifest.json")) {
    Remove-Item $ubol -Recurse -Force -ErrorAction SilentlyContinue
    # Only the chromium folder is fetched; line endings as published
    git -c advice.detachedHead=false clone --quiet --depth 1 --filter=blob:none --sparse --config core.autocrlf=false --branch $ubolTag https://github.com/uBlockOrigin/uBOL-home $ubol
    if ($LASTEXITCODE) { throw 'uBlock Origin Lite could not be fetched from github.com' }
    git -C $ubol sparse-checkout set chromium
    if ($LASTEXITCODE) { throw 'uBlock Origin Lite could not be fetched from github.com' }
}
if ((git -C $ubol rev-parse HEAD) -ne $ubolCommit) { throw "uBlock Origin Lite: $ubolTag is not at commit $ubolCommit" }

Remove-Item $build -Recurse -Force -ErrorAction SilentlyContinue
dotnet build "$root\src\Browser\LiteBro.csproj" -c Release -o "$build\app" --nologo -v q -p:Version=$version
if ($LASTEXITCODE) { throw 'browser build failed' }
Copy-Item "$ubol\chromium" "$build\app\ublock" -Recurse
Remove-Item "$build\app\ublock\log.txt" -ErrorAction SilentlyContinue # the release's build log

# Forward slashes in entry names, which the installer's ZipArchive expects
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open("$build\payload.zip", 'Create')
try {
    # The build's .xml and .pdb stay out; the extension's files all go in (it has .xml files of its own)
    Get-ChildItem "$build\app" -Recurse -File | Where-Object { $_.Extension -notin '.xml', '.pdb' -or $_.FullName.StartsWith("$build\app\ublock\") } | ForEach-Object {
        $name = $_.FullName.Substring("$build\app\".Length).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $name, 'Optimal')
    }
}
finally { $zip.Dispose() }

dotnet build "$root\src\Setup\Setup.csproj" -c Release -o "$build\setup" --nologo -v q -p:Version=$version
if ($LASTEXITCODE) { throw 'installer build failed' }
"LiteBro $version"
New-Item -ItemType Directory -Force "$root\dist" | Out-Null
Copy-Item "$build\setup\LiteBro-Setup.exe" "$root\dist\" -Force
Get-Item "$root\dist\LiteBro-Setup.exe" | Select-Object FullName, @{ n = 'KB'; e = { [math]::Round($_.Length / 1KB) } }
