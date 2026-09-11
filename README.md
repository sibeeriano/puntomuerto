# Punto muerto — resumen diario de noticias

Consolita en C#/.NET 8 que junta noticias de autos desde varios RSS, las
guarda en SQLite (dedup automático por `link`) y escribe un sitio estático
en `docs/` para publicarlo en Vercel o GitHub Pages. No hace falta un
servidor corriendo 24/7: el programa corre una vez por día, regenera
`docs/noticias.json` y el sitio se actualiza solo.

## Requisitos

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Setup

```bash
dotnet restore
dotnet build
dotnet run
```

La primera corrida crea `noticias.db` (SQLite, un solo archivo) en la carpeta
del proyecto y trae todo lo que haya disponible ahora mismo en cada feed.
Las corridas siguientes solo insertan lo nuevo: el `UNIQUE` en el link se
encarga del deduplicado.

Cada corrida, además, regenera `docs/noticias.json` (todas las notas de la
base) y `docs/semana.json` (lo más relevante de los últimos 7 días,
agrupando títulos parecidos). El viernes alcanza con abrir **Esta semana**.

## Sitio estático (frontend)

El frontend vive en `docs/` y es HTML/CSS/JS plano: sin frameworks ni build.

| Archivo | Rol |
|---|---|
| `docs/index.html` | Portada diaria |
| `docs/semana.html` | Lo más relevante de la semana |
| `docs/style.css` | Estilos |
| `docs/app.js` | Filtros + tarjetas del día |
| `docs/semana.js` | Tarjetas de la semana |
| `docs/noticias.json` | Todas las notas |
| `docs/semana.json` | Temas destacados (lo escribe la consolita) |

Para verlo en local:

```bash
cd docs
python3 -m http.server 8080
```

