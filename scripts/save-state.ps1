# Saves the cloud PC user's projects and Roblox login to an encrypted 7z asset on the "cloudpc-state" release.
# Never throws: a failed save must not end the session.
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
$tag = 'cloudpc-state'
$assetName = 'cloudpc-state.7z'
$maxBytes = 1500MB

try {
    if ([string]::IsNullOrWhiteSpace($env:STATE_PASSWORD)) { Write-Host 'STATE_PASSWORD is not set; skipping save.'; return }
    $user = $env:RDP_USERNAME
    $profileDir = "C:\Users\$user"
    if (-not (Test-Path $profileDir)) { Write-Host "No profile at $profileDir; nothing to save."; return }

    $sevenZip = (Get-Command 7z -ErrorAction SilentlyContinue).Source
    if (-not $sevenZip -and (Test-Path "$env:ProgramFiles\7-Zip\7z.exe")) { $sevenZip = "$env:ProgramFiles\7-Zip\7z.exe" }
    if (-not $sevenZip) { Write-Host 'Save skipped: 7z is not available on this runner.'; return }

    $stage = Join-Path $env:RUNNER_TEMP 'state-stage'
    $archive = Join-Path $env:RUNNER_TEMP $assetName
    foreach ($p in @($stage, $archive)) { if (Test-Path $p) { Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue } }
    New-Item -ItemType Directory -Path $stage | Out-Null

    # Projects and files
    foreach ($d in @('Desktop', 'Documents')) {
        if (Test-Path "$profileDir\$d") {
            robocopy "$profileDir\$d" "$stage\$d" /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
        }
    }
    # Roblox settings, plugins, autosaves and login storage; the big caches and installed versions are excluded.
    $rb = "$profileDir\AppData\Local\Roblox"
    if (Test-Path $rb) {
        robocopy $rb "$stage\LocalRoblox" /E /R:1 /W:1 /XD Versions logs Downloads http rbx-storage Crashpad /NFL /NDL /NJH /NJS /NP | Out-Null
    }
    # Roblox registry settings, only possible while the user's hive is loaded (after the first RDP sign-in).
    try {
        $sid = (New-Object System.Security.Principal.NTAccount($user)).Translate([System.Security.Principal.SecurityIdentifier]).Value
        "$sid" | Set-Content -Path "$stage\user-sid.txt" -Encoding ascii
        if (Test-Path "Registry::HKEY_USERS\$sid\Software\Roblox Corporation") {
            reg export "HKU\$sid\Software\Roblox Corporation" "$stage\roblox-hkcu.reg" /y | Out-Null
        }
    } catch { Write-Host "Registry export skipped: $_" }

    & $sevenZip a -t7z -mx=3 -mhe=on "-p$($env:STATE_PASSWORD)" $archive "$stage\*" | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $archive)) { Write-Host "Save failed: 7z exited with code $LASTEXITCODE."; return }
    $size = (Get-Item $archive).Length
    if ($size -gt $maxBytes) { Write-Host "Save skipped: archive is $([math]::Round($size / 1MB)) MB, above the $([math]::Round($maxBytes / 1MB)) MB limit."; return }

    gh release view $tag *> $null
    if ($LASTEXITCODE -ne 0) {
        gh release create $tag --title 'Cloud PC saved state (encrypted)' --notes 'Encrypted archive written by the Cloud PC workflow. Do not delete.' --target main
        if ($LASTEXITCODE -ne 0) { Write-Host 'Save failed: could not create the release.'; return }
    }
    gh release upload $tag $archive --clobber
    if ($LASTEXITCODE -ne 0) { Write-Host 'Save failed: release upload error.'; return }
    Write-Host "Saved state: $([math]::Round($size / 1MB, 1)) MB at $((Get-Date).ToUniversalTime().ToString('u'))"
    Set-Content -Path (Join-Path $env:RUNNER_TEMP 'last-save-ok.txt') -Value (Get-Date).ToUniversalTime().ToString('o')
} catch {
    Write-Host "Save failed: $_"
} finally {
    foreach ($p in @($stage, $archive)) { if ($p -and (Test-Path $p)) { Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue } }
    # A failed native command (for example gh when no release exists yet) must not become the step's exit code.
    $global:LASTEXITCODE = 0
}
