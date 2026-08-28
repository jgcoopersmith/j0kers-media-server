j0kers Media Server — Setup
===========================

TO INSTALL OR UPGRADE
  Double-click  Install.cmd

  It installs to
      %LOCALAPPDATA%\Programs\j0kers Media Server
  and puts a shortcut on the desktop. No administrator rights needed.

  To install somewhere else:
      Install.cmd -TargetDir "D:\j0kers Media Server"

UPGRADING AN EXISTING INSTALL
  Run the same Install.cmd over it. It detects the existing install, stops the
  running server, replaces only the program, and KEEPS everything else:

      accounts and keys (users.json, signing.key)
      saved settings    (server.json, settings.json)
      channels, library, favorites, playlists, mounts, DLNA shares
      watch history and the probe cache
      the TLS certificate this machine generated
      converted media (media\) and the log history (logs\)

  Nothing you configured is overwritten. Ports, the transcodes directory, and
  every other edit survive the upgrade.

WHAT'S INSTALLED
  j0kers-media-server.exe   the server — the .NET runtime is bundled, so
                            nothing has to be installed first
  ffmpeg.exe, ffprobe.exe   media engine for transcoding and live TV
  server.json               settings (first install only; yours is kept)
  providers.json            free-TV providers (first install only)

FIRST RUN
  The dashboard opens at  http://localhost:9090/
  Create the administrator account there. That account is a Server Admin, so
  it can see everything including the Transcode panel and the log window.

  Free TV is on out of the box: Pluto TV, Tubi, The Roku Channel and
  Samsung TV Plus.

PORTS (change in server.json, or in the dashboard's Config dialog)
  9090  dashboard      8080  media / HLS      8554  RTSP
  Windows may ask once to allow the server through the firewall — that is what
  lets other devices on your network reach it.

REQUIREMENTS
  64-bit Windows 10 or 11. Nothing else.
