# syntax=docker/dockerfile:1.7

ARG DOTNET_VERSION=8.0-bookworm-slim
ARG DEEZSPOTAG_BUILD_VERSION=dev

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

COPY . .

RUN dotnet restore DeezSpoTag.Web/DeezSpoTag.Web.csproj
RUN dotnet publish DeezSpoTag.Web/DeezSpoTag.Web.csproj -c Release -o /app/publish --no-restore \
    && mkdir -p /app/publish/Tools \
    && cp -a Tools/AppleMusicWrapper /app/publish/Tools/AppleMusicWrapper

FROM docker:27-cli AS docker-cli

FROM golang:1.25-bookworm AS apple-wrapper-build
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
ARG TARGETARCH
ARG BENTO4_URL_X86_64=https://www.bok.net/Bento4/binaries/Bento4-SDK-1-6-0-641.x86_64-unknown-linux.zip
ARG BENTO4_SHA256=
ARG ESSENTIA_TF_PACKAGE=essentia-tensorflow==2.1b6.dev1389
LABEL org.opencontainers.image.source="https://github.com/Lauqnan14/DeezSpoTag" \
      org.opencontainers.image.title="deezspotag"

RUN apt-get update -o Acquire::Retries=5 \
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
    && install -m 0755 -d /etc/apt/keyrings \
    && curl --fail --show-error --silent --location \
       --retry 8 --retry-all-errors --retry-delay 2 --connect-timeout 10 --max-time 120 \
       https://dist.gpac.io/gpac/linux/gpg.asc \
       -o /etc/apt/keyrings/gpac.asc \
    && chmod a+r /etc/apt/keyrings/gpac.asc \
    && codename="$(. /etc/os-release && echo "${VERSION_CODENAME}")" \
    && printf '%s\n' \
      "Types: deb" \
      "URIs: https://dist.gpac.io/gpac/linux/debian" \
      "Suites: ${codename}" \
      "Components: main" \
      "Signed-By: /etc/apt/keyrings/gpac.asc" \
      > /etc/apt/sources.list.d/gpac.sources \
    && apt-get update -o Acquire::Retries=5 \
    && apt-get install -y --no-install-recommends gpac \
    && mp4box_path="$(command -v MP4Box || true)" \
    && if [ -z "$mp4box_path" ]; then mp4box_path="$(command -v mp4box || true)"; fi \
    && if [ -z "$mp4box_path" ]; then echo "MP4Box not found after GPAC install." >&2; exit 1; fi \
    && install -m 0755 "$mp4box_path" /usr/local/bin/mp4box \
    && if [ "${TARGETARCH:-amd64}" = "amd64" ]; then \
         curl --fail --show-error --silent --location \
           --retry 6 --retry-all-errors --retry-delay 2 --connect-timeout 10 --max-time 180 \
           -o /tmp/bento4.zip "$BENTO4_URL_X86_64"; \
         if [ -n "$BENTO4_SHA256" ]; then echo "$BENTO4_SHA256  /tmp/bento4.zip" | sha256sum -c -; fi; \
         mkdir -p /tmp/bento4; \
         unzip -q /tmp/bento4.zip -d /tmp/bento4; \
         mp4decrypt_path="$(find /tmp/bento4 -type f -name mp4decrypt -perm -111 | head -n 1)"; \
         if [ -n "$mp4decrypt_path" ]; then install -m 0755 "$mp4decrypt_path" /usr/local/bin/mp4decrypt; fi; \
         rm -rf /tmp/bento4 /tmp/bento4.zip; \
       else \
         echo "Skipping mp4decrypt install for TARGETARCH=${TARGETARCH:-unknown}:" \
              "no official Bento4 arm64 binary URL is configured."; \
       fi \
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

COPY --from=docker-cli /usr/local/bin/docker /usr/local/bin/docker
COPY --from=docker-cli /usr/local/libexec/docker/cli-plugins/docker-compose /usr/local/libexec/docker/cli-plugins/docker-compose

RUN set -eux; \
    python3 -m venv /opt/venv; \
    /opt/venv/bin/pip install --no-cache-dir --upgrade pip; \
    /opt/venv/bin/pip install --no-cache-dir "numpy>=1.25" pyyaml six; \
    if ! /opt/venv/bin/pip install --no-cache-dir "${ESSENTIA_TF_PACKAGE}"; then \
      echo "essentia-tensorflow install failed for TARGETARCH=${TARGETARCH:-unknown};" \
           "continuing with degraded analysis support."; \
    fi

ENV OPENSSL_CONF=/etc/ssl/openssl-legacy.cnf \
    HOME=/data/home \
    XDG_CACHE_HOME=/data/.cache \
    PIP_CACHE_DIR=/data/.cache/pip \
    PIP_NO_CACHE_DIR=1 \
    DEEZSPOTAG_CONFIG_DIR=/data \
    DEEZSPOTAG_DATA_DIR=/data \
    DEEZSPOTAG_BUILD_VERSION=${DEEZSPOTAG_BUILD_VERSION} \
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

