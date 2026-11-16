# CloudMobiR

A Windows cloud PC you start from your phone: a GitHub Actions run boots a Windows Server 2025 runner, enables Remote Desktop, joins your Tailscale network, and you connect with the Windows App (RDP).

## One-time setup
1. In Tailscale (login.tailscale.com, Settings, Keys) generate an auth key. Enable Reusable and Ephemeral.
2. Repo Settings, Secrets and variables, Actions, add two secrets:
   - `TAILSCALE_AUTH_KEY`: the key from step 1
   - `RDP_PASSWORD`: a strong password (Windows complexity rules apply)
3. Install Tailscale and the Windows App on the phone, sign in to the same tailnet.

## Each session
1. Actions tab, Cloud PC, Run workflow (default 330 minutes).
2. When the "Verify RDP is reachable" step is green, open the run's log for the Tailscale IP (or find `cloudpc` in the Tailscale app).
3. Turn Tailscale on, then in the Windows App add a PC: address = that IP, user = `cloudpc` (or the username you chose), password = your `RDP_PASSWORD`.

The runner is wiped when the run ends (maximum 6 hours per job); start a new run to get a fresh PC.

## Roblox Studio
The run puts `RobloxStudioInstaller.exe` on the cloud PC's desktop. Double-click it inside the RDP session and sign in. The download URL is set in `STUDIO_INSTALLER_URL` in the workflow; if that step fails, the session still starts and you can download Studio from inside the PC. The runner has no GPU, so Studio uses software rendering.

## Blender and Claude Desktop
The run installs Blender (Chocolatey package `blender`) and tries to install Claude Desktop (the signed Windows installer from claude.ai) after RDP is already reachable, so connect when "Verify RDP is reachable" is green and give the installs a few minutes. Both steps are non-fatal; check their step logs if an app is missing. The runner has no GPU, so Blender renders on the CPU.

## Roblox Player and Blender OpenGL
The run downloads `RobloxPlayerInstaller.exe` to the desktop and attempts a silent install (URL in `ROBLOX_PLAYER_URL`). The runner has no GPU, so after Blender is installed the run copies Mesa's software OpenGL `opengl32.dll` (mmozeiko/build-mesa, llvmpipe x64, URL in `MESA_LLVMPIPE_URL`) next to `blender.exe`. That is what clears Blender's "OpenGL 4.3 or higher is required" error; Blender is slow on it. Both steps are non-fatal.

## Roblox Diagnose shortcut
If Roblox Player will not open a game, double-click `Roblox Diagnose` on the desktop. It starts the Player directly, then writes GPU, OS, exit code, recent Windows application errors and the newest Roblox logs to `roblox-diagnose.txt` on your desktop and opens it. Send that file's contents instead of typing commands over RDP.

## Claude Desktop fallback
The run downloads the Windows installer (`CLAUDE_SETUP_URL` in the workflow), checks that it is a signed executable, and leaves it on the desktop as `Claude Setup`. If Claude is not installed after you sign in, double-click `Claude Setup`. The `Download Claude Desktop` shortcut to claude.com/download is still there as a last resort.

## Saved state and auto-continue
- Every 30 minutes, and again when the session ends or is cancelled, the run saves the cloud PC user's `Desktop`, `Documents`, Roblox settings/plugins/autosaves/login storage and Roblox registry settings into an encrypted 7z (`cloudpc-state.7z`) attached to the `cloudpc-state` release. The next run restores it right after the user is created.
- The password is the `STATE_PASSWORD` repository secret. Keep it private: the archive can contain your Roblox session.
- Roblox login restoring is not guaranteed. Windows encrypts the stored session per user and per machine, so you may be asked to sign in again; projects and settings are restored regardless.
- Run workflow with `auto_continue` ticked to keep going past the 6-hour limit: about 20 minutes before the PC ends it saves, then starts a new run that restores that state. The new PC shows up in Tailscale as `cloudpc-<run number>` after about 10-15 minutes (the run page also lists its IP). Save your work in Studio and move to the new PC; changes made on the old PC after the handover are not saved. Cancel every running Cloud PC run to stop the chain.
- If you start a new run by hand while an old one is running, the old one is cancelled; wait for its "Save state" step to finish first so the newest files are saved.

## Performance
A best-effort step sets the High performance power plan, turns off Defender real-time scanning, and gives new profiles "best performance" visual effects and no wallpaper. These are unverified on the runner. The runner has no GPU, so Roblox Studio renders on the CPU; lowering Studio's own quality settings helps most, and Roblox settings are kept by the saved state.

## Options
- `performance_tweaks` (default on): turn it off to skip the performance tweaks step.
- `previous_run` is internal; leave it empty.
