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

# Todas las fuentes, Autoblog incluido. Este job es el reloj principal.
dotnet run

git add docs/noticias.json docs/semana.json docs/guiones.json docs/guiones
if git diff --cached --quiet; then
  echo "Sin notas nuevas; no hay commit."
  exit 0
fi

git commit -m "chore: actualizar noticias $(date -u +%Y-%m-%d)"
git push
