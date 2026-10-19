#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
IMAGE="${1:-opcontrol-parental-web}"
DESCRIBE="$(git describe --tags --always --dirty 2>/dev/null || echo unknown)"
BASE="$(git describe --tags --abbrev=0 2>/dev/null | sed 's/^v//' || true)"
[ -n "${BASE:-}" ] || BASE="$(grep -oP '(?<=<Version>)[^<]+' Directory.Build.props)"
COMMIT="$(git rev-parse --short HEAD 2>/dev/null || echo unknown)"
BUILD_DATE="$(date -u +%Y-%m-%d)"
TAG="${BASE}-$(date -u +%Y%m%d)"
echo "Image: ${IMAGE}:${TAG}"
echo "Informational: ${DESCRIBE} (commit ${COMMIT}, build ${BUILD_DATE})"
docker build \
  --build-arg VERSION="$BASE" \
  --build-arg INFORMATIONAL="$DESCRIBE" \
  --build-arg COMMIT="$COMMIT" \
  --build-arg BUILD_DATE="$BUILD_DATE" \
  -t "${IMAGE}:${TAG}" .
echo "Built ${IMAGE}:${TAG}"
