#!/data/data/com.termux/files/usr/bin/bash
# scripts/setup-android-runner.sh
# Run once in Termux to set up the GitHub Actions self-hosted runner
# with GPU (Adreno/Turnip) support for linux-cloud-pc.yml
#
# Usage:
#   chmod +x scripts/setup-android-runner.sh
#   ./scripts/setup-android-runner.sh
#
# After this script completes:
#   1. Go to your repo -> Settings -> Actions -> Runners -> New self-hosted runner
#   2. Select Linux / ARM64
#   3. Copy the token shown there and paste when prompted below
#   4. The runner registers itself with labels: self-hosted, linux, android

set -e

RUNNER_VERSION="2.321.0"   # Update to latest from github.com/actions/runner/releases
RUNNER_DIR="$HOME/actions-runner"

echo "=== CloudMobiR Android runner setup ==="

# ── 1. Base packages ─────────────────────────────────────────────────────────
echo "[1/7] Installing base packages..."
pkg update -y
pkg install -y \
  git wget curl python3 tar proot-distro \
  tigervnc xfce4 xfce4-terminal xfce4-goodies \
  dbus openssl

# ── 2. Box64 (x86_64 emulation for Wine) ─────────────────────────────────────
echo "[2/7] Installing Box64..."
if ! command -v box64 &>/dev/null; then
  pkg install -y box64 2>/dev/null || {
    echo "box64 not in pkg repo — adding ptitSeb's repo"
    curl -s https://raw.githubusercontent.com/ptitSeb/box64/main/termux-install.sh | bash
  }
fi
box64 --version 2>/dev/null || echo "box64 installed (version check failed — normal)"

# ── 3. Wine-GE (Turnip-patched for Adreno) ───────────────────────────────────
echo "[3/7] Installing Wine-GE..."
WINE_DIR="$HOME/wine-ge"
if [ ! -d "$WINE_DIR" ]; then
  # Termux x11 community builds Wine-GE with Turnip patches for Adreno
  pkg install -y wine 2>/dev/null || {
    echo "Pulling Wine-GE community build..."
    mkdir -p "$WINE_DIR"
    # Latest Wine-GE-custom ARM64 build from GloriousEggroll
    # Check https://github.com/GloriousEggroll/wine-ge-custom/releases for the latest
    WINE_URL="https://github.com/GloriousEggroll/wine-ge-custom/releases/download/GE-Proton9-26/wine-lutris-GE-Proton9-26-x86_64.tar.xz"
    wget -q --show-progress -O /tmp/wine-ge.tar.xz "$WINE_URL"
    tar -xf /tmp/wine-ge.tar.xz -C "$WINE_DIR" --strip-components=1
    rm /tmp/wine-ge.tar.xz
    echo "export PATH=\"$WINE_DIR/bin:\$PATH\"" >> ~/.bashrc
  }
fi
export PATH="$WINE_DIR/bin:$PATH"
command -v wine &>/dev/null && echo "Wine: $(wine --version)" || echo "wine not in PATH yet — source ~/.bashrc"

# ── 4. DXVK (Vulkan-based D3D9/10/11 → runs on Adreno via Turnip) ─────────────
echo "[4/7] Installing DXVK..."
DXVK_DIR="$HOME/dxvk"
if [ ! -d "$DXVK_DIR" ]; then
  DXVK_URL="https://github.com/doitsujin/dxvk/releases/download/v2.4/dxvk-2.4.tar.gz"
  wget -q --show-progress -O /tmp/dxvk.tar.gz "$DXVK_URL"
  mkdir -p "$DXVK_DIR"
  tar -xf /tmp/dxvk.tar.gz -C "$DXVK_DIR" --strip-components=1
  rm /tmp/dxvk.tar.gz
  # Install into Wine prefix
  if command -v wine &>/dev/null; then
    WINEPREFIX="$HOME/.wine" bash "$DXVK_DIR/setup_dxvk.sh" install --symlink 2>/dev/null || true
    echo "DXVK installed into Wine prefix"
  fi
fi

# ── 5. Turnip / Mesa env vars ─────────────────────────────────────────────────
echo "[5/7] Setting GPU environment variables..."
grep -q "CloudMobiR GPU tweaks" ~/.bashrc 2>/dev/null || {
  printf '%s\n' \
    '# CloudMobiR GPU tweaks — Turnip (open-source Qualcomm Vulkan)' \
    'export MESA_VK_VERSION_OVERRIDE=1.3' \
    'export MESA_GL_VERSION_OVERRIDE=4.3' \
    'export MESA_GLSL_VERSION_OVERRIDE=430' \
    'export VK_ICD_FILENAMES=/data/data/com.termux/files/usr/share/vulkan/icd.d/freedreno_icd.aarch64.json' \
    'export GALLIUM_DRIVER=freedreno' \
    'export WINEESYNC=1' \
    'export DXVK_ASYNC=1' \
    'export DXVK_STATE_CACHE=1' \
    'export DXVK_LOG_LEVEL=none' \
    'export BOX64_WINE="$HOME/wine-ge/bin/wine"' \
    >> ~/.bashrc
}
source ~/.bashrc || true

# ── 6. GitHub Actions self-hosted runner ──────────────────────────────────────
echo "[6/7] Installing GitHub Actions runner..."
mkdir -p "$RUNNER_DIR"
cd "$RUNNER_DIR"

if [ ! -f "./config.sh" ]; then
  ARCH=$(uname -m)
  case "$ARCH" in
    aarch64|arm64) RUNNER_ARCH="arm64" ;;
    *)             RUNNER_ARCH="x64" ;;
  esac
  RUNNER_URL="https://github.com/actions/runner/releases/download/v${RUNNER_VERSION}/actions-runner-linux-${RUNNER_ARCH}-${RUNNER_VERSION}.tar.gz"
  echo "Downloading runner $RUNNER_VERSION ($RUNNER_ARCH)..."
  wget -q --show-progress -O runner.tar.gz "$RUNNER_URL"
  tar -xf runner.tar.gz
  rm runner.tar.gz

  echo ""
  echo "======================================================="
  echo "  Go to your repo:"
  echo "  Settings -> Actions -> Runners -> New self-hosted runner"
  echo "  Select: Linux / ARM64"
  echo "  Copy the token from that page, then paste it below."
  echo "======================================================="
  read -rp "Repo URL (e.g. https://github.com/dvaustria741-a11y/CloudMobiR): " REPO_URL
  read -rp "Runner token: " RUNNER_TOKEN

  ./config.sh \
    --url "$REPO_URL" \
    --token "$RUNNER_TOKEN" \
    --labels "self-hosted,linux,android" \
    --name "android-adreno-$(hostname)" \
    --unattended \
    --replace
fi

# ── 7. Create a startup script to launch the runner ──────────────────────────
echo "[7/7] Creating runner startup script..."
printf '%s\n' \
  '#!/data/data/com.termux/files/usr/bin/bash' \
  'source ~/.bashrc' \
  'cd "$HOME/actions-runner"' \
  './run.sh' \
  > "$HOME/start-runner.sh"
chmod +x "$HOME/start-runner.sh"

echo ""
echo "======================================================="
echo "  Setup complete!"
echo "======================================================="
echo "  Start the runner with:"
echo "    ~/start-runner.sh"
echo ""
echo "  Then trigger linux-cloud-pc.yml from GitHub Actions."
echo "  Your Adreno GPU will be used automatically via Turnip."
echo "======================================================="
