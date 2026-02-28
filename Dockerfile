# syntax=docker/dockerfile:1

FROM ghcr.io/linuxserver/baseimage-alpine:3.23

# set version label
ARG BUILD_DATE
ARG VERSION
LABEL build_version="version:- ${VERSION} Build-date:- ${BUILD_DATE}"
LABEL maintainer="darthShadow"

# environment settings
ARG PROWLARR_BRANCH="master"
ENV XDG_CONFIG_HOME="/config/xdg" \
  COMPlus_EnableDiagnostics=0 \
  TMPDIR=/run/prowlarr-temp

RUN \
  echo "**** install packages ****" && \
  apk add -U --upgrade --no-cache \
    icu-libs \
    jq \
    sqlite-libs \
    xmlstarlet && \
  mkdir -p /app/prowlarr/bin

# copy pre-built musl binaries (replaces curl download from LSIO original)
COPY docker-build/ /app/prowlarr/bin/

RUN \
  chmod a+x /app/prowlarr/bin/Prowlarr && \
  echo -e "UpdateMethod=docker\nBranch=${PROWLARR_BRANCH}\nPackageVersion=${VERSION}\nPackageAuthor=[darthShadow](https://github.com/darthShadow)" > /app/prowlarr/package_info && \
  echo "**** cleanup ****" && \
  rm -rf \
    /app/prowlarr/bin/Prowlarr.Update \
    /tmp/* \
    /var/tmp/*

# copy local files
COPY root/ /

# ports and volumes
EXPOSE 9696
VOLUME /config
