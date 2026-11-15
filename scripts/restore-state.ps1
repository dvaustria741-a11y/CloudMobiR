# Restores the saved projects and Roblox login for the cloud PC user. Run after the local user exists.
# Never throws: a failed restore must not stop the session.
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
$tag = 'cloudpc-state'
$assetName = 'cloudpc-state.7z'

try {
    if ([string]::IsNullOrWhiteSpace($env:STATE_PASSWORD)) { Write-Host 'STATE_PASSWORD is not set; skipping restore.'; return }
    $user = $env:RDP_USERNAME
    $profileDir = "C:\Users\$user"

    $sevenZip = (Get-Command 7z -ErrorAction SilentlyContinue).Source
    if (-not $sevenZip -and (Test-Path "$env:ProgramFiles\7-Zip\7z.exe")) { $sevenZip = "$env:ProgramFiles\7-Zip\7z.exe" }
    if (-not $sevenZip) { Write-Host 'Restore skipped: 7z is not available on this runner.'; return }

    $dl = Join-Path $env:RUNNER_TEMP 'state-dl'
    $stage = Join-Path $env:RUNNER_TEMP 'state-restore'
    foreach ($p in @($dl, $stage)) { if (Test-Path $p) { Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue } }
    New-Item -ItemType Directory -Path $dl | Out-Null

    gh release download $tag -p $assetName -D $dl --clobber
    $archive = Join-Path $dl $assetName
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $archive)) { Write-Host 'No saved state found yet; starting with a fresh PC.'; return }

    & $sevenZip x $archive "-o$stage" "-p$($env:STATE_PASSWORD)" -y | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host "Restore failed: could not decrypt or extract the archive (7z exit code $LASTEXITCODE). Check STATE_PASSWORD."; return }

    # Create the profile folder now, because the user has not signed in yet on this fresh runner.
    if (-not (Test-Path $profileDir)) {
        Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class CloudPcUserEnv {
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    public static extern int CreateProfile(string pszUserSid, string pszUserName, StringBuilder pszProfilePath, uint cchProfilePath);
}
'@
        $sid = (New-Object System.Security.Principal.NTAccount($user)).Translate([System.Security.Principal.SecurityIdentifier]).Value
        $sb = New-Object System.Text.StringBuilder 260
        $hr = [CloudPcUserEnv]::CreateProfile($sid, $user, $sb, 260)
        if ($hr -ne 0 -and $hr -ne -2147024713) { Write-Host ("CreateProfile failed with HRESULT 0x{0:X8}." -f $hr); return }
    }
    if (-not (Test-Path $profileDir)) { Write-Host "Restore failed: profile folder $profileDir still does not exist."; return }

    foreach ($d in @('Desktop', 'Documents')) {
        if (Test-Path "$stage\$d") {
            robocopy "$stage\$d" "$profileDir\$d" /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
            icacls "$profileDir\$d" /grant "${user}:(OI)(CI)F" /T /C /Q | Out-Null
        }
    }
    if (Test-Path "$stage\LocalRoblox") {
        $rb = "$profileDir\AppData\Local\Roblox"
        robocopy "$stage\LocalRoblox" $rb /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
        icacls $rb /grant "${user}:(OI)(CI)F" /T /C /Q | Out-Null
    }

    # Registry: load the user's hive, import the saved keys under it, unload.
    $reg = "$stage\roblox-hkcu.reg"
    if (Test-Path $reg) {
        $hiveName = 'CloudPcRestore'
        $hiveFile = "$profileDir\NTUSER.DAT"
        reg load "HKU\$hiveName" $hiveFile | Out-Null
        if ($LASTEXITCODE -eq 0) {
            $tmpReg = Join-Path $stage 'roblox-hkcu-retargeted.reg'
            (Get-Content $reg) -replace 'HKEY_USERS\\S-[0-9-]+', "HKEY_USERS\$hiveName" | Set-Content -Path $tmpReg -Encoding Unicode
            reg import $tmpReg | Out-Null
            if ($LASTEXITCODE -ne 0) { Write-Host 'Registry import failed.' }
            [gc]::Collect(); [gc]::WaitForPendingFinalizers()
            for ($i = 0; $i -lt 5; $i++) { reg unload "HKU\$hiveName" 2>$null | Out-Null; if ($LASTEXITCODE -eq 0) { break }; Start-Sleep -Seconds 2 }
        } else { Write-Host 'Registry restore skipped: could not load the user hive.' }
    }
    Write-Host 'Saved state restored. Roblox login may still be asked for again if Windows cannot decrypt the stored session.'
} catch {
    Write-Host "Restore failed: $_"
} finally {
    foreach ($p in @($dl, $stage)) { if ($p -and (Test-Path $p)) { Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue } }
}
