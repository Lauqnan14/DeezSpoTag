# syntax=docker/dockerfile:1.7

ARG DOTNET_VERSION=10.0
ARG DEEZSPOTAG_BUILD_VERSION=dev

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

COPY . .

RUN dotnet restore DeezSpoTag.Web/DeezSpoTag.Web.csproj
RUN dotnet publish DeezSpoTag.Web/DeezSpoTag.Web.csproj -c Release -o /app/publish --no-restore \
    && mkdir -p /app/publish/Tools \
    && cp -a Tools/AppleMusicWrapper /app/publish/Tools/AppleMusicWrapper

FROM docker:cli AS docker-cli

FROM golang:1.26.4-bookworm AS apple-wrapper-build
WORKDIR /work
ARG TARGETARCH

COPY Tools/AppleMusicWrapper/runv2/go.mod Tools/AppleMusicWrapper/runv2/go.sum ./
RUN go mod download
COPY Tools/AppleMusicWrapper/runv2/*.go ./
RUN set -eux; \
    target_arch="${TARGETARCH:-amd64}"; \
    CGO_ENABLED=0 GOOS=linux GOARCH="${target_arch}" go build -trimpath -ldflags "-s -w" -o /out/apple-wrapper-runv2 .

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app
ARG DEEZSPOTAG_BUILD_VERSION=dev
ARG DEEZSPOTAG_FETCH_MODELS_DURING_BUILD=1
ARG TARGETARCH
ARG BENTO4_URL_X86_64=https://www.bok.net/Bento4/binaries/Bento4-SDK-1-6-0-641.x86_64-unknown-linux.zip
ARG BENTO4_SHA256=
ARG ESSENTIA_TF_PACKAGE=essentia-tensorflow==2.1b6.dev1389
LABEL org.opencontainers.image.source="https://github.com/Lauqnan14/DeezSpoTag" \
      org.opencontainers.image.title="deezspotag"

COPY scripts/mp4decrypt /usr/local/bin/mp4decrypt

RUN apt-get update -o Acquire::Retries=5 \
    && apt-get install -y --no-install-recommends \
       perl-base=5.38.2-3.2ubuntu0.3 \
       tar \
    && apt-get install -y --no-install-recommends \
       openssl \
       ca-certificates \
       python3 \
       python3-venv \
       python3-pip \
       curl \
       aria2 \
       ffmpeg \
       unzip \
    && if ! apt-get install -y --no-install-recommends gpac; then \
         os_id="$(. /etc/os-release && echo "${ID}")"; \
         codename="$(. /etc/os-release && echo "${VERSION_CODENAME}")"; \
         if [ "$os_id" = "debian" ]; then \
           install -m 0755 -d /etc/apt/keyrings; \
           curl --fail --show-error --silent --location \
             --retry 8 --retry-all-errors --retry-delay 2 --connect-timeout 10 --max-time 120 \
             https://dist.gpac.io/gpac/linux/gpg.asc \
             -o /etc/apt/keyrings/gpac.asc; \
           chmod a+r /etc/apt/keyrings/gpac.asc; \
           printf '%s\n' \
             "Types: deb" \
             "URIs: https://dist.gpac.io/gpac/linux/debian" \
             "Suites: ${codename}" \
             "Components: main" \
             "Signed-By: /etc/apt/keyrings/gpac.asc" \
             > /etc/apt/sources.list.d/gpac.sources; \
           apt-get update -o Acquire::Retries=5; \
           apt-get install -y --no-install-recommends gpac; \
         else \
           echo "gpac package unavailable for ${os_id}/${codename}" >&2; \
           exit 1; \
         fi; \
       fi \
    && mp4box_path="$(command -v MP4Box || true)" \
    && if [ -z "$mp4box_path" ]; then mp4box_path="$(command -v mp4box || true)"; fi \
    && if [ -z "$mp4box_path" ]; then echo "MP4Box not found after GPAC install." >&2; exit 1; fi \
    && install -m 0755 "$mp4box_path" /usr/local/bin/mp4box \
    && chmod 0755 /usr/local/bin/mp4decrypt \
    && if [ "${TARGETARCH:-amd64}" = "amd64" ]; then \
         if curl --fail --show-error --silent --location \
           --retry 6 --retry-all-errors --retry-delay 2 --connect-timeout 10 --max-time 180 \
           -o /tmp/bento4.zip "$BENTO4_URL_X86_64"; then \
           checksum_ok=1; \
           if [ -n "$BENTO4_SHA256" ] && ! echo "$BENTO4_SHA256  /tmp/bento4.zip" | sha256sum -c -; then \
             checksum_ok=0; \
             echo "Bento4 checksum validation failed; keeping bundled mp4decrypt compatibility wrapper."; \
           fi; \
           if [ "$checksum_ok" = "1" ]; then \
             mkdir -p /tmp/bento4; \
             unzip -q /tmp/bento4.zip -d /tmp/bento4; \
             mp4decrypt_path="$(find /tmp/bento4 -type f -name mp4decrypt -perm -111 | head -n 1)"; \
             if [ -n "$mp4decrypt_path" ]; then \
               install -m 0755 "$mp4decrypt_path" /usr/local/bin/mp4decrypt; \
             else \
               echo "Bento4 archive did not contain mp4decrypt; keeping bundled compatibility wrapper."; \
             fi; \
           fi; \
         else \
           echo "Bento4 download unavailable; keeping bundled mp4decrypt compatibility wrapper."; \
         fi; \
         rm -rf /tmp/bento4 /tmp/bento4.zip; \
       else \
         echo "Keeping bundled mp4decrypt compatibility wrapper for TARGETARCH=${TARGETARCH:-unknown}."; \
       fi \
    && command -v mp4decrypt >/dev/null \
    && printf '%s\n' \
      'openssl_conf = openssl_init' \
      '' \
      '.include /etc/ssl/openssl.cnf' \
      '' \
      '[openssl_init]' \
      'providers = provider_sect' \
      '' \
      '[provider_sect]' \
      'default = default_sect' \
      'legacy = legacy_sect' \
      '' \
      '[default_sect]' \
      'activate = 1' \
      '' \
      '[legacy_sect]' \
      'activate = 1' \
      > /etc/ssl/openssl-legacy.cnf \
    && rm -rf /var/lib/apt/lists/*

RUN set -eux; \
    python3 -m venv /opt/venv; \
    /opt/venv/bin/pip install --no-cache-dir --upgrade pip; \
    /opt/venv/bin/pip install --no-cache-dir "numpy>=1.25" pyyaml six; \
    /opt/venv/bin/pip install --no-cache-dir "${ESSENTIA_TF_PACKAGE}"; \
    /opt/venv/bin/python3 - <<'PY'
import essentia.standard as es

required = [
    "MonoLoader",
    "TensorflowPredictMusiCNN",
    "TensorflowPredict2D",
    "RhythmExtractor2013",
    "KeyExtractor",
    "Loudness",
    "DynamicComplexity",
    "Danceability",
    "Windowing",
    "Spectrum",
    "RMS",
    "Centroid",
    "FlatnessDB",
    "ZeroCrossingRate",
]
missing = [name for name in required if getattr(es, name, None) is None]
if missing:
    raise SystemExit(f"Essentia runtime missing required algorithms: {', '.join(missing)}")
PY

COPY --from=docker-cli /usr/local/bin/docker /usr/local/bin/docker

ENV OPENSSL_CONF=/etc/ssl/openssl-legacy.cnf \
    HOME=/data/home \
    XDG_CACHE_HOME=/data/.cache \
    PIP_CACHE_DIR=/data/.cache/pip \
    PIP_NO_CACHE_DIR=1 \
    DEEZSPOTAG_CONFIG_DIR=/data \
    DEEZSPOTAG_DATA_DIR=/data \
    DEEZSPOTAG_BUILD_VERSION=${DEEZSPOTAG_BUILD_VERSION} \
    DEEZSPOTAG_APPLE_MP4DECRYPT_PATH=/usr/local/bin/mp4decrypt \
    DEEZSPOTAG_APPLE_MP4BOX_PATH=/usr/local/bin/mp4box \
    APPLE_WRAPPER_RUNV2=/app/Tools/AppleMusicWrapper/runv2/apple-wrapper-runv2 \
    DEEZSPOTAG_SPOTIFY_USE_CONFIG_CREDS=1 \
    DOTNET_SYSTEM_NET_HTTP_USESOCKETSHTTPHANDLER=1 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 \
    ASPNETCORE_URLS=http://+:8668 \
    VIBE_ANALYZER_PATH=/app/Tools/vibe_analyzer.py \
    VIBE_ANALYZER_MODELS=/app/Tools/models \
    VIBE_ANALYZER_PYTHON=/opt/venv/bin/python3 \
    SHAZAM_PYTHON=/opt/shazam-venv/bin/python3 \
    PATH=/opt/venv/bin:$PATH

RUN mkdir -p /data /data/home /data/.cache/pip \
    && chmod -R 0777 /data

COPY --from=build /app/publish .
COPY --from=build /src/DeezSpoTag.Services/Library/Schema /app/Schema
COPY --from=build /src/DeezSpoTag.Web/Tools /app/Tools
COPY --from=apple-wrapper-build /out/apple-wrapper-runv2 /app/Tools/AppleMusicWrapper/runv2/apple-wrapper-runv2
COPY scripts/fetch-vibe-models.sh /tmp/fetch-vibe-models.sh

RUN set -eux; \
    python3 -m venv /opt/shazam-venv; \
    /opt/shazam-venv/bin/pip install --no-cache-dir --upgrade pip; \
    /opt/shazam-venv/bin/pip install --no-cache-dir -r /app/Tools/shazam_port/requirements-modern.txt; \
    models_dir=/app/Tools/models; \
    mkdir -p "${models_dir}"; \
    if [ "${DEEZSPOTAG_FETCH_MODELS_DURING_BUILD}" = "1" ]; then \
      MODELS_DIR="${models_dir}" /tmp/fetch-vibe-models.sh; \
    else \
      echo "Skipping model fetch during Docker build; workflow must provide bundled models."; \
    fi

EXPOSE 8668

ENTRYPOINT ["dotnet", "DeezSpoTag.Web.dll"]