RUN set -eux; \
    python3 -m venv /opt/shazam-venv; \
    /opt/shazam-venv/bin/pip install --no-cache-dir --upgrade pip; \
    /opt/shazam-venv/bin/pip install --no-cache-dir -r /app/Tools/shazam_port/requirements-modern.txt; \
    # Ensure all analyzer models required by current code paths are present. \
    # Bundled repository models are used first; any missing files are fetched best-effort. \
    # Runtime provisioning in TrackAnalysisBackgroundService will retry missing models. \
    models_dir=/app/Tools/models; \
    mkdir -p "${models_dir}"; \
    model_fetch_failures=0; \
    fetch_if_missing() { \
      file="$1"; \
      url="$2"; \
      target="${models_dir}/${file}"; \
      tmp="${target}.tmp"; \
      if [ ! -s "${target}" ]; then \
        attempt=1; \
        max_attempts=8; \
        while [ "${attempt}" -le "${max_attempts}" ]; do \
          if curl -fL --connect-timeout 20 --max-time 300 -o "${tmp}" "${url}"; then \
            mv "${tmp}" "${target}"; \
            return 0; \
          fi; \
          rm -f "${tmp}"; \
          if [ "${attempt}" -eq "${max_attempts}" ]; then \
            echo "WARNING: Failed to download ${file} from ${url} after ${max_attempts} attempts." >&2; \
            model_fetch_failures=$((model_fetch_failures + 1)); \
            return 0; \
          fi; \
          sleep_seconds=$((attempt * 3)); \
          echo "Retry ${attempt}/${max_attempts} for ${file} in ${sleep_seconds}s..." >&2; \
          sleep "${sleep_seconds}"; \
          attempt=$((attempt + 1)); \
        done; \
      fi; \
    }; \
    fetch_if_missing "msd-musicnn-1.pb" "https://essentia.upf.edu/models/feature-extractors/musicnn/msd-musicnn-1.pb"; \
    fetch_if_missing "mood_happy-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/mood_happy/mood_happy-msd-musicnn-1.pb"; \
    fetch_if_missing "mood_sad-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/mood_sad/mood_sad-msd-musicnn-1.pb"; \
    fetch_if_missing "mood_relaxed-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/mood_relaxed/mood_relaxed-msd-musicnn-1.pb"; \
    fetch_if_missing "mood_aggressive-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/mood_aggressive/mood_aggressive-msd-musicnn-1.pb"; \
    fetch_if_missing "mood_party-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/mood_party/mood_party-msd-musicnn-1.pb"; \
    fetch_if_missing "mood_acoustic-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/mood_acoustic/mood_acoustic-msd-musicnn-1.pb"; \
    fetch_if_missing "mood_electronic-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/mood_electronic/mood_electronic-msd-musicnn-1.pb"; \
    fetch_if_missing "voice_instrumental-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/voice_instrumental/voice_instrumental-msd-musicnn-1.pb"; \
    fetch_if_missing "tonal_atonal-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/tonal_atonal/tonal_atonal-msd-musicnn-1.pb"; \
    fetch_if_missing "danceability-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/danceability/danceability-msd-musicnn-1.pb"; \
    fetch_if_missing "deam-msd-musicnn-2.pb" "https://essentia.upf.edu/models/classification-heads/deam/deam-msd-musicnn-2.pb"; \
    fetch_if_missing "discogs-effnet-bs64-1.pb" "https://essentia.upf.edu/models/feature-extractors/discogs-effnet/discogs-effnet-bs64-1.pb"; \
    fetch_if_missing "approachability_regression-discogs-effnet-1.pb" "https://essentia.upf.edu/models/classification-heads/approachability/approachability_regression-discogs-effnet-1.pb"; \
    fetch_if_missing "engagement_regression-discogs-effnet-1.pb" "https://essentia.upf.edu/models/classification-heads/engagement/engagement_regression-discogs-effnet-1.pb"; \
    fetch_if_missing "genre_discogs400-discogs-effnet-1.pb" "https://essentia.upf.edu/models/classification-heads/genre_discogs400/genre_discogs400-discogs-effnet-1.pb"; \
    fetch_if_missing "genre_discogs400-discogs-effnet-1.json" "https://essentia.upf.edu/models/classification-heads/genre_discogs400/genre_discogs400-discogs-effnet-1.json"; \
    if [ "${model_fetch_failures}" -gt 0 ]; then \
      echo "WARNING: ${model_fetch_failures} model file(s) were not fetched during image build." \
        "Runtime will retry provisioning." >&2; \
    fi

EXPOSE 8668

ENTRYPOINT ["dotnet", "DeezSpoTag.Web.dll"]
