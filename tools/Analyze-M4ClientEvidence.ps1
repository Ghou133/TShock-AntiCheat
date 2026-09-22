#requires -Version 7.2
param(
    [string]$EventsPath = (Join-Path $PSScriptRoot '../artifacts/network-runs/network-adapter-20260910T004906143Z/gameplay-events-20260910T004908381Z.jsonl'),
    [string]$StdoutPath = (Join-Path $PSScriptRoot '../artifacts/network-runs/network-adapter-20260910T004906143Z/stdout.log'),
    [string]$OutputRoot = (Join-Path $PSScriptRoot '../artifacts/m4-client-analysis'),
    [ValidateRange(1, 256)][int]$MaximumInputMiB = 80
)
$ErrorActionPreference = 'Stop'
$analysisDirectory = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ('analysis-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
New-Item -ItemType Directory -Path $analysisDirectory -Force | Out-Null

function Capture-Prefix([string]$Path, [string]$Destination) {
    $sourcePath = [IO.Path]::GetFullPath($Path)
    $startedUtc = [DateTime]::UtcNow.ToString('o')
    # Bound the read at the length observed after opening. A concurrent append is excluded.
    $stream = [IO.File]::Open($sourcePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $lengthAtOpen = $stream.Length
        if ($lengthAtOpen -gt $MaximumInputMiB * 1MB) { throw "Input exceeds $MaximumInputMiB MiB: $sourcePath" }
        $bytes = [byte[]]::new([int]$lengthAtOpen)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            if ($read -eq 0) { throw "Input truncated while capturing: $sourcePath" }
            $offset += $read
        }
    }
    finally { $stream.Dispose() }
    [IO.File]::WriteAllBytes($Destination, $bytes)
    $completeLength = $bytes.Length
    while ($completeLength -gt 0 -and $bytes[$completeLength - 1] -ne 10) { $completeLength-- }
    $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
    $completeText = $strictUtf8.GetString($bytes, 0, $completeLength).TrimStart([char]0xFEFF)
    return [pscustomobject]@{
        metadata = [ordered]@{
            sourcePath = $sourcePath; capturedPath = $Destination; startedUtc = $startedUtc
            completedUtc = [DateTime]::UtcNow.ToString('o'); lengthAtOpen = $lengthAtOpen
            capturedBytes = $bytes.Length; parsedCompleteLineBytes = $completeLength
            excludedIncompleteTailBytes = $bytes.Length - $completeLength
            sha256 = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash
            boundary = 'Exactly the byte length at open; complete LF-terminated lines only are parsed.'
        }
        text = $completeText
    }
}

