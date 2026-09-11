#!/bin/sh
set -e
cd "$(dirname "$0")"

if [ -f .env ]; then
  set -a
  # shellcheck disable=SC1091
  . ./.env
  set +a
fi

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:/usr/local/share/dotnet:/usr/local/bin:$PATH"

dotnet run

git add docs/noticias.json docs/semana.json docs/guiones.json docs/guiones \
  docs/index.html docs/semana.html docs/podcast.html docs/style.css \
  docs/app.js docs/semana.js docs/podcast.js docs/robots.txt
if git diff --cached --quiet; then
  echo "Sin cambios en docs/; no hay commit."
  exit 0
fi

git commit -m "chore: actualizar noticias $(date -u +%Y-%m-%d)"
git push
