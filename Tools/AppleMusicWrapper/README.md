Apple Music Wrapper (native runtime)

This project embeds a local runtime for the Apple Music wrapper so it can be
run directly from the DeezSpoTag Web process without Docker.

Runtime layout
- Tools/AppleMusicWrapper/runtime/app/wrapper
- Tools/AppleMusicWrapper/runtime/app/rootfs

Source layout
- Tools/AppleMusicWrapper/source (mirrors https://github.com/itouakirai/wrapper)

Build the wrapper binary
- Run: ./Tools/AppleMusicWrapper/build-wrapper.sh
- This compiles the host "wrapper" binary into Tools/AppleMusicWrapper/runtime/app/wrapper.
- The Android "main" binary is already present in the runtime rootfs shipped with this repo.

Build the Docker wrapper image (amd64 parity path)
- Run: ./Tools/AppleMusicWrapper/build-image.sh
- Default output tag: `deezspotag-apple-wrapper:local-amd64`
- This builds the published-style wrapper container from `Tools/AppleMusicWrapper/Dockerfile`
  and runs the wrapper smoke audit automatically.
- Skip the smoke audit only if you are iterating locally:
  `SKIP_SMOKE=1 ./Tools/AppleMusicWrapper/build-image.sh custom-tag`

Container portability notes
- The Docker image now bakes the minimal device nodes required by the wrapper rootfs.
- The image also hydrates timezone data into the chrooted Android rootfs to avoid
  `__bionic_open_tzdata` warnings on Linux hosts.
- Shared-control mode remains the supported deployment path for Docker/NAS installs.

Rootfs prerequisites (one-time, requires root)
- Create /dev nodes:
  sudo ./Tools/AppleMusicWrapper/setup-rootfs-dev.sh
- Bind-mount kernel filesystems:
  sudo ./Tools/AppleMusicWrapper/setup-rootfs-mounts.sh

Notes
- The wrapper performs a chroot into rootfs, so it must run as root or with
  CAP_SYS_CHROOT. If you prefer capabilities, run:
  sudo setcap cap_sys_chroot+ep Tools/AppleMusicWrapper/runtime/app/wrapper
- The service expects runtime under Tools/AppleMusicWrapper/runtime unless
  DEEZSPOTAG_APPLE_WRAPPER_DIR is set.
