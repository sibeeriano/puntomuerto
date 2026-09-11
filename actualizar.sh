#!/bin/sh
set -e
cd "$(dirname "$0")"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:/usr/local/share/dotnet:/usr/local/bin:$PATH"

dotnet run

git add docs/noticias.json docs/index.html docs/style.css docs/app.js
if git diff --cached --quiet; then
  echo "Sin cambios en docs/; no hay commit."
  exit 0
fi

git commit -m "chore: actualizar noticias $(date -u +%Y-%m-%d)"
git push
