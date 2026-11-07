#!/usr/bin/env bash
# Install the same Supabase CLI version used by .github/workflows/ci.yml.
# Codespaces only; does not start Docker services or access Supabase credentials.
set -euo pipefail

VERSION='2.122.0'
INSTALL_DIR="${HOME}/.local/bin"
BIN="${INSTALL_DIR}/supabase"

if [[ -x "$BIN" ]] && [[ "$("$BIN" --version)" == "$VERSION" ]]; then
  printf 'Supabase CLI %s is already installed.\n' "$VERSION"
  exit 0
fi

case "$(uname -s)/$(uname -m)" in
  Linux/x86_64) TARGET='linux_amd64' ;;
  Linux/aarch64|Linux/arm64) TARGET='linux_arm64' ;;
  *) printf 'Unsupported platform: %s/%s\n' "$(uname -s)" "$(uname -m)" >&2; exit 1 ;;
esac

for cmd in curl tar sha256sum install awk; do
  if ! command -v "$cmd" >/dev/null 2>&1; then
    printf 'Required command not found: %s\n' "$cmd" >&2
    exit 1
  fi
done

ASSET="supabase_${VERSION}_${TARGET}.tar.gz"
BASE_URL="https://github.com/supabase/cli/releases/download/v${VERSION}"
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

printf 'Installing Supabase CLI %s (%s)...\n' "$VERSION" "$TARGET"
curl --fail --location --silent --show-error --retry 3 \
  "${BASE_URL}/${ASSET}" -o "${TMP_DIR}/${ASSET}"
curl --fail --location --silent --show-error --retry 3 \
  "${BASE_URL}/checksums.txt" -o "${TMP_DIR}/checksums.txt"

EXPECTED="$(awk -v asset="$ASSET" '$2 == asset || $2 == "*" asset { print $1; exit }' "${TMP_DIR}/checksums.txt")"
if [[ ! "$EXPECTED" =~ ^[0-9a-fA-F]{64}$ ]]; then
  printf 'No valid SHA-256 checksum found for %s.\n' "$ASSET" >&2
  exit 1
fi
printf '%s  %s\n' "$EXPECTED" "${TMP_DIR}/${ASSET}" | sha256sum --check --status || {
  printf 'Supabase CLI checksum verification failed.\n' >&2
  exit 1
}

tar -xzf "${TMP_DIR}/${ASSET}" -C "$TMP_DIR" supabase
mkdir -p "$INSTALL_DIR"
install -m 0755 "${TMP_DIR}/supabase" "$BIN"
if [[ "$("$BIN" --version)" != "$VERSION" ]]; then
  printf 'Installed Supabase CLI version did not match %s.\n' "$VERSION" >&2
  exit 1
fi

# Interactive terminals get the CLI on PATH; devcontainer.json also sets remoteEnv.PATH.
PATH_LINE='export PATH="$HOME/.local/bin:$PATH"'
if ! grep -Fqx "$PATH_LINE" "$HOME/.bashrc" 2>/dev/null; then
  printf '\n%s\n' "$PATH_LINE" >> "$HOME/.bashrc"
fi
printf 'Supabase CLI %s installed at %s.\n' "$VERSION" "$BIN"
