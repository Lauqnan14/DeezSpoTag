FROM debian:stable-slim

RUN apt-get update && apt-get install -y \
    ca-certificates \
    bash \
    curl \
    unzip \
    && rm -rf /var/lib/apt/lists/*

COPY Tools/AppleMusicWrapper/runtime/app /opt/apple-wrapper

WORKDIR /opt/apple-wrapper
ARG WRAPPER_BUNDLE_URL_X86_64=https://github.com/WorldObservationLog/wrapper/releases/download/Wrapper.x86_64.1dac7bb/Wrapper.x86_64.1dac7bb.zip
ARG WRAPPER_BUNDLE_URL_ARM64=https://github.com/WorldObservationLog/wrapper/releases/download/Wrapper.arm64.latest/Wrapper.arm64.e338fa6.zip
ARG TARGETARCH

# Always hydrate runtime from the upstream architecture-specific bundle so the pushed
# image stays runnable across amd64 and arm64 hosts.
RUN set -eux; \
    if [ -d /opt/apple-wrapper/rootfs/rootfs ] && [ ! -f /opt/apple-wrapper/rootfs/system/bin/main ]; then \
      cp -a /opt/apple-wrapper/rootfs/rootfs/* /opt/apple-wrapper/rootfs/; \
    fi; \
    case "$TARGETARCH" in \
      amd64) bundle_url="$WRAPPER_BUNDLE_URL_X86_64" ;; \
      arm64) bundle_url="$WRAPPER_BUNDLE_URL_ARM64" ;; \
      *) echo "Unsupported TARGETARCH for wrapper bundle: $TARGETARCH" >&2; exit 1 ;; \
    esac; \
    tmpdir="$(mktemp -d)"; \
    curl -fL -o "$tmpdir/wrapper.zip" "$bundle_url"; \
    unzip -q "$tmpdir/wrapper.zip" -d "$tmpdir/unpack"; \
    main_path="$(find "$tmpdir/unpack" -type f -path '*/rootfs/system/bin/main' -print -quit)"; \
    if [ -z "$main_path" ]; then \
      echo "Downloaded wrapper bundle does not contain rootfs/system/bin/main" >&2; \
      exit 1; \
    fi; \
    bundle_rootfs="$(dirname "$(dirname "$(dirname "$main_path")")")"; \
    rm -rf /opt/apple-wrapper/rootfs; \
    cp -a "$bundle_rootfs" /opt/apple-wrapper/rootfs; \
    bundle_wrapper="$(find "$tmpdir/unpack" -type f -name wrapper -perm -111 -print -quit)"; \
    if [ -z "$bundle_wrapper" ]; then \
      echo "Downloaded wrapper bundle does not contain executable wrapper binary" >&2; \
      exit 1; \
    fi; \
    cp -f "$bundle_wrapper" /opt/apple-wrapper/wrapper; \
    rm -rf "$tmpdir"

RUN set -eux; \
    mkdir -p \
      /opt/apple-wrapper/rootfs/dev \
      /opt/apple-wrapper/rootfs/data/data/com.apple.android.music/files \
      /opt/apple-wrapper/data; \
    chmod 755 /opt/apple-wrapper/entrypoint.sh; \
    chmod 755 /opt/apple-wrapper/wrapper; \
    if [ -f /opt/apple-wrapper/rootfs/system/bin/main ]; then chmod 755 /opt/apple-wrapper/rootfs/system/bin/main; fi; \
    if [ -f /opt/apple-wrapper/rootfs/system/bin/linker64 ]; then chmod 755 /opt/apple-wrapper/rootfs/system/bin/linker64; fi

ENTRYPOINT ["/opt/apple-wrapper/entrypoint.sh"]
