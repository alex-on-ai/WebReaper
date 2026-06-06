#!/bin/sh
# install.sh: WebReaper CLI installer (ADR-0070).
#
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/alex-on-ai/WebReaper/master/scripts/install.sh | sh
#   # or with flags:
#   curl -fsSL https://raw.githubusercontent.com/alex-on-ai/WebReaper/master/scripts/install.sh | sh -s -- --force
#
# Environment variables:
#   WEBREAPER_VERSION        : pin to a specific tag (e.g. v10.0.0). Default: latest release.
#   PREFIX                   : install directory. Default: /usr/local/bin → ~/.local/bin fallback.
#   WEBREAPER_FORCE=1        : overwrite existing install (same as --force).
#   WEBREAPER_INSTALL_VERBOSE=1: verbose output (set -x + curl -v).
#   NO_COLOR=1               : disable ANSI colour.
#
# Flags:
#   --force     : overwrite without confirmation, even if same version is installed
#   --upgrade   : overwrite only if installing a strictly newer version
#   --help, -h  : print this usage block
#
# Exit codes:
#   0: success (installed, upgraded, or already-current)
#   1: generic / unexpected error
#   2: unsupported OS / architecture
#   3: missing required tool (curl/wget, sha256sum/shasum, tar, unzip)
#   4: network failure after retries
#   5: SHA256 verification failed
#   6: conflicting webreaper found elsewhere on PATH (rerun with --force)
#   7: same/older version already at target (rerun with --force or --upgrade)
#   8: install location not writable

set -eu

REPO="alex-on-ai/WebReaper"
DEFAULT_PREFIX="/usr/local/bin"
FALLBACK_PREFIX="${HOME}/.local/bin"

VERSION="${WEBREAPER_VERSION:-}"
FORCE="${WEBREAPER_FORCE:-}"
UPGRADE=""
PREFIX_INPUT="${PREFIX:-}"
CURL_VERBOSE=""
if [ "${WEBREAPER_INSTALL_VERBOSE:-}" = "1" ]; then set -x; CURL_VERBOSE="-v"; fi

# ----- colour (tty-only) ---------------------------------------------------

if [ -t 1 ] && [ -z "${NO_COLOR:-}" ]; then
  RED=$(printf '\033[0;31m'); GREEN=$(printf '\033[0;32m')
  YELLOW=$(printf '\033[1;33m'); BLUE=$(printf '\033[0;34m')
  BOLD=$(printf '\033[1m'); RESET=$(printf '\033[0m')
else
  RED=""; GREEN=""; YELLOW=""; BLUE=""; BOLD=""; RESET=""
fi

err()  { printf "%s✗%s %s\n" "$RED"   "$RESET" "$*" >&2; }
warn() { printf "%s⚠%s %s\n" "$YELLOW" "$RESET" "$*" >&2; }
info() { printf "%s→%s %s\n" "$BLUE"   "$RESET" "$*"; }
ok()   { printf "%s✓%s %s\n" "$GREEN"  "$RESET" "$*"; }

usage() {
  sed -n '2,/^$/p' <<'USAGE_END' | sed 's/^# \{0,1\}//'
# install.sh: WebReaper CLI installer
#
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/alex-on-ai/WebReaper/master/scripts/install.sh | sh
#
# Flags: --force, --upgrade, --help.
# See script header for env vars and exit codes.
USAGE_END
}

# ----- args -----------------------------------------------------------------

while [ $# -gt 0 ]; do
  case "$1" in
    --force)        FORCE=1 ;;
    --upgrade)      UPGRADE=1 ;;
    --help|-h)      usage; exit 0 ;;
    *)              err "unknown argument: $1"; usage; exit 1 ;;
  esac
  shift
done

# ----- preflight ------------------------------------------------------------

have() { command -v "$1" >/dev/null 2>&1; }

if   have curl; then DL="curl"
elif have wget; then DL="wget"
else err "need 'curl' or 'wget' on PATH"; exit 3
fi

if   have sha256sum; then SHA_CMD="sha256sum"
elif have shasum;    then SHA_CMD="shasum -a 256"
else err "need 'sha256sum' or 'shasum'"; exit 3
fi

have tar   || { err "need 'tar' on PATH"; exit 3; }
have unzip || { err "need 'unzip' on PATH"; exit 3; }

# ----- OS + arch detection --------------------------------------------------

