# Records each game's demo and turns it into a small looping GIF in docs/media, for the README and the web page.
#   .\tools\record-gifs.ps1                          every game in tools\record-gifs.csv
#   .\tools\record-gifs.ps1 -Games hoops,chess       only these
#   .\tools\record-gifs.ps1 -Exe .\dist\DeskArcade.exe   an existing build instead of a fresh Release build
#
# Each game runs as its own copy (--profile gif, so never the installed game's settings or scores) with --demo, so it
# plays by itself, and --record writes PNG frames of the overlay over a plain blue desktop. --record-windows puts two
# stand-in editor windows on it, the same on every PC, so there are window tops to play on. ffmpeg (on PATH) then crops
# each recording as the table says, scales it and makes a GIF with a palette of its own (palettegen, paletteuse), using
# fewer colours and then fewer frames until it fits under -MaxMB. The overlay shows on screen while it records: the
# delay plus a few seconds a game.
#
# tools\record-gifs.csv, one game a line: game (the --game id; the GIF is docs\media\<game>.gif), delay (seconds of play
# before the first frame), seconds (the length), crop ("x,y,w,h" as fractions of the screen), width (of the GIF) and
# takes. Some demos are random (the pet does something every few seconds, or nothing); with takes above 1 the game is
# recorded that many times and the take with the largest GIF is kept, since more happening makes a bigger GIF.
# tools/record-gifs.sh does the same on Linux and macOS.
param(
    [string[]]$Games,
    [string]$Exe,
    [string]$OutDir,
    [int]$Fps = 15,
    [double]$MaxMB = 2
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $root 'docs/media' }
if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) { throw 'ffmpeg is not on PATH (winget install Gyan.FFmpeg)' }

# Stand-in windows, as fractions of the screen, topmost first: a small one on the left and a taller one in the middle.
$windows = '0.04,0.50,0.30,0.50;0.40,0.34,0.34,0.66'

if (-not $Exe) {
    Write-Host 'Building Desk Arcade (Release)'
    dotnet build (Join-Path $root 'DeskArcade.csproj') -c Release -v q -nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)" }
    $Exe = Join-Path $root 'bin/Release/net10.0/DeskArcade'
    if (Test-Path "$Exe.exe") { $Exe = "$Exe.exe" }
}
$Exe = (Resolve-Path $Exe).Path

$table = Import-Csv (Join-Path $PSScriptRoot 'record-gifs.csv')
if ($Games) { $table = $table | Where-Object { $Games -contains $_.game } }
if (-not $table) { throw "None of '$($Games -join ', ')' is in record-gifs.csv" }

$work = Join-Path ([IO.Path]::GetTempPath()) 'deskarcade-gifs'
New-Item -ItemType Directory -Force $work, $OutDir | Out-Null

function Invoke-Game([string[]]$arguments, [int]$timeout) {
    # quoted by hand: Windows PowerShell passes -ArgumentList on as one command line
    $quoted = $arguments | ForEach-Object { if ($_ -match '[\s;]') { "`"$_`"" } else { $_ } }
    $process = Start-Process -FilePath $Exe -ArgumentList $quoted -PassThru
    if (-not $process.WaitForExit($timeout * 1000)) {
        $process.Kill()
        throw "Desk Arcade did not quit within $timeout seconds ($($arguments -join ' '))"
    }
}

# A first start shows a welcome for a few seconds; get it over with once, before anything is recorded.
Invoke-Game @('--profile', 'gif', '--game', 'pet', '--snapshot', (Join-Path $work 'warm-up.png'), '--snapshot-delay', '1') 60

# Crops, scales and encodes a recording's frames as a GIF with a palette of its own.
function ConvertTo-Gif($row, [string]$frames, [string]$gif, [int]$colors, [int]$rate) {
    $x, $y, $w, $h = $row.crop.Split(',')
    $filter = "crop=iw*${w}:ih*${h}:iw*${x}:ih*${y},fps=$rate,scale=$($row.width):-2:flags=lanczos,split[a][b];" +
        "[a]palettegen=max_colors=${colors}:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle"
    ffmpeg -v error -y -f concat -safe 0 -i (Join-Path $frames 'frames.txt') -vf $filter -loop 0 $gif
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed on $($row.game)" }
    (Get-Item $gif).Length / 1MB
}

foreach ($row in $table) {
    $takes = [Math]::Max(1, [int]$row.takes)
    $best = $null
    for ($take = 1; $take -le $takes; $take++) {
        $frames = Join-Path $work "$($row.game)-$take"
        Remove-Item $frames -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "Recording $($row.game) (take $take of $takes): $($row.seconds) s after $($row.delay) s of play"
        Invoke-Game @('--profile', 'gif', '--demo', '--game', $row.game, '--record', $frames, '--record-seconds', $row.seconds,
            '--record-fps', $Fps, '--record-delay', $row.delay, '--record-windows', $windows) ([int]$row.delay + [int]$row.seconds + 60)
        if (-not (Test-Path (Join-Path $frames 'frames.txt'))) { throw "$($row.game): no frames were recorded" }
        $mb = ConvertTo-Gif $row $frames (Join-Path $work "$($row.game)-$take.gif") 128 $Fps
        if (-not $best -or $mb -gt $best.MB) {
            if ($best) { Remove-Item $best.Frames -Recurse -Force }
            $best = @{ Frames = $frames; MB = $mb }
        }
        else { Remove-Item $frames -Recurse -Force }
    }

    # The best take, with fewer colours and then fewer frames until it fits.
    $gif = Join-Path $OutDir "$($row.game).gif"
    foreach ($try in @(@(128, $Fps), @(96, $Fps), @(64, $Fps), @(64, 10), @(48, 8))) {
        $colors, $rate = $try
        $mb = ConvertTo-Gif $row $best.Frames $gif $colors $rate
        if ($mb -le $MaxMB) { break }
    }
    Write-Host ("  {0}: {1:0.00} MB, {2} colours, {3} fps" -f $gif, $mb, $colors, $rate)
    Remove-Item $best.Frames -Recurse -Force
}
