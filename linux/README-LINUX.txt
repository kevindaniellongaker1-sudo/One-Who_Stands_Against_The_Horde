OWSATH — One Who Stands Against The Horde (Linux x64)
=====================================================

QUICK START
  1. Extract this archive anywhere:   tar xzf OWSATH-linux-x64.tar.gz
  2. cd OWSATH-linux
  3. sh install.sh          (or just: chmod +x OWSATH)
  4. ./OWSATH

WHAT YOU NEED
  - A 64-bit Linux with a desktop (X11 or XWayland) — the game opens a
    graphics window via raylib, which is bundled; no packages to install
    on most distros. If the window fails to open on a bare server, you
    are missing X libraries (install your distro's xorg/libX11 basics).
  - No .NET installation needed — the runtime is built in.

PLAYING
  The graphics window shows the battlefield, party stats, and clickable
  buttons for every choice; the terminal underneath mirrors everything
  and accepts typed input too. Keyboard works in the window (arrow keys
  move square by square; A-Z name entry on-screen or typed).

SAVES
  ~/Desktop/Galaxy Sky   (or ~/Galaxy Sky when no Desktop folder exists)
  One .sav per character plus the high-score board — the same format as
  the Windows build, so save files can be copied between machines.