Abrí [http://localhost:8080](http://localhost:8080). `fetch` no funciona si
abrís el `index.html` como `file://`.

El sitio muestra solapas por fuente (más **Todas**), fecha relativa
("hace 3 h") y un link a la nota original.

## Esta semana (automático)

No hace falta un job extra los viernes. El cron diario ya regenera
`docs/semana.json` con una ventana móvil de 7 días: junta notas del mismo
tema (títulos parecidos), las ordena por cuántos medios lo cubrieron y
qué tan recientes son, y deja ~24 destacadas. El viernes abrís
`/semana.html` (o el link **Esta semana** en el home).

Si además querés el dump completo para pasárselo a una IA y armar el
guion del podcast:

```bash
dotnet run -- --digest
```

Los viernes el digest también se genera solo al correr `dotnet run`.
Queda `digest_YYYY-MM-DD.json` en la raíz (no se sube al repo).

El viernes el cron arma solo el **esqueleto** y lo deja cifrado en
`docs/guiones/`. En `/podcast.html` (botón **Podcast** del footer) ves ese
esqueleto y **Crear guion con GPT**. Eso llama a OpenAI una sola vez; después
el botón pasa a **Ver guion**. En Vercel no se genera: ahí solo se lee lo
ya guardado.

Para que el botón funcione, levantá el sitio con el programa:

```bash
dotnet run -- --sitio
```

```bash
dotnet run -- --guion-only          # esqueleto, sin GPT
dotnet run -- --guion-ia 2026-09-11 # guion GPT de esa semana (si no existe)
```

## Estado de los feeds

Probado el 11 de septiembre de 2026:

| Fuente | URL | Resultado |
|---|---|---|
| Autoblog Argentina | `https://autoblog.com.ar/feed/` | OK — 10 items |
| Autotest | `https://autotest.com.ar/feed/` | OK — 10 items (Cloudflare: si falla TLS de .NET, se baja con curl) |
| Megautos | `https://www.megautos.com/feed/` | OK — 10 items |
| iProfesional | `https://www.iprofesional.com/rss/autos` | OK — 19 items |
| El Economista | `https://eleconomista.com.ar/autos/feed/` | OK — 20 items |
| Motorpasión (ES) | `https://feeds.weblogssl.com/motorpasion` | OK — 20 items. Redirige a `https://www.motorpasion.com/feedburner.xml` |
| Diariomotor (ES) | `https://www.diariomotor.com/feed/` | OK — 20 items |

Todas las fuentes están en español. El mismo hecho (por ejemplo el lanzamiento de la Niagara) puede aparecer en varios medios: son notas distintas, con links distintos. El dedup es por URL, no por tema.

Notas:

- El programa manda un User-Agent de navegador. Sin eso, Autoblog Argentina
  también responde 403 (Cloudflare).
- Si Motorpasión falla, hay un fallback a `https://www.motorpasion.com/feedburner.xml`.
  El `/feed/` de WordPress que mencionaba el README viejo hoy da **404**.
- Fechas vacías, HTML en títulos/resúmenes o XML sin declaración no cortan
  la corrida: se limpian o se saltean ese campo. Timeouts de red reintentan
  hasta 3 veces con backoff (2s, 4s).

## Publicar en Vercel

El front es HTML estático: Vercel no corre el programa C#, solo sirve `docs/`.
Hay un `vercel.json` en la raíz para que no intente instalar npm ni
compilar .NET.

1. Subí el repo a GitHub (`git push -u origin main`).
2. En [vercel.com](https://vercel.com) → **Add New → Project** e importá
   `sibeeriano/puntomuerto`.
3. Framework Preset: **Other**. Output Directory: `docs`.
   Build e Install Command vacíos (el `vercel.json` ya lo deja así).
4. Deploy. Te da una URL tipo `https://puntomuerto.vercel.app`.

Cada `git push` redespliega. El JSON de noticias se actualiza cuando la
consolita corre, commitea `docs/noticias.json` y pushea: Vercel publica
el cambio solo.

## Publicar en GitHub Pages

El repo ya apunta a `https://github.com/sibeeriano/puntomuerto`. El sitio
queda en `https://sibeeriano.github.io/puntomuerto/`.

1. Subí el repo (`git push -u origin main`) si todavía no está en GitHub.
2. En GitHub: **Settings → Pages**.
3. En **Build and deployment → Source** elegí **Deploy from a branch**.
4. Branch: `main`, folder: `/docs`.
5. Save. Al cabo de uno o dos minutos el sitio queda en
   `https://sibeeriano.github.io/puntomuerto/`.

Hay un archivo `docs/.nojekyll` para que GitHub no procese la carpeta con
Jekyll.

## Automatizarlo (una sola tarea programada)

La idea: una vez por día corre la consolita, regenera `docs/noticias.json`
y `docs/semana.json`, hace commit y push. Vercel (o GitHub Pages) publica
los JSON nuevos y el sitio se actualiza solo. El viernes no hay que hacer
nada extra: la ventana de 7 días ya está al día.

Hay un script `actualizar.sh` en la raíz que hace exactamente eso:

```bash
chmod +x actualizar.sh
./actualizar.sh
```

Adentro: `dotnet run` → `git add` de `docs/` → commit solo si hubo cambios
→ `git push`.

### Mac / Linux (cron)

```cron
0 8 * * * cd "/ruta/al/puntomuerto" && ./actualizar.sh >> /tmp/autos-hoy.log 2>&1
```

Requisitos: `dotnet` en el PATH del cron (a veces hay que usar la ruta
completa, p. ej. `$HOME/.dotnet/dotnet run` o exportar `PATH` al inicio
del script) y `git` autenticado para pushear (SSH o credential helper).

### Windows (Task Scheduler)

1. Acción: *Iniciar un programa*.
2. Programa: `dotnet.exe` (o el `actualizar.sh` vía Git Bash).
3. Argumentos, si no usás el script: `run`.
4. "Iniciar en": la carpeta del proyecto.
5. Después, en la misma tarea o en un `.bat`:

```bat
cd /d C:\ruta\al\puntomuerto
dotnet run
git add docs\noticias.json docs\semana.json
git diff --cached --quiet || git commit -m "chore: actualizar noticias"
git push
```

Programala todos los días a la hora que quieras.

## Fuentes para confirmar y sumar

Cuando confirmes una, sumala a `feeds.json` con el mismo formato:

```json
{ "name": "Nombre de la fuente", "url": "https://..." }
```

Candidatas (no verificadas todavía): Km77, Motor1 Argentina, Autocosmos
Argentina, Parabrisas. En sitios WordPress suele funcionar agregar `/feed/`
al final de la URL.

## Qué no se commitea

`.gitignore` deja afuera `bin/`, `obj/`, `noticias.db`, `.env`, los
`digest_*.json` y los `guion_*.md`. El sitio (`docs/`, incluidos
`noticias.json`, `semana.json` y los guiones cifrados) **sí** va al repo.
