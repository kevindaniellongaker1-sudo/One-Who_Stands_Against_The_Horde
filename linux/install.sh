#!/bin/sh
# OWSATH — One Who Stands Against The Horde: Linux installer
# Makes the game executable and (optionally) adds a menu entry.
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"
chmod +x "$DIR/OWSATH"
echo "OWSATH is ready. Run it with:  $DIR/OWSATH"
if [ -d "$HOME/.local/share/applications" ]; then
  sed "s|@DIR@|$DIR|g" "$DIR/owsath.desktop" > "$HOME/.local/share/applications/owsath.desktop"
  echo "Menu entry installed (log out/in if it doesn't appear)."
fi
echo "Saves live in ~/Desktop/Galaxy Sky (or ~/Galaxy Sky if no Desktop)."