UNAME_S=$(uname -s); UNAME_M=$(uname -m)
case "${UNAME_S}-${UNAME_M}" in
  Linux-x86_64)   RID="linux-x64";   ARCHIVE_EXT="tar.gz" ;;
  Linux-aarch64)  RID="linux-arm64"; ARCHIVE_EXT="tar.gz" ;;
  Darwin-x86_64)  RID="osx-x64";     ARCHIVE_EXT="zip" ;;
  Darwin-arm64)   RID="osx-arm64";   ARCHIVE_EXT="zip" ;;
  *)
    err "unsupported platform: ${UNAME_S} ${UNAME_M}"
    err "supported: Linux x86_64/aarch64, Darwin x86_64/arm64"
    err "Windows: download from https://github.com/${REPO}/releases/latest"
    exit 2 ;;
esac

# ----- download helpers (with retry + exponential backoff) ------------------

dl_to_file() {
  _url="$1"; _out="$2"
  if [ "$DL" = "curl" ]; then curl -fsSL ${CURL_VERBOSE} "$_url" -o "$_out"
  else                        wget -q -O "$_out" "$_url"
  fi
}

dl_to_stdout() {
  if [ "$DL" = "curl" ]; then curl -fsSL ${CURL_VERBOSE} "$1"
  else                        wget -q -O - "$1"
  fi
}

retry_download() {
  _url="$1"; _out="$2"; _what="$3"
  _delays="2 5 10"
  _attempt=1
  for _d in $_delays X; do
    if dl_to_file "$_url" "$_out" 2>/dev/null; then return 0; fi
    [ "$_d" = "X" ] && break
    warn "${_what} download failed (attempt ${_attempt}); retrying in ${_d}s..."
    sleep "$_d"
    _attempt=$((_attempt + 1))
  done
  err "could not download ${_what} from ${_url} after 3 attempts"
  exit 4
}

# ----- resolve version ------------------------------------------------------

if [ -z "$VERSION" ]; then
  info "resolving latest release..."
  TAG=$(dl_to_stdout "https://api.github.com/repos/${REPO}/releases/latest" 2>/dev/null \
        | grep '"tag_name"' \
        | head -1 \
        | sed -E 's/.*"tag_name"[^"]*"([^"]+)".*/\1/' || true)
  if [ -z "${TAG:-}" ]; then
    err "could not resolve latest release from api.github.com (rate-limited? offline?)"
    err "pin a version explicitly: WEBREAPER_VERSION=v10.0.0"
    exit 4
  fi
  VERSION="$TAG"
fi

info "installing ${BOLD}webreaper ${VERSION}${RESET} for ${RID}"

# ----- choose install location ---------------------------------------------

if [ -z "$PREFIX_INPUT" ]; then
  if [ -w "$DEFAULT_PREFIX" ]; then
    PREFIX_DIR="$DEFAULT_PREFIX"
  else
    PREFIX_DIR="$FALLBACK_PREFIX"
    info "no write access to ${DEFAULT_PREFIX}; falling back to ${PREFIX_DIR}"
    mkdir -p "$PREFIX_DIR"
  fi
else
  PREFIX_DIR="$PREFIX_INPUT"
  mkdir -p "$PREFIX_DIR" 2>/dev/null || true
  [ -w "$PREFIX_DIR" ] || { err "PREFIX=${PREFIX_DIR} is not writable"; exit 8; }
fi

TARGET="${PREFIX_DIR}/webreaper"

# ----- conflict + idempotency check -----------------------------------------

if EXISTING=$(command -v webreaper 2>/dev/null); then
  if [ "$EXISTING" != "$TARGET" ]; then
    warn "another webreaper found at ${EXISTING} (installing to ${TARGET})"
    if [ -z "$FORCE" ]; then
      err "refusing to install: multiple webreaper binaries would be on PATH"
      err "rerun with --force (or WEBREAPER_FORCE=1) to override"
      exit 6
    fi
  fi
fi