function Utc-Text($Value) { ([DateTimeOffset]$Value).UtcDateTime.ToString('o') }
function Write-Json([string]$Name, $Value) {
    $jsonText = ConvertTo-Json -InputObject $Value -Depth 30
    [IO.File]::WriteAllText((Join-Path $analysisDirectory $Name), $jsonText + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}
function Compact-State($Row) {
    $payload = $Row.payload
    return [ordered]@{
        sourceLine = $Row.sourceLine; utc = $Row.utc; sequence = $Row.sequence; label = $payload.label
        worldId = $payload.worldID; ssc = $payload.ssc
        players = @($payload.players | ForEach-Object {
            [ordered]@{
                slot = $_.Index; name = $_.Name; loggedIn = $_.IsLoggedIn
                accountId = $_.accountId; accountName = $_.accountName; group = $_.group
                health = $_.health; rawLifeMaximum = $_.rawLifeMaximum; effectiveLifeMaximum = $_.effectiveLifeMaximum
                mana = $_.mana; rawManaMaximum = $_.rawManaMaximum; effectiveManaMaximum = $_.effectiveManaMaximum
                dead = $_.dead; activeChest = $_.ActiveChest; disabledForSsc = $_.IsDisabledForSSC
                inventory = @($_.inventory)
            }
        })
        chestPair = $payload.chestPair
        activeNpcs = @($payload.npcs | Where-Object active | Select-Object index, type, life)
        recordingHealth = $payload.recordingHealth
    }
}

$eventsCapture = Capture-Prefix $EventsPath (Join-Path $analysisDirectory 'events-prefix.jsonl')
$stdoutCapture = Capture-Prefix $StdoutPath (Join-Path $analysisDirectory 'stdout-prefix.log')
$rows = [Collections.Generic.List[object]]::new()
$sourceLine = 0
foreach ($line in ($eventsCapture.text -split "`n")) {
    $sourceLine++
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    try { $parsed = ConvertFrom-Json -InputObject $line }
    catch { throw "Invalid complete JSONL line $sourceLine : $($_.Exception.Message)" }
    $rows.Add([pscustomobject]@{
        sourceLine = $sourceLine; utc = Utc-Text $parsed.utc; sequence = $parsed.sequence
        kind = $parsed.kind; payload = $parsed.payload
    })
}
# The journal combines queued main-thread snapshots and synchronous hook writes. Its
# optional sequence is not a global ordering key; retain source lines and sort UTC.
$orderedRows = @($rows | Sort-Object utc, sourceLine)
$states = @($orderedRows | Where-Object kind -eq 'server-state' | ForEach-Object { Compact-State $_ })
$stateChanges = [Collections.Generic.List[object]]::new()
$lastSignature = $null
foreach ($state in $states) {
    $signature = ConvertTo-Json -InputObject @($state.players, $state.chestPair, $state.activeNpcs) -Depth 20 -Compress
    if ($signature -ne $lastSignature -or $state.label -notlike 'periodic-*') { $stateChanges.Add($state) }
    $lastSignature = $signature
}
$markers = @($orderedRows | Where-Object kind -eq 'console-command' | ForEach-Object {
    [ordered]@{sourceLine=$_.sourceLine; utc=$_.utc; sequence=$_.sequence; command=$_.payload.Command; arguments=@($_.payload.Arguments)}
})
$rawFrames = @($orderedRows | Where-Object kind -eq 'raw-client-experiment-frame' | ForEach-Object {
    $decoded = $null
    $payloadHex = [string]$_.payload.payloadHex
    # Only decode the audited fixed five-byte raw life/mana structures. Other
    # packets retain exact hex, including compressed target-version NPC encodings.
    if ($_.payload.packetId -in 16,42 -and $_.payload.payloadBytes -eq 5 -and $payloadHex -match '^[0-9A-Fa-f]{10}$') {
        $bytes = [Convert]::FromHexString($payloadHex)
        $current = [int]$bytes[1] -bor ([int]$bytes[2] -shl 8)
        $maximum = [int]$bytes[3] -bor ([int]$bytes[4] -shl 8)
        if ($current -ge 32768) { $current -= 65536 }
        if ($maximum -ge 32768) { $maximum -= 65536 }
        $decoded = [ordered]@{senderClaim=$bytes[0]; current=$current; rawMaximum=$maximum; schema='target-1.4.5.8-raw-vitals-int16-le'}
    }
    [ordered]@{
        sourceLine=$_.sourceLine; utc=$_.utc; packetId=$_.payload.packetId; playerSlot=$_.payload.playerIndex
        payloadBytes=$_.payload.payloadBytes; payloadHex=$payloadHex; decoded=$decoded
        handledObserved=$_.payload.handledObserved; hookPriority=$_.payload.hookPriority
    }
})
$packetEvents = @($orderedRows | Where-Object {$_.kind -in 'player-slot-packet','chest-item-packet','chest-open-packet'} |
    Select-Object sourceLine,utc,kind,payload)
$healthChanges = [Collections.Generic.List[object]]::new()
$lastHealth = @{}
$chestChanges = [Collections.Generic.List[object]]::new()
$lastChests = $null
foreach ($state in $states) {
    $presentSlots = @($state.players | ForEach-Object { [string]$_.slot })
    foreach ($oldSlot in @($lastHealth.Keys)) { if ($oldSlot -notin $presentSlots) { $lastHealth.Remove($oldSlot) } }
    foreach ($player in $state.players) {
        $slotKey = [string]$player.slot
        $healthKey = "$($player.loggedIn):$($player.accountName):$($player.health):$($player.rawLifeMaximum):$($player.dead)"
        if ($lastHealth[$slotKey] -ne $healthKey) {
            $healthChanges.Add([ordered]@{sourceLine=$state.sourceLine;utc=$state.utc;sequence=$state.sequence;label=$state.label;player=$player})
        }
        $lastHealth[$slotKey] = $healthKey
    }
    $chestKey = ConvertTo-Json -InputObject $state.chestPair -Depth 12 -Compress
    if ($chestKey -ne $lastChests) {
        $chestChanges.Add([ordered]@{sourceLine=$state.sourceLine;utc=$state.utc;sequence=$state.sequence;label=$state.label;chestPair=$state.chestPair})
    }
    $lastChests = $chestKey
}
$stdoutLines = @($stdoutCapture.text -split "`n")
$notableStdout = @(
    for ($i=0; $i -lt $stdoutLines.Count; $i++) {
        if ($stdoutLines[$i] -match 'ANTICHEAT_INCIDENT|ANTICHEAT_REVOKED|ANTICHEAT_RULE_INPUT|Server executed: /(?:slap|qa_mark)|authenticated successfully|has joined|has left|was booted|kicked|banned') {
            [ordered]@{sourceLine=$i+1;text=$stdoutLines[$i].TrimEnd("`r")}
        }
    }
)
$healthSignals = @($states | ForEach-Object {$_.recordingHealth} | Where-Object {$null -ne $_})
$summary = [ordered]@{
    schemaVersion=1; generatedUtc=[DateTime]::UtcNow.ToString('o'); analysisKind='offline-observation-only'
    scriptPath=$PSCommandPath; scriptSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
    inputs=@($eventsCapture.metadata,$stdoutCapture.metadata)
    firstEventUtc=($orderedRows | Select-Object -First 1).utc; lastEventUtc=($orderedRows | Select-Object -Last 1).utc
    eventCount=$rows.Count; kindCounts=@($rows|Group-Object kind|Sort-Object Name|Select-Object Name,Count)
    stateCount=$states.Count; stateChangeCount=$stateChanges.Count; rawFrameCount=$rawFrames.Count
    rawPacketCounts=@($rawFrames|Group-Object { $_.packetId }|Sort-Object Name|Select-Object Name,Count)
    incidentLines=@($notableStdout|Where-Object {$_.text -match 'ANTICHEAT_INCIDENT'})
    recordingHealth=[ordered]@{
        sampledCount=$healthSignals.Count; last=($healthSignals|Select-Object -Last 1)
        maximumDropped=($healthSignals|Measure-Object dropped -Maximum).Maximum
        journalLimitObserved=@($healthSignals|Where-Object journalLimitReported).Count -gt 0
    }
    limitations=@(
        'Markers are operator labels, not proof an action succeeded; inspect real state and raw-frame chronology.'
        'Raw hook receipt/Handled observation does not establish final cancellation, delivery, or client processing.'
        'No missing-packet or unchanged-health observation is converted into a cheating verdict.'
        'Snapshot account names and slots are observations; missing accountId/sessionGeneration remain unknown.'
        'Inputs are independent finite byte-prefix captures of concurrently appended files, not one atomic server snapshot.'
        'Only life/mana five-byte frames are decoded here; other target packet encodings retain raw hex.'
        'Journal health is sampled; zero sampled drops does not prove completeness of uninstrumented paths.'
    )
    files=@('summary.json','state-changes.json','markers.json','raw-frames.json','packet-events.json','health-changes.json','chest-changes.json','notable-stdout.json','events-prefix.jsonl','stdout-prefix.log')
}
Write-Json 'summary.json' $summary
Write-Json 'state-changes.json' $stateChanges.ToArray()
Write-Json 'markers.json' $markers
Write-Json 'raw-frames.json' $rawFrames
Write-Json 'packet-events.json' $packetEvents
Write-Json 'health-changes.json' $healthChanges.ToArray()
Write-Json 'chest-changes.json' $chestChanges.ToArray()
Write-Json 'notable-stdout.json' $notableStdout
[ordered]@{analysisDirectory=$analysisDirectory;events=$rows.Count;states=$states.Count;rawFrames=$rawFrames.Count;lastEventUtc=$summary.lastEventUtc;incompleteTailBytes=$eventsCapture.metadata.excludedIncompleteTailBytes;incidents=$summary.incidentLines.Count}|ConvertTo-Json -Compress
