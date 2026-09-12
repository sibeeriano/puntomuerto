#!/bin/sh
set -e
cd "$(dirname "$0")"

git pull --ff-only

if [ -f .env ]; then
  set -a
  # shellcheck disable=SC1091
  . ./.env
  set +a
fi

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:/usr/local/share/dotnet:/usr/local/bin:$PATH"

dotnet run -- --fuente "Autoblog Argentina"

git add docs/noticias.json docs/semana.json docs/guiones.json docs/guiones
if git diff --cached --quiet; then
  echo "Sin notas nuevas de Autoblog; no hay commit."
  exit 0
fi

git commit -m "chore: actualizar Autoblog $(date -u +%Y-%m-%d)"
git push
