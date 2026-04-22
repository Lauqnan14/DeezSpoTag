FROM debian:stable-slim

SHELL ["/bin/bash", "-o", "pipefail", "-c"]

RUN apt-get update && apt-get install -y \
    ca-certificates \
    bash \
    curl \
    tini \
    tzdata \
    unzip \
    && rm -rf /var/lib/apt/lists/*

ENV WRAPPER_ARGS="-H 0.0.0.0 -D 10020 -M 20020 -A 30020" \
    args="-H 0.0.0.0 -D 10020 -M 20020 -A 30020" \
    WRAPPER_LOGIN_RETRY_ATTEMPTS=2 \
    WRAPPER_LOGIN_RETRY_DELAY_SECONDS=4 \
    TZ=UTC

COPY Tools/AppleMusicWrapper/runtime/app /opt/apple-wrapper

WORKDIR /opt/apple-wrapper
ARG WRAPPER_BUNDLE_URL_X86_64=https://github.com/WorldObservationLog/wrapper/releases/download/wrapper.x86_64.latest/Wrapper.x86_64.latest.zip
ARG WRAPPER_BUNDLE_URL_ARM64=https://github.com/WorldObservationLog/wrapper/releases/download/wrapper.arm64.latest/Wrapper.arm64.latest.zip
ARG ANDROID_TZDATA_URL=https://android.googlesource.com/platform/bionic/+/e5cabca516252addb5e305c8e1e0f35cafbcafbe/libc/zoneinfo/tzdata?format=TEXT
ARG TARGETARCH

# Always hydrate runtime from the upstream architecture-specific bundle so the pushed
# image stays runnable across amd64 and arm64 hosts.
RUN set -eux; \
    if [ -d /opt/apple-wrapper/rootfs/rootfs ] && [ ! -f /opt/apple-wrapper/rootfs/system/bin/main ]; then \
      cp -a /opt/apple-wrapper/rootfs/rootfs/* /opt/apple-wrapper/rootfs/; \
    fi; \
    case "$TARGETARCH" in \
      amd64) \
        bundle_candidates="$WRAPPER_BUNDLE_URL_X86_64 https://github.com/WorldObservationLog/wrapper/releases/download/wrapper.x86_64.latest/Wrapper.x86_64.latest.zip" ;; \
      arm64) \
        bundle_candidates="$WRAPPER_BUNDLE_URL_ARM64 https://github.com/WorldObservationLog/wrapper/releases/download/wrapper.arm64.latest/Wrapper.arm64.latest.zip" ;; \
      *) echo "Unsupported TARGETARCH for wrapper bundle: $TARGETARCH" >&2; exit 1 ;; \
    esac; \
    tmpdir="$(mktemp -d)"; \
    bundle_url=""; \
    seen_candidates=""; \
    for candidate in $bundle_candidates; do \
      [ -n "$candidate" ] || continue; \
      case " $seen_candidates " in \
        *" $candidate "*) continue ;; \
      esac; \
      seen_candidates="$seen_candidates $candidate"; \
      if [ "$candidate" = "$bundle_url" ]; then \
        continue; \
      fi; \
      echo "Attempting wrapper bundle download: $candidate"; \
      if curl -fL --retry 5 --retry-all-errors --connect-timeout 20 --max-time 300 -o "$tmpdir/wrapper.zip" "$candidate"; then \
        bundle_url="$candidate"; \
        break; \
      fi; \
    done; \
    if [ -z "$bundle_url" ]; then \
      echo "Failed to download wrapper bundle for TARGETARCH=$TARGETARCH" >&2; \
      echo "Candidates: $bundle_candidates" >&2; \
      exit 1; \
    fi; \
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
    rm -rf "$tmpdir"; \
    set -eux; \
    mkdir -p \
      /opt/apple-wrapper/rootfs/apex/com.android.tzdata/etc/tz \
      /opt/apple-wrapper/rootfs/dev \
      /opt/apple-wrapper/rootfs/etc \
      /opt/apple-wrapper/rootfs/tmp \
      /opt/apple-wrapper/rootfs/usr/share/zoneinfo \
      /opt/apple-wrapper/rootfs/system/usr/share/zoneinfo \
      /opt/apple-wrapper/rootfs/data/data/com.apple.android.music/files \
      /opt/apple-wrapper/data; \
    chmod 1777 /opt/apple-wrapper/rootfs/tmp; \
    rm -f \
      /opt/apple-wrapper/rootfs/dev/null \
      /opt/apple-wrapper/rootfs/dev/zero \
      /opt/apple-wrapper/rootfs/dev/random \
      /opt/apple-wrapper/rootfs/dev/urandom \
      /opt/apple-wrapper/rootfs/dev/tty \
      /opt/apple-wrapper/rootfs/dev/ptmx; \
    mknod -m 666 /opt/apple-wrapper/rootfs/dev/null c 1 3; \
    mknod -m 666 /opt/apple-wrapper/rootfs/dev/zero c 1 5; \
    mknod -m 666 /opt/apple-wrapper/rootfs/dev/random c 1 8; \
    mknod -m 666 /opt/apple-wrapper/rootfs/dev/urandom c 1 9; \
    mknod -m 666 /opt/apple-wrapper/rootfs/dev/tty c 5 0; \
    mknod -m 666 /opt/apple-wrapper/rootfs/dev/ptmx c 5 2; \
    cp -a /usr/share/zoneinfo/. /opt/apple-wrapper/rootfs/usr/share/zoneinfo/; \
    cp -a /usr/share/zoneinfo/. /opt/apple-wrapper/rootfs/system/usr/share/zoneinfo/; \
    curl -fSL --retry 5 --retry-all-errors --connect-timeout 20 --max-time 120 "$ANDROID_TZDATA_URL" | base64 -d > /opt/apple-wrapper/rootfs/system/usr/share/zoneinfo/tzdata; \
    cp -f /opt/apple-wrapper/rootfs/system/usr/share/zoneinfo/tzdata /opt/apple-wrapper/rootfs/usr/share/zoneinfo/tzdata; \
    cp -f /opt/apple-wrapper/rootfs/system/usr/share/zoneinfo/tzdata /opt/apple-wrapper/rootfs/apex/com.android.tzdata/etc/tz/tzdata; \
    cp -f /usr/share/zoneinfo/UTC /opt/apple-wrapper/rootfs/etc/localtime; \
    printf 'UTC\n' > /opt/apple-wrapper/rootfs/etc/timezone; \
    chmod 755 /opt/apple-wrapper/entrypoint.sh; \
    chmod 755 /opt/apple-wrapper/wrapper; \
    if [ -f /opt/apple-wrapper/rootfs/system/bin/main ]; then \
      chmod 755 /opt/apple-wrapper/rootfs/system/bin/main; \
    fi; \
    if [ -f /opt/apple-wrapper/rootfs/system/bin/linker64 ]; then \
      chmod 755 /opt/apple-wrapper/rootfs/system/bin/linker64; \
    fi

EXPOSE 10020 20020 30020

ENTRYPOINT ["/usr/bin/tini", "--", "/opt/apple-wrapper/entrypoint.sh"]
