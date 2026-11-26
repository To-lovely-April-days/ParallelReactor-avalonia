#!/bin/bash
# 在 Ubuntu 20.04 arm64 容器里编出板子用的程序，产物在 build-arm64/。
# 需要先装 Docker Desktop（或 colima），第一次会自动建镜像。
set -euo pipefail
cd "$(dirname "$0")/.."
if ! docker image inspect reactor-build:focal >/dev/null 2>&1; then
  docker build --platform linux/arm64 -t reactor-build:focal docker
fi
docker run --rm --platform linux/arm64 -v "$PWD":/src -w /src reactor-build:focal bash -c '
  cmake -S . -B build-arm64 -DCMAKE_BUILD_TYPE=Release -DREACTOR_HMI_BACKEND=SDL -DREACTOR_FULLSCREEN=ON -DREACTOR_USE_FREETYPE=ON &&
  cmake --build build-arm64 -j"$(nproc)" &&
  (cd build-arm64 && ctest --output-on-failure)'
echo "产物：build-arm64/reactor-hmi  build-arm64/rtu-probe  build-arm64/reactord"
