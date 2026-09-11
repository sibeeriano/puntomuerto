const fuenteFiltroEl = document.getElementById("fuente-filtro");
const gridEl = document.getElementById("grid");
const statusEl = document.getElementById("status");
const updatedEl = document.getElementById("updated");

let destacadas = [];
let fuenteActiva = "Todas";

init();

async function init() {
  try {
    const res = await fetch("semana.json", { cache: "no-store" });
    if (!res.ok) throw new Error(`No se pudo leer semana.json (${res.status})`);

    const data = await res.json();
    destacadas = Array.isArray(data.destacadas) ? data.destacadas : [];

    updatedEl.textContent = formatearRango(data.desde, data.hasta, destacadas.length);

    fuenteFiltroEl.addEventListener("change", () => {
      fuenteActiva = fuenteFiltroEl.value;
      renderCards();
    });

    renderFuentes(destacadas);
    renderCards();
  } catch (err) {
    updatedEl.textContent = "No se pudo cargar el resumen de la semana";
    showStatus("Abrí este sitio por HTTP (Vercel, GitHub Pages o un servidor local). El archivo semana.json no se puede leer desde file://.");
    console.error(err);
  }
}

function renderFuentes(items) {
  const fuentes = [...new Set(items.flatMap((a) => a.fuentes || [a.fuente]).filter(Boolean))].sort((a, b) =>
    a.localeCompare(b, "es")
  );
  fuenteFiltroEl.replaceChildren();
  for (const label of ["Todas", ...fuentes]) {
    const option = document.createElement("option");
    option.value = label;
    option.textContent = label;
    fuenteFiltroEl.append(option);
  }
  fuenteFiltroEl.value = fuenteActiva;
}

function renderCards() {
  const visibles = destacadas.filter((a) =>
    fuenteActiva === "Todas" ||
    a.fuente === fuenteActiva ||
    (a.fuentes || []).includes(fuenteActiva)
  );
  gridEl.replaceChildren();

  if (!visibles.length) {
    showStatus("No hay temas destacados para ese filtro.");
    return;
  }

  hideStatus();
  const frag = document.createDocumentFragment();
  for (const item of visibles) {
    frag.append(cardEl(item));
  }
  gridEl.append(frag);
}

function cardEl(item) {
  const article = document.createElement("article");
  article.className = "card";

  if (item.imagen) {
    const media = document.createElement("a");
    media.className = "card-media";
    media.href = item.link || "#";
    media.target = "_blank";
    media.rel = "noopener noreferrer";
    const img = document.createElement("img");
    img.src = item.imagen;
    img.alt = "";
    img.loading = "lazy";
    img.referrerPolicy = "no-referrer";
    img.addEventListener("error", () => media.remove());
    media.append(img);
    article.append(media);
  }

  const meta = document.createElement("div");
  meta.className = "meta";

  const fuente = document.createElement("span");
  fuente.className = "fuente";
  fuente.textContent = item.fuente || "Fuente";

  const fecha = document.createElement("time");
  fecha.dateTime = item.fecha || "";
  fecha.textContent = fechaRelativa(item.fecha);

  meta.append(fuente, fecha);

  const title = document.createElement("h2");
  title.textContent = item.titulo || "(sin título)";

  const resumen = document.createElement("p");
  resumen.className = "resumen";
  resumen.textContent = item.resumen || "Sin resumen.";

  const body = document.createElement("div");
  body.className = "card-body";
  body.append(meta, title, resumen);

  const otras = (item.fuentes || []).filter((f) => f && f !== item.fuente);
  if (otras.length) {
    const badge = document.createElement("p");
    badge.className = "menciones";
    badge.textContent = otras.length === 1
      ? `También en ${otras[0]}`
      : `También en ${otras.join(", ")}`;
    body.append(badge);
  }

  const link = document.createElement("a");
  link.className = "leer";
  link.href = item.link || "#";
  link.target = "_blank";
  link.rel = "noopener noreferrer";
  link.textContent = "Leer nota";
  body.append(link);

  article.append(body);
  return article;
}

function fechaRelativa(iso) {
  if (!iso) return "sin fecha";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "sin fecha";

  const mins = Math.round((Date.now() - d.getTime()) / 60000);
  if (mins < 1) return "hace un momento";
  if (mins < 60) return `hace ${mins} min`;
  const horas = Math.round(mins / 60);
  if (horas < 24) return `hace ${horas} h`;
  const dias = Math.round(horas / 24);
  if (dias === 1) return "hace 1 día";
  if (dias < 7) return `hace ${dias} días`;
  return d.toLocaleDateString("es", { day: "numeric", month: "short" });
}

function formatearRango(desde, hasta, total) {
  const opts = { day: "numeric", month: "long" };
  const start = parseIsoDate(desde);
  const end = parseIsoDate(hasta);
  const rango = start && end
    ? `${start.toLocaleDateString("es", opts)} – ${end.toLocaleDateString("es", opts)}`
    : "últimos 7 días";
  const noun = total === 1 ? "tema" : "temas";
  return `${rango} · ${total} ${noun} destacados`;
}

function parseIsoDate(value) {
  if (!value) return null;
  const d = new Date(`${value}T12:00:00`);
  return Number.isNaN(d.getTime()) ? null : d;
}

function showStatus(msg) {
  statusEl.hidden = false;
  statusEl.textContent = msg;
}

function hideStatus() {
  statusEl.hidden = true;
  statusEl.textContent = "";
}
