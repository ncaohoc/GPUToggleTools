# Makes a GitHub release of GPU Toggle:
#   bumps VERSION, builds GpuToggle.exe, commits + tags "vX.Y.Z", pushes main and the tag,
#   then creates the GitHub release with GpuToggle.exe and a zip attached.
#
#   powershell -ExecutionPolicy Bypass -File release.ps1                 patch bump 1.2.3 -> 1.2.4
#   powershell -ExecutionPolicy Bypass -File release.ps1 -Bump minor     1.2.3 -> 1.3.0
#   powershell -ExecutionPolicy Bypass -File release.ps1 -Version 2.0.0  exact version
#   powershell -ExecutionPolicy Bypass -File release.ps1 -DryRun         bump + build + package only, nothing committed or pushed
#   -Notes "text"   release notes (default: commit subjects since the last release)
#   -Draft          create the GitHub release as a draft
#
# If VERSION has never been released (no tag vX.Y.Z yet) it is released as is, without a bump.
# Needs git and the GitHub CLI (gh auth login). ASCII only, no backslashes (see build.bat).

param(
    [ValidateSet('patch', 'minor', 'major')] [string]$Bump = 'patch',
    [string]$Version,
    [string]$Notes,
    [switch]$Draft,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Run([string]$exe, [string[]]$argv) {
    & $exe @argv
    if ($LASTEXITCODE -ne 0) { throw ($exe + ' ' + ($argv -join ' ') + ' failed (exit ' + $LASTEXITCODE + ')') }
}
function Git1([string[]]$argv) { $o = & git @argv; if ($LASTEXITCODE -ne 0) { throw ('git ' + ($argv -join ' ') + ' failed') }; return (($o | Out-String).Trim()) }

# ---- checks
$branch = Git1 @('rev-parse', '--abbrev-ref', 'HEAD')
if ($branch -ne 'main' -and -not $DryRun) { throw "Releases are made from main (now on '$branch')." }
$dirty = Git1 @('status', '--porcelain')
if ($dirty -and -not $DryRun) { throw ("Uncommitted changes, commit or stash them first:`n" + $dirty) }
if (-not $DryRun) {
    Run git @('fetch', '--quiet', 'origin', 'main', '--tags')
    $behind = [int](Git1 @('rev-list', '--count', 'HEAD..origin/main'))
    if ($behind -gt 0) { throw "main is $behind commit(s) behind origin/main. Pull first." }
    & gh auth status *> $null
    if ($LASTEXITCODE -ne 0) { throw 'The GitHub CLI is not logged in. Run: gh auth login' }
}

# ---- version
$cur = (Get-Content VERSION -TotalCount 1).Trim()
if ($cur -notmatch '^[0-9]+[.][0-9]+[.][0-9]+$') { throw "VERSION must look like 1.2.3 (found '$cur')." }
if ($Version) { $new = $Version.Trim().TrimStart('v') }
elseif (-not (Git1 @('tag', '--list', "v$cur"))) { $new = $cur }
else {
    $p = $cur.Split('.') | ForEach-Object { [int]$_ }
    switch ($Bump) {
        'major' { $new = '{0}.0.0' -f ($p[0] + 1) }
        'minor' { $new = '{0}.{1}.0' -f $p[0], ($p[1] + 1) }
        default { $new = '{0}.{1}.{2}' -f $p[0], $p[1], ($p[2] + 1) }
    }
}
if ($new -notmatch '^[0-9]+[.][0-9]+[.][0-9]+$') { throw "Bad version '$new'." }
$tag = "v$new"
if (Git1 @('tag', '--list', $tag)) { throw "Tag $tag already exists." }
Write-Host "Releasing $tag (VERSION was $cur)" -ForegroundColor Cyan

# ---- build
Set-Content -Path VERSION -Value $new -Encoding Ascii
try {
    $env:NOPAUSE = '1'
    & cmd.exe /c (Join-Path $PSScriptRoot 'build.bat')   # full path: cmd may not start in this folder
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
catch {
    Set-Content -Path VERSION -Value $cur -Encoding Ascii
    throw
}

# ---- package
$out = Join-Path $PSScriptRoot 'release'
New-Item -ItemType Directory -Force $out | Out-Null
Get-ChildItem $out -File | Remove-Item -Force
$exe = Join-Path $out 'GpuToggle.exe'
$zip = Join-Path $out "GpuToggle-$tag.zip"
Copy-Item GpuToggle.exe $exe
Compress-Archive -Path GpuToggle.exe, README.md -DestinationPath $zip -Force
$sha = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
Write-Host "Packaged $exe and $zip (SHA-256 $sha)"

# ---- notes: commit subjects since the previous release
$prev = ((Git1 @('tag', '--list', 'v[0-9]*', '--merged', 'HEAD', '--sort=-v:refname')) -split "`n")[0].Trim()
$range = if ($prev) { "$prev..HEAD" } else { 'HEAD' }
$log = & git log $range --no-merges --pretty=format:'- %s' | Where-Object { $_ -notmatch '^- Release v' }
if (-not $Notes) { $Notes = if ($log) { "## Changes`n" + ($log -join "`n") } else { 'Maintenance release.' } }
$body = $Notes + "`n`n## Download`n" +
        "Download **GpuToggle.exe** below and run it (it asks for administrator rights, needed to enable / disable GPUs). " +
        "Needs Windows 10 / 11 with .NET Framework 4.x, which is built into Windows.`n`n" +
        "SHA-256 of GpuToggle.exe: ``$sha``"

if ($DryRun) {
    Set-Content -Path VERSION -Value $cur -Encoding Ascii
    Write-Host "`nDry run: VERSION restored to $cur, nothing committed or pushed. Release notes would be:`n" -ForegroundColor Yellow
    Write-Host $body
    return
}

# ---- commit, tag, push, GitHub release
Run git @('add', 'VERSION')
& git diff --cached --quiet -- VERSION
if ($LASTEXITCODE -ne 0) { Run git @('commit', '--quiet', '-m', "Release $tag") }   # unchanged on the first release of a number
Run git @('tag', '-a', $tag, '-m', "GPU Toggle $tag")
Run git @('push', 'origin', 'main')
Run git @('push', 'origin', $tag)

$notesFile = Join-Path $out 'notes.md'
[IO.File]::WriteAllText($notesFile, $body, (New-Object Text.UTF8Encoding $false))
$ghArgs = @('release', 'create', $tag, $exe, $zip, '--title', "GPU Toggle $tag", '--notes-file', $notesFile, '--latest')
if ($Draft) { $ghArgs += '--draft' }
Run gh $ghArgs
Remove-Item $notesFile -Force

$repo = Git1 @('remote', 'get-url', 'origin')
$repo = $repo -replace '[.]git$', ''
Write-Host "`nReleased $tag" -ForegroundColor Green
Write-Host "Release page:   $repo/releases/tag/$tag"
Write-Host "Latest download: $repo/releases/latest/download/GpuToggle.exe"
