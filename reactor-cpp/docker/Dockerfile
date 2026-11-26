# 和板子一样的编译环境：Ubuntu 20.04 arm64。在 Apple 芯片的 Mac 上是原生速度。
#   docker build --platform linux/arm64 -t reactor-build:focal docker
#   docker/build-arm64.sh
FROM ubuntu:20.04
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
        build-essential cmake pkg-config \
        libsdl2-dev libx11-dev libfreetype6-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
