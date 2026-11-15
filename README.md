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
