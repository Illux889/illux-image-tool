#!/usr/bin/env bash
# Builds jpegtran (libjpeg-turbo 3.2.0) for macOS on Apple silicon into third_party/libjpeg-turbo/.
#
# Downloads the official source tarball from the libjpeg-turbo GitHub release, verifies a pinned SHA-256, builds a
# statically linked arm64 jpegtran with CMake (-DENABLE_SHARED=OFF) and copies jpegtran, LICENSE.md and README.ijg
# into third_party/libjpeg-turbo/. Idempotent: does nothing if all three are already present. Fails hard on a hash
# mismatch. Needs cmake and the Xcode command-line tools (both are on GitHub's macOS runners).
set -euo pipefail

VERSION='3.2.0'
URL="https://github.com/libjpeg-turbo/libjpeg-turbo/releases/download/${VERSION}/libjpeg-turbo-${VERSION}.tar.gz"
# Computed from the downloaded 2,537,858-byte asset; matches the digest GitHub lists for the release asset.
SHA256='6f30092cef9fb839779646608f4ee14ae3cbac989c47fa05e841b0841f09878e'

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TARGET="${REPO_ROOT}/third_party/libjpeg-turbo"

if [[ -f "${TARGET}/jpegtran" && -f "${TARGET}/LICENSE.md" && -f "${TARGET}/README.ijg" ]]; then
    echo "jpegtran already present in ${TARGET}"
    exit 0
fi

if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "fetch-jpegtran.sh builds the macOS jpegtran; on Windows run build/fetch-jpegtran.ps1." >&2
    exit 1
fi
command -v cmake >/dev/null || { echo "cmake not found (brew install cmake)." >&2; exit 1; }

WORK="$(mktemp -d "${TMPDIR:-/tmp}/illux-jpegtran.XXXXXXXX")"
trap 'rm -rf "${WORK}"' EXIT

ARCHIVE="${WORK}/libjpeg-turbo-${VERSION}.tar.gz"
echo "Downloading ${URL}"
curl --fail --silent --show-error --location --proto '=https' --tlsv1.2 --output "${ARCHIVE}" "${URL}"

ACTUAL="$(shasum -a 256 "${ARCHIVE}" | awk '{print $1}')"
if [[ "${ACTUAL}" != "${SHA256}" ]]; then
    echo "SHA-256 mismatch for ${URL}" >&2
    echo "  expected ${SHA256}" >&2
    echo "  actual   ${ACTUAL}" >&2
    exit 1
fi

tar -xzf "${ARCHIVE}" -C "${WORK}"
SOURCE="${WORK}/libjpeg-turbo-${VERSION}"

# Static jpegtran only: no shared libraries, no TurboJPEG API; arm64, macOS 15 or later.
cmake -S "${SOURCE}" -B "${WORK}/build" \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_OSX_ARCHITECTURES=arm64 \
    -DCMAKE_OSX_DEPLOYMENT_TARGET=15.0 \
    -DENABLE_SHARED=OFF \
    -DENABLE_STATIC=ON \
    -DWITH_TURBOJPEG=OFF
cmake --build "${WORK}/build" --target jpegtran-static --parallel "$(sysctl -n hw.ncpu)"

BUILT="${WORK}/build/jpegtran-static"
[[ -x "${BUILT}" ]] || { echo "Build did not produce ${BUILT}" >&2; exit 1; }
ARCHS="$(lipo -archs "${BUILT}")"
if [[ "${ARCHS}" != "arm64" ]]; then
    echo "Expected an arm64-only jpegtran, got: ${ARCHS}" >&2
    exit 1
fi
if otool -L "${BUILT}" | tail -n +2 | grep -qi 'jpeg'; then
    echo "jpegtran is linked against a shared libjpeg; expected a static build:" >&2
    otool -L "${BUILT}" >&2
    exit 1
fi

mkdir -p "${TARGET}"
install -m 0755 "${BUILT}" "${TARGET}/jpegtran"
install -m 0644 "${SOURCE}/LICENSE.md" "${TARGET}/LICENSE.md"
install -m 0644 "${SOURCE}/README.ijg" "${TARGET}/README.ijg"
"${TARGET}/jpegtran" -version 2>&1 | head -n 1 || true
echo "jpegtran ${VERSION} built into ${TARGET}"
