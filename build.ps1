# Builds the browser and packs it into the installer: dist\LiteBrowser-Setup.exe
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$build = Join-Path $root 'build'

Remove-Item $build -Recurse -Force -ErrorAction SilentlyContinue
dotnet build "$root\src\Browser\LiteBrowser.csproj" -c Release -o "$build\app" --nologo -v q
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

dotnet build "$root\src\Setup\Setup.csproj" -c Release -o "$build\setup" --nologo -v q
if ($LASTEXITCODE) { throw 'installer build failed' }
New-Item -ItemType Directory -Force "$root\dist" | Out-Null
Copy-Item "$build\setup\LiteBrowser-Setup.exe" "$root\dist\" -Force
Get-Item "$root\dist\LiteBrowser-Setup.exe" | Select-Object FullName, @{ n = 'KB'; e = { [math]::Round($_.Length / 1KB) } }
