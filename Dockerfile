# syntax=docker/dockerfile:1.7

ARG DOTNET_VERSION=8.0-bookworm-slim
ARG DEEZSPOTAG_BUILD_VERSION=dev

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

COPY . .

RUN dotnet restore src.sln
RUN dotnet publish DeezSpoTag.Web/DeezSpoTag.Web.csproj -c Release -o /app/publish --no-restore \
    && mkdir -p /app/publish/Tools \
    && cp -a Tools/AppleMusicWrapper /app/publish/Tools/AppleMusicWrapper

FROM golang:1.23-bookworm AS apple-wrapper-build
WORKDIR /work

COPY Tools/AppleMusicWrapper/runv2/go.mod Tools/AppleMusicWrapper/runv2/go.sum ./
RUN go mod download
COPY Tools/AppleMusicWrapper/runv2/*.go ./
RUN CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -trimpath -ldflags "-s -w" -o /out/apple-wrapper-runv2 .

FROM debian:bookworm-slim AS media-tools
ARG BENTO4_URL=https://www.bok.net/Bento4/binaries/Bento4-SDK-1-6-0-641.x86_64-unknown-linux.zip
ARG BENTO4_SHA256=

WORKDIR /tmp/media-tools

RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends ca-certificates curl unzip; \
    install -m 0755 -d /etc/apt/keyrings; \
    curl -fsSL https://dist.gpac.io/gpac/linux/gpg.asc -o /etc/apt/keyrings/gpac.asc; \
    chmod a+r /etc/apt/keyrings/gpac.asc; \
    codename="$(. /etc/os-release && echo "${VERSION_CODENAME}")"; \
    printf '%s\n' \
      "Types: deb" \
      "URIs: https://dist.gpac.io/gpac/linux/debian" \
      "Suites: ${codename}" \
      "Components: main" \
      "Signed-By: /etc/apt/keyrings/gpac.asc" \
      > /etc/apt/sources.list.d/gpac.sources; \
    apt-get update; \
    apt-get install -y --no-install-recommends gpac; \
    mp4box_path="$(command -v MP4Box || true)"; \
    if [ -z "$mp4box_path" ]; then mp4box_path="$(command -v mp4box || true)"; fi; \
    if [ -z "$mp4box_path" ]; then echo "MP4Box not found after GPAC install." >&2; exit 1; fi; \
    install -m 0755 "$mp4box_path" /usr/local/bin/mp4box; \
    curl -fL -sS -o bento4.zip "$BENTO4_URL"; \
    if [ -n "$BENTO4_SHA256" ]; then echo "$BENTO4_SHA256  bento4.zip" | sha256sum -c -; fi; \
    mkdir -p /tmp/bento4; \
    unzip -q bento4.zip -d /tmp/bento4; \
    mp4decrypt_path="$(find /tmp/bento4 -type f -name mp4decrypt -perm -111 | head -n 1)"; \
    if [ -z "$mp4decrypt_path" ]; then echo "mp4decrypt not found after Bento4 setup." >&2; exit 1; fi; \
    install -m 0755 "$mp4decrypt_path" /usr/local/bin/mp4decrypt; \
    rm -rf /var/lib/apt/lists/* /tmp/media-tools /tmp/bento4

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app
ARG DEEZSPOTAG_BUILD_VERSION=dev
ARG ESSENTIA_TF_WHEEL_URL=https://files.pythonhosted.org/packages/f0/5f/7283634ee1d5d195d75986adc98a2309fab2df121a4618f3826eb2073d29/essentia_tensorflow-2.1b6.dev1389-cp311-cp311-manylinux_2_17_x86_64.manylinux2014_x86_64.whl

RUN apt-get update \
    && apt-get install -y --no-install-recommends openssl ca-certificates python3 python3-venv python3-pip curl aria2 ffmpeg \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --gid 1001 appuser \
    && useradd --uid 1001 --gid 1001 --no-create-home --shell /sbin/nologin appuser

COPY docker/openssl-legacy.cnf /etc/ssl/openssl-legacy.cnf

COPY --from=media-tools /usr/local/bin/mp4box /usr/local/bin/mp4box
COPY --from=media-tools /usr/local/bin/mp4decrypt /usr/local/bin/mp4decrypt

RUN set -eux; \
    python3 -m venv /opt/venv; \
    /opt/venv/bin/pip install --no-cache-dir --upgrade pip; \
    /opt/venv/bin/pip install --no-cache-dir "numpy>=1.25" pyyaml six; \
    wheel_filename="$(basename "${ESSENTIA_TF_WHEEL_URL}")"; \
    wheel_path="/tmp/${wheel_filename}"; \
    aria2c \
      --allow-overwrite=true \
      --continue=true \
      --file-allocation=none \
      --max-connection-per-server=8 \
      --split=8 \
      --min-split-size=1M \
      --connect-timeout=30 \
      --timeout=60 \
      --retry-wait=5 \
      --max-tries=0 \
      --summary-interval=15 \
      --dir=/tmp \
      --out="${wheel_filename}" \
      "${ESSENTIA_TF_WHEEL_URL}"; \
    /opt/venv/bin/pip install --no-cache-dir --no-deps "${wheel_path}"; \
    rm -f "${wheel_path}"

ENV OPENSSL_CONF=/etc/ssl/openssl-legacy.cnf \
    OPENSSL_MODULES=/usr/lib/x86_64-linux-gnu/ossl-modules \
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
    SHAZAM_PYTHON=/opt/venv/bin/python3 \
    PATH=/opt/venv/bin:$PATH

RUN mkdir -p /data

COPY --from=build /app/publish .
COPY --from=build /src/DeezSpoTag.Services/Library/Schema /app/Schema
COPY --from=build /src/DeezSpoTag.Web/Tools /app/Tools
COPY --from=apple-wrapper-build /out/apple-wrapper-runv2 /app/Tools/AppleMusicWrapper/runv2/apple-wrapper-runv2

# Ensure all analyzer models required by current code paths are present.
# Bundled repository models are used first; any missing files are fetched at build time.
RUN set -eux; \
    models_dir=/app/Tools/models; \
    mkdir -p "${models_dir}"; \
    fetch_if_missing() { \
      file="$1"; \
      url="$2"; \
      if [ ! -s "${models_dir}/${file}" ]; then \
        curl -fL -o "${models_dir}/${file}" "${url}"; \
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
    fetch_if_missing "genre_discogs400-discogs-effnet-1.json" "https://essentia.upf.edu/models/classification-heads/genre_discogs400/genre_discogs400-discogs-effnet-1.json"

RUN chown -R appuser:appuser /app /data

USER appuser

EXPOSE 8668

ENTRYPOINT ["dotnet", "DeezSpoTag.Web.dll"]
