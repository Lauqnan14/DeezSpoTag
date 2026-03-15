FROM debian:trixie-slim

RUN apt-get update && apt-get install -y \
    ca-certificates \
    bash \
    proot \
    curl \
    unzip \
    && rm -rf /var/lib/apt/lists/*

COPY Tools/AppleMusicWrapper/runtime/app /opt/apple-wrapper

WORKDIR /opt/apple-wrapper
ARG WRAPPER_BUNDLE_URL_X86_64=https://github.com/WorldObservationLog/wrapper/releases/download/Wrapper.x86_64.1dac7bb/Wrapper.x86_64.1dac7bb.zip
ARG WRAPPER_BUNDLE_URL_ARM64=https://github.com/WorldObservationLog/wrapper/releases/download/Wrapper.arm64.latest/Wrapper.arm64.e338fa6.zip

# Some runtime bundles nest the Android rootfs under rootfs/rootfs.
# Ensure system/bin/main exists at /opt/apple-wrapper/rootfs/system/bin/main.
RUN set -eux; \
    if [ -d /opt/apple-wrapper/rootfs/rootfs ] && [ ! -f /opt/apple-wrapper/rootfs/system/bin/main ]; then \
      cp -a /opt/apple-wrapper/rootfs/rootfs/* /opt/apple-wrapper/rootfs/; \
    fi; \
    if [ ! -f /opt/apple-wrapper/rootfs/system/bin/main ]; then \
      arch="$(dpkg --print-architecture)"; \
      case "$arch" in \
        amd64) bundle_url="$WRAPPER_BUNDLE_URL_X86_64" ;; \
        arm64) bundle_url="$WRAPPER_BUNDLE_URL_ARM64" ;; \
        *) echo "Unsupported architecture for wrapper bundle: $arch" >&2; exit 1 ;; \
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
      if [ -n "$bundle_wrapper" ]; then cp -f "$bundle_wrapper" /opt/apple-wrapper/wrapper; fi; \
      rm -rf "$tmpdir"; \
    fi

# Align Docker runtime with local setup scripts: ensure mount points exist so
# wrapper startup is consistent in container mode. Device nodes are provided by
# the runtime rootfs and host mounts; avoid creating char devices in image layers.
RUN set -eux; \
    dev_dir="/opt/apple-wrapper/rootfs/dev"; \
    mkdir -p "$dev_dir" "/opt/apple-wrapper/rootfs/dev/pts" "/opt/apple-wrapper/rootfs/proc" "/opt/apple-wrapper/rootfs/sys"

RUN chmod 4755 /opt/apple-wrapper/wrapper && \
    if [ -f /opt/apple-wrapper/rootfs/system/bin/main ]; then chmod 755 /opt/apple-wrapper/rootfs/system/bin/main; fi && \
    if [ -f /opt/apple-wrapper/rootfs/system/bin/linker64 ]; then chmod 755 /opt/apple-wrapper/rootfs/system/bin/linker64; fi && \
    if [ -f /opt/apple-wrapper/rootfs/rootfs/system/bin/main ]; then chmod 755 /opt/apple-wrapper/rootfs/rootfs/system/bin/main; fi && \
    if [ -f /opt/apple-wrapper/rootfs/rootfs/system/bin/linker64 ]; then chmod 755 /opt/apple-wrapper/rootfs/rootfs/system/bin/linker64; fi

ENTRYPOINT ["/bin/bash", "-lc", "set -euo pipefail; IFS=' ' read -r -a wrapper_args <<< \"${WRAPPER_ARGS:-}\"; exec /opt/apple-wrapper/wrapper \"${wrapper_args[@]}\""]
