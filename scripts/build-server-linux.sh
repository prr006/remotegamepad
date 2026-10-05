#!/usr/bin/env bash
#
# RemoteGamepad — Linux server build
#
# Restore + Release build of the existing .NET server. Never publishes and
# never targets win-x64: the Linux server is a framework-dependent build that
# runs with "dotnet run -c Release" / "dotnet bin/Release/.../*.dll".
#
# Usage: ./scripts/build-server-linux.sh [--allow-root] [extra dotnet args]
set -euo pipefail

RG_SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source-path=SCRIPTDIR source=lib/remote-gamepad-linux.sh
. "${RG_SCRIPT_DIR}/lib/remote-gamepad-linux.sh"

ALLOW_ROOT=0
EXTRA_ARGS=()
while [ $# -gt 0 ]; do
    case "$1" in
        --allow-root) ALLOW_ROOT=1 ;;
        -h|--help)
            cat <<EOF
${RG_PROJECT_NAME} Linux server build (restore + Release build only)

Usage: $0 [--allow-root] [additional dotnet build arguments]

  --allow-root   Permit building as root (not recommended: it leaves
                 root-owned obj/ and bin/ directories behind).

The build output lands in:
  Server/RemoteGamepadServer/bin/Release/net${RG_REQUIRED_DOTNET_MAJOR}.0/
EOF
            exit 0 ;;
        *) EXTRA_ARGS+=("$1") ;;
    esac
    shift
done

rg_require_linux

if rg_is_root && [ "$ALLOW_ROOT" = "0" ]; then
    rg_err "Refusing to build as root: root-owned obj/ and bin/ directories break the unprivileged server run."
    rg_info "Run this script as your normal user (no sudo), or pass --allow-root if you really mean it."
    exit 1
fi

if ! rg_have dotnet; then
    rg_err ".NET SDK not found."
    rg_info "Install .NET ${RG_REQUIRED_DOTNET_MAJOR} SDK (scripts/setup-linux.sh can do it for you) and retry."
    exit 1
fi

SDK_VERSION="$(rg_dotnet_sdk_version 2>/dev/null || true)"
if [ -z "$SDK_VERSION" ]; then
    rg_err "No .NET SDK is installed (only a runtime was found): $(dotnet --list-runtimes 2>/dev/null | head -1)"
    rg_info "Install the .NET ${RG_REQUIRED_DOTNET_MAJOR} *SDK*, not just the runtime."
    exit 1
fi
if ! rg_dotnet_sdk_ok; then
    rg_err "Found .NET SDK ${SDK_VERSION}, but the server targets net${RG_REQUIRED_DOTNET_MAJOR}.0."
    rg_info "Install the .NET ${RG_REQUIRED_DOTNET_MAJOR} SDK and retry (see docs/Linux.md)."
    exit 1
fi

cd -- "$RG_SERVER_DIR"
rg_section "Restore"
rg_info "Project : ${RG_SERVER_DIR}/RemoteGamepadServer.csproj"
rg_info "SDK     : ${SDK_VERSION}"
dotnet restore

rg_section "Build (Release)"
dotnet build -c Release --no-restore ${EXTRA_ARGS[@]+"${EXTRA_ARGS[@]}"}

rg_section "Result"
OUTPUT_DIR="${RG_SERVER_DIR}/bin/Release/net${RG_REQUIRED_DOTNET_MAJOR}.0"
if [ -d "$OUTPUT_DIR" ]; then
    rg_ok "Built: ${OUTPUT_DIR}"
else
    rg_ok "Build completed."
fi
rg_info "Test with: ./scripts/test-linux.sh"
rg_info "Run with : ./scripts/run-server-linux.sh"
