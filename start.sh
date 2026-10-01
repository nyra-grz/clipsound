#!/bin/zsh
# Startet den Server (falls er nicht schon läuft) und öffnet das Soundboard in Safari
DIR="$HOME/meme-soundboard"
NODE="$(command -v node || echo /usr/local/bin/node)"
if ! curl -s -o /dev/null http://localhost:3000; then
  cd "$DIR" && nohup "$NODE" server.js > server.log 2>&1 &
  for i in {1..20}; do curl -s -o /dev/null http://localhost:3000 && break; sleep 0.2; done
fi
open -a Safari http://localhost:3000