if [ -x "$TARGET" ]; then
  # WebReaper.Cli's `version` command prints the
  # AssemblyInformationalVersion (see WebReaper.Cli/Commands/VersionCommand.cs);
  # under ADR-0024 tag-derived versioning this formats as `<semver>+<sha>`
  # (e.g. `10.0.0+abc123`). Strip the `+<metadata>` suffix so the
  # idempotency/upgrade comparison works against the bare semver.
  EXISTING_VER=$("$TARGET" version 2>/dev/null | head -1 | awk '{print $NF}' || true)
  if [ -n "${EXISTING_VER:-}" ]; then
    WANT_VER="${VERSION#v}"
    WANT_VER="${WANT_VER%%+*}"
    EXISTING_VER_NORM="${EXISTING_VER#v}"
    EXISTING_VER_NORM="${EXISTING_VER_NORM%%+*}"
    if [ "$EXISTING_VER_NORM" = "$WANT_VER" ]; then
      if [ -n "$FORCE" ]; then
        info "${VERSION} already installed at ${TARGET}; --force → reinstalling"
      else
        ok "webreaper ${VERSION} is already installed at ${TARGET}"
        info "rerun with --force to reinstall"
        exit 0
      fi
    elif [ -n "$UPGRADE" ]; then
      if printf '%s\n%s\n' "$EXISTING_VER_NORM" "$WANT_VER" | sort -V >/dev/null 2>&1; then
        NEWEST=$(printf '%s\n%s\n' "$EXISTING_VER_NORM" "$WANT_VER" | sort -V | tail -1)
        if [ "$NEWEST" = "$EXISTING_VER_NORM" ]; then
          ok "${EXISTING_VER} already installed; ${VERSION} is not strictly newer (--upgrade is no-op)"
          exit 0
        fi
      fi
      info "upgrading ${EXISTING_VER} → ${VERSION}"
    elif [ -z "$FORCE" ]; then
      warn "webreaper ${EXISTING_VER} is at ${TARGET}; installing ${VERSION} would overwrite"
      err "refusing to overwrite: pass --force or --upgrade"
      exit 7
    fi
  fi
fi

# ----- download + verify + extract + install -------------------------------

TMP=$(mktemp -d -t webreaper-install.XXXXXX)
# shellcheck disable=SC2064
trap "rm -rf '$TMP'" EXIT INT TERM

ASSET_NAME="webreaper-${VERSION}-${RID}.${ARCHIVE_EXT}"
ASSET_URL="https://github.com/${REPO}/releases/download/${VERSION}/${ASSET_NAME}"
SUMS_URL="https://github.com/${REPO}/releases/download/${VERSION}/SHA256SUMS"

info "downloading ${ASSET_NAME}..."
retry_download "$ASSET_URL" "${TMP}/${ASSET_NAME}" "binary archive"

info "downloading SHA256SUMS..."
retry_download "$SUMS_URL" "${TMP}/SHA256SUMS" "checksum manifest"

EXPECTED=$(awk -v f="${ASSET_NAME}" '$2 == f { print $1; exit }' "${TMP}/SHA256SUMS" || true)
[ -n "${EXPECTED:-}" ] || { err "SHA256SUMS has no entry for ${ASSET_NAME}"; exit 5; }
ACTUAL=$(cd "$TMP" && $SHA_CMD "$ASSET_NAME" | awk '{print $1}')
if [ "$ACTUAL" != "$EXPECTED" ]; then
  err "SHA256 mismatch for ${ASSET_NAME}"
  err "  expected: ${EXPECTED}"
  err "  actual:   ${ACTUAL}"
  exit 5
fi
ok "SHA256 verified"

info "extracting..."
case "$ARCHIVE_EXT" in
  tar.gz) tar -xzf "${TMP}/${ASSET_NAME}" -C "$TMP" ;;
  zip)    unzip -q -o "${TMP}/${ASSET_NAME}" -d "$TMP" ;;
esac

EXTRACTED_DIR="${TMP}/webreaper-${VERSION}-${RID}"
[ -x "${EXTRACTED_DIR}/webreaper" ] || {
  err "expected binary at ${EXTRACTED_DIR}/webreaper not found in archive"
  exit 1
}

info "installing to ${TARGET}..."
# Atomic install: stage next to the target then `mv` (atomic on the same
# filesystem). cp+chmod in-place would let a concurrent reader observe the
# binary mid-write or post-write-but-pre-chmod. Staging next to the target
# (rather than from $TMP) also sidesteps cross-filesystem mv non-atomicity
# when /usr/local/bin lives on a different volume than mktemp's default.
TMP_TARGET="${TARGET}.new.$$"
cp -f "${EXTRACTED_DIR}/webreaper" "$TMP_TARGET"
chmod 0755 "$TMP_TARGET"
mv -f "$TMP_TARGET" "$TARGET"

# ----- post-install --------------------------------------------------------

INSTALLED_VER=$("$TARGET" version 2>/dev/null | head -1 || echo "$VERSION")
ok "installed: ${BOLD}${INSTALLED_VER}${RESET}"
ok "location:  ${TARGET}"

case ":${PATH}:" in
  *":${PREFIX_DIR}:"*) ;;
  *)
    warn "${PREFIX_DIR} is not on your \$PATH"
    warn "add to your shell rc:"
    printf "    %sexport PATH=\"%s:\$PATH\"%s\n" "$BOLD" "$PREFIX_DIR" "$RESET" >&2
    ;;
esac

printf '\n%sNext:%s        webreaper init && webreaper scrape https://example.com\n' "$BOLD" "$RESET"
printf '%sUninstall:%s   rm %s\n\n'                                                  "$BOLD" "$RESET" "$TARGET"
