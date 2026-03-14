Apple Music Wrapper (systemd install)

This option runs the wrapper as a dedicated systemd service (no Docker). The web app
then talks to localhost ports 10020/20020/30020.

Install (run once, root)
- Ensure Tools/AppleMusicWrapper/runtime/app is fully populated (wrapper + rootfs).
- Run: sudo ./Tools/AppleMusicWrapper/systemd/install-systemd.sh

Login / run control
- Login (start with credentials):
  sudo ./Tools/AppleMusicWrapper/systemd/apple-wrapperctl.sh login user:pass
- Run without login (keeps wrapper up):
  sudo ./Tools/AppleMusicWrapper/systemd/apple-wrapperctl.sh run
- Check status/logs:
  sudo ./Tools/AppleMusicWrapper/systemd/apple-wrapperctl.sh status
  sudo ./Tools/AppleMusicWrapper/systemd/apple-wrapperctl.sh logs

Files installed
- /opt/apple-wrapper (runtime)
- /etc/systemd/system/apple-wrapper.service
- /etc/default/apple-wrapper (base args, mode 0600)
- /run/apple-wrapper-login.env (ephemeral login args; removed after service start)

Notes
- The wrapper runs as root because it needs chroot/mknod.
- The default args bind the wrapper to `127.0.0.1` only.
- Login credentials are written to `/run/apple-wrapper-login.env` for one restart only,
  then deleted automatically by systemd.
- This avoids bind-mount churn during app runtime.
- If you update the runtime, re-run install-systemd.sh.
