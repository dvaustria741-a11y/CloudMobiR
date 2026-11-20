#!/data/data/com.termux/files/usr/bin/bash
# CloudMobiR Android runner setup for Termux (Adreno/Turnip)
set -e

RUNNER_VERSION="2.321.0"
RUNNER_DIR="$HOME/actions-runner"

echo "=== CloudMobiR Android runner setup ==="

# 1. Base packages
echo "[1/7] Installing base packages..."
pkg update -y
pkg install -y git wget curl python tar proot-distro tigervnc xfce4 xfce4-terminal xfce4-goodies dbus openssl

# 2. Box64
echo "[2/7] Installing Box64..."
if ! command -v box64 >/dev/null 2>&1; then
  if ! pkg install -y box64; then
    echo "box64 package not available; trying upstream Termux installer..."
    curl -fsSL https://raw.githubusercontent.com/ptitSeb/box64/main/termux-install.sh | bash
  fi
fi
box64 --version 2>/dev/null || echo "Box64 version check unavailable; continuing."

# 3. Wine (do not download the old, incompatible Wine-GE archive)
echo "[3/7] Installing Wine..."
WINE_DIR="$HOME/wine-ge"
if [ -d "$WINE_DIR" ] && [ ! -x "$WINE_DIR/bin/wine" ]; then
  echo "Removing incomplete Wine-GE directory from the previous failed attempt..."
  rm -rf "$WINE_DIR"
fi
if command -v wine >/dev/null 2>&1; then
  WINE_AVAILABLE=1
elif pkg install -y wine && command -v wine >/dev/null 2>&1; then
  WINE_AVAILABLE=1
else
  WINE_AVAILABLE=0
  echo "WARNING: Termux Wine package unavailable. Skipping Wine/DXVK."
fi
if [ "$WINE_AVAILABLE" -eq 1 ]; then
  wine --version || true
fi

# 4. DXVK is optional; only attempt when Wine exists
echo "[4/7] Installing DXVK..."
DXVK_DIR="$HOME/dxvk"
if [ "$WINE_AVAILABLE" -eq 1 ] && [ ! -f "$DXVK_DIR/setup_dxvk.sh" ]; then
  DXVK_URL="https://github.com/doitsujin/dxvk/releases/download/v2.4/dxvk-2.4.tar.gz"
  if wget -q --show-progress -O /tmp/dxvk.tar.gz "$DXVK_URL"; then
    mkdir -p "$DXVK_DIR"
    if tar -xf /tmp/dxvk.tar.gz -C "$DXVK_DIR" --strip-components=1; then
      rm -f /tmp/dxvk.tar.gz
      WINEPREFIX="$HOME/.wine" bash "$DXVK_DIR/setup_dxvk.sh" install --symlink 2>/dev/null || true
      echo "DXVK files extracted."
    else
      echo "WARNING: DXVK archive extraction failed; skipping DXVK."
      rm -f /tmp/dxvk.tar.gz
    fi
  else
    echo "WARNING: DXVK download failed; skipping DXVK."
    rm -f /tmp/dxvk.tar.gz
  fi
elif [ "$WINE_AVAILABLE" -eq 0 ]; then
  echo "Wine not available; DXVK skipped."
fi

# 5. GPU environment variables
echo "[5/7] Setting GPU environment variables..."
if ! grep -q "CloudMobiR GPU tweaks" "$HOME/.bashrc" 2>/dev/null; then
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
    >> "$HOME/.bashrc"
fi
source "$HOME/.bashrc" || true

# 6. GitHub Actions self-hosted runner
echo "[6/7] Installing GitHub Actions runner..."
mkdir -p "$RUNNER_DIR"
cd "$RUNNER_DIR"
if [ ! -f "./config.sh" ]; then
  case "$(uname -m)" in
    aarch64|arm64) RUNNER_ARCH="arm64" ;;
    *) RUNNER_ARCH="x64" ;;
  esac
  RUNNER_URL="https://github.com/actions/runner/releases/download/v${RUNNER_VERSION}/actions-runner-linux-${RUNNER_ARCH}-${RUNNER_VERSION}.tar.gz"
  echo "Downloading runner $RUNNER_VERSION ($RUNNER_ARCH)..."
  wget -q --show-progress -O runner.tar.gz "$RUNNER_URL"
  tar -xf runner.tar.gz
  rm -f runner.tar.gz

  echo
  echo "Repository Settings -> Actions -> Runners -> New self-hosted runner"
  echo "Select Linux / ARM64, then copy the temporary registration token."
  read -rp "Repo URL: " REPO_URL
  read -rsp "Runner token: " RUNNER_TOKEN
  echo
  ./config.sh --url "$REPO_URL" --token "$RUNNER_TOKEN" --labels "self-hosted,linux,android" --name "android-adreno-$(hostname)" --unattended --replace
fi

# 7. Runner launcher
echo "[7/7] Creating runner startup script..."
printf '%s\n' \
  '#!/data/data/com.termux/files/usr/bin/bash' \
  'source "$HOME/.bashrc" 2>/dev/null || true' \
  'cd "$HOME/actions-runner"' \
  './run.sh' \
  > "$HOME/start-runner.sh"
chmod +x "$HOME/start-runner.sh"

echo
echo "=== Setup complete ==="
echo "Start runner: ~/start-runner.sh"
echo "Then trigger linux-cloud-pc.yml from GitHub Actions."
echo "Keep Termux open while the runner is active."
