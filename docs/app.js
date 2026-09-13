const fuenteFiltroEl = document.getElementById("fuente-filtro");
const gridEl = document.getElementById("grid");
const statusEl = document.getElementById("status");
const updatedEl = document.getElementById("updated");
const fechaFiltroEl = document.getElementById("fecha-filtro");
const pagerEl = document.getElementById("pager");
const masEl = document.getElementById("mas");

const PAGE_SIZE = 12;

let articulos = [];
let fuenteActiva = "Todas";
let rangoFecha = "todas";
let busqueda = (new URLSearchParams(location.search).get("q") || "").trim();
let pagina = 1;

init();

async function init() {
  try {
    const res = await fetch("noticias.json", { cache: "no-store" });
    if (!res.ok) throw new Error(`No se pudo leer noticias.json (${res.status})`);

    const data = await res.json();
    articulos = Array.isArray(data) ? data : [];

    const lastMod = res.headers.get("Last-Modified");
    const actualizado = lastMod ? new Date(lastMod) : fechaMasReciente(articulos);
    updatedEl.textContent = formatearActualizado(actualizado, articulos.length);

    fechaFiltroEl.addEventListener("change", () => {
      rangoFecha = fechaFiltroEl.value;
      pagina = 1;
      renderCards();
    });
    fuenteFiltroEl.addEventListener("change", () => {
      fuenteActiva = fuenteFiltroEl.value;
      pagina = 1;
      renderCards();
    });
    masEl.addEventListener("click", () => {
      pagina += 1;
      renderCards();
    });
    document.addEventListener("pm-search", (ev) => {
      busqueda = (ev.detail.q || "").trim();
      pagina = 1;
      renderCards();
    });

    renderFuentes(articulos);
    renderCards();
  } catch (err) {
    updatedEl.textContent = "No se pudieron cargar las noticias";
    showStatus("Abrí este sitio por HTTP (GitHub Pages o un servidor local). El archivo noticias.json no se puede leer desde file://.");
    console.error(err);
  }
}

function renderFuentes(items) {
  const fuentes = [...new Set(items.map((a) => a.fuente).filter(Boolean))].sort((a, b) =>
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
  const visibles = articulos.filter((a) =>
    (fuenteActiva === "Todas" || a.fuente === fuenteActiva) &&
    pasaFiltroFecha(a, rangoFecha) &&
    pasaBusqueda(a)
  );
  const corte = pagina * PAGE_SIZE;
  const paginaItems = visibles.slice(0, corte);
  gridEl.replaceChildren();

  if (!visibles.length) {
    pagerEl.hidden = true;
    showStatus(busqueda ? "No hay noticias para esa búsqueda." : "No hay noticias para ese filtro.");
    return;
  }

  hideStatus();
  const frag = document.createDocumentFragment();
  for (const item of paginaItems) {
    frag.append(cardEl(item));
  }
  gridEl.append(frag);

  pagerEl.hidden = visibles.length <= paginaItems.length;
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

  const link = document.createElement("a");
  link.className = "leer";
  link.href = item.link || "#";
  link.target = "_blank";
  link.rel = "noopener noreferrer";
  link.textContent = "Leer nota";

  const body = document.createElement("div");
  body.className = "card-body";
  body.append(meta, title, resumen, link);
  article.append(body);
  return article;
}

function normTexto(value) {
  return String(value || "")
    .toLowerCase()
    .normalize("NFD")
    .replace(/\p{M}/gu, "");
}

function pasaBusqueda(item) {
  if (!busqueda) return true;
  const q = normTexto(busqueda);
  const hay = [item.titulo, item.resumen, item.fuente]
    .filter(Boolean)
    .map(normTexto)
    .join(" ");
  return hay.includes(q);
}

function pasaFiltroFecha(item, rango) {
  if (rango === "todas") return true;
  const d = parseFecha(item.fecha);
  if (!d) return false;

  const hoy = inicioDelDia(new Date());
  if (rango === "hoy") return d >= hoy;
  if (rango === "ayer") {
    const ayer = new Date(hoy);
    ayer.setDate(ayer.getDate() - 1);
    return d >= ayer && d < hoy;
  }

  const dias = rango === "7d" ? 7 : rango === "30d" ? 30 : 0;
  if (!dias) return true;
  const desde = new Date(hoy);
  desde.setDate(desde.getDate() - (dias - 1));
  return d >= desde;
}

function parseFecha(iso) {
  if (!iso) return null;
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? null : d;
}

function inicioDelDia(date) {
  const d = new Date(date);
  d.setHours(0, 0, 0, 0);
  return d;
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

function fechaMasReciente(items) {
  const dates = items
    .map((a) => new Date(a.fecha))
    .filter((d) => !Number.isNaN(d.getTime()))
    .sort((a, b) => b - a);
  return dates[0] || new Date();
}

function formatearActualizado(date, total) {
  const when = date.toLocaleDateString("es", {
    day: "numeric",
    month: "long",
    year: "numeric"
  });
  const noun = total === 1 ? "noticia" : "noticias";
  return `Última actualización: ${when} · ${total} ${noun}`;
}

function showStatus(msg) {
  statusEl.hidden = false;
  statusEl.textContent = msg;
}

function hideStatus() {
  statusEl.hidden = true;
  statusEl.textContent = "";
}
