const STORAGE_KEY = "pm-podcast-clave";
const gateEl = document.getElementById("gate");
const claveEl = document.getElementById("clave");
const gateErrorEl = document.getElementById("gate-error");
const studioEl = document.getElementById("studio");
const semanaFiltroEl = document.getElementById("semana-filtro");
const accionEl = document.getElementById("accion-gpt");
const statusEl = document.getElementById("status");
const guionEl = document.getElementById("guion");

let index = [];
let clave = sessionStorage.getItem(STORAGE_KEY) || "";
let vista = "esqueleto";

init();

async function init() {
  try {
    await reloadIndex();
  } catch (err) {
    showGateError("No se pudieron cargar los guiones. Abrí el sitio por HTTP.");
    console.error(err);
    return;
  }

  gateEl.addEventListener("submit", async (ev) => {
    ev.preventDefault();
    await unlock(claveEl.value);
  });

  semanaFiltroEl.addEventListener("change", () => {
    vista = "esqueleto";
    if (clave) loadSemana();
  });

  accionEl.addEventListener("click", onAccion);

  if (clave) {
    const ok = await unlock(clave, true);
    if (!ok) clave = "";
  }
}

async function reloadIndex() {
  const res = await fetch("guiones.json", { cache: "no-store" });
  if (!res.ok) throw new Error(`No se pudo leer guiones.json (${res.status})`);
  const data = await res.json();
  index = Array.isArray(data) ? data : [];
}

function itemActual() {
  return index.find((item) => item.fecha === semanaFiltroEl.value) || index[0];
}

function archivoEsqueleto(item) {
  return item?.esqueleto || item?.archivo || "";
}

async function textoGuion(item) {
  const cached = sessionStorage.getItem(`pm-guion-${item.fecha}`);
  if (item.guion) {
    try {
      return await decryptArchivo(item.guion, clave);
    } catch (err) {
      if (cached) return cached;
      throw err;
    }
  }
  if (cached) return cached;
  throw new Error("Todavía no hay guion para esa semana.");
}

async function unlock(password, silent) {
  hideGateError();
  if (!password) {
    showGateError("Escribí la contraseña.");
    return false;
  }
  if (!index.length) {
    showGateError("Todavía no hay esqueletos publicados.");
    return false;
  }

  try {
    await decryptArchivo(archivoEsqueleto(index[0]), password);
  } catch (err) {
    if (!silent) showGateError("Contraseña incorrecta.");
    sessionStorage.removeItem(STORAGE_KEY);
    console.warn(err);
    return false;
  }

  clave = password;
  sessionStorage.setItem(STORAGE_KEY, password);
  gateEl.hidden = true;
  studioEl.hidden = false;
  renderSemanas();
  await loadSemana();
  return true;
}

function etiquetaViernes(item) {
  const raw = item.fecha || "";
  const d = new Date(`${raw}T12:00:00`);
  if (Number.isNaN(d.getTime())) return item.rango || raw;
  const text = d.toLocaleDateString("es-AR", { weekday: "long", day: "numeric", month: "long" }).replace(",", "");
  return text.charAt(0).toUpperCase() + text.slice(1);
}

function renderSemanas() {
  const selected = semanaFiltroEl.value;
  semanaFiltroEl.replaceChildren();
  for (const item of index) {
    const option = document.createElement("option");
    option.value = item.fecha;
    option.textContent = etiquetaViernes(item);
    semanaFiltroEl.append(option);
  }
  if (selected && index.some((item) => item.fecha === selected)) {
    semanaFiltroEl.value = selected;
  }
}

function syncBoton() {
  const item = itemActual();
  const tieneGuion = Boolean(item?.guion);
  accionEl.hidden = !item;
  accionEl.disabled = false;
  if (tieneGuion) {
    accionEl.textContent = vista === "guion" ? "Ver esqueleto" : "Ver guion";
  } else {
    accionEl.textContent = "Crear guion con GPT";
  }
}

async function loadSemana() {
  hideStatus();
  guionEl.replaceChildren();
  const item = itemActual();
  if (!item) {
    showStatus("No hay semanas publicadas.");
    syncBoton();
    return;
  }

  try {
    const md = vista === "guion"
      ? await textoGuion(item)
      : await decryptArchivo(archivoEsqueleto(item), clave);
    guionEl.innerHTML = renderMarkdown(md);
  } catch (err) {
    showStatus("No se pudo abrir ese archivo.");
    console.error(err);
  }
  syncBoton();
}

async function onAccion() {
  const item = itemActual();
  if (!item) return;

  if (item.guion) {
    vista = vista === "guion" ? "esqueleto" : "guion";
    await loadSemana();
    return;
  }

  accionEl.disabled = true;
  accionEl.textContent = "Creando…";
  hideStatus();
  try {
    const res = await fetch("/api/crear-guion", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ fecha: item.fecha, password: clave })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok || !data.ok) {
      throw new Error(data.error || "No se pudo crear el guion.");
    }
    if (data.markdown) {
      sessionStorage.setItem(`pm-guion-${item.fecha}`, data.markdown);
    }
    if (data.guion) {
      const i = index.findIndex((x) => x.fecha === item.fecha);
      if (i >= 0) index[i] = { ...index[i], guion: data.guion };
    }
    try {
      await reloadIndex();
      renderSemanas();
    } catch {
      renderSemanas();
    }
    vista = "guion";
    await loadSemana();
  } catch (err) {
    showStatus(err.message || "No se pudo crear el guion.");
    syncBoton();
  }
}

async function decryptArchivo(archivo, password) {
  const res = await fetch(archivo, { cache: "no-store" });
  if (!res.ok) throw new Error(`No se pudo leer ${archivo}`);
  const packed = await res.json();
  return decryptGuion(packed, password);
}

async function decryptGuion(packed, password) {
  const salt = b64ToBytes(packed.salt);
  const iv = b64ToBytes(packed.iv);
  const data = b64ToBytes(packed.ct);
  const material = await crypto.subtle.importKey(
    "raw",
    new TextEncoder().encode(password),
    "PBKDF2",
    false,
    ["deriveKey"]
  );
  const key = await crypto.subtle.deriveKey(
    { name: "PBKDF2", salt, iterations: 120000, hash: "SHA-256" },
    material,
    { name: "AES-GCM", length: 256 },
    false,
    ["decrypt"]
  );
  const plain = await crypto.subtle.decrypt({ name: "AES-GCM", iv }, key, data);
  return new TextDecoder().decode(plain);
}

function b64ToBytes(value) {
  const bin = atob(value);
  const bytes = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i += 1) bytes[i] = bin.charCodeAt(i);
  return bytes;
}

function renderMarkdown(md) {
  const lines = md.replace(/\r\n/g, "\n").split("\n");
  const html = [];
  let list = [];

  const flushList = () => {
    if (!list.length) return;
    html.push(`<ul>${list.map((item) => `<li>${inline(item)}</li>`).join("")}</ul>`);
    list = [];
  };

  for (const raw of lines) {
    const line = raw.trimEnd();
    if (line.startsWith("- ")) {
      list.push(line.slice(2).trim());
      continue;
    }
    flushList();
    if (!line.trim()) continue;
    if (line.startsWith("### ")) {
      html.push(`<h3>${inline(line.slice(4))}</h3>`);
    } else if (line.startsWith("## ")) {
      html.push(`<h2>${inline(line.slice(3))}</h2>`);
    } else if (line.startsWith("# ")) {
      html.push(`<h1>${inline(line.slice(2))}</h1>`);
    } else {
      html.push(`<p>${inline(line)}</p>`);
    }
  }
  flushList();
  return html.join("");
}

function inline(text) {
  let out = escapeHtml(text);
  out = out.replace(/\*\*(.+?)\*\*/g, "<strong>$1</strong>");
  out = out.replace(/`(.*?)`/g, "<code>$1</code>");
  out = out.replace(/(https?:\/\/[^\s<]+)/g, '<a href="$1" target="_blank" rel="noopener noreferrer">$1</a>');
  return out;
}

function escapeHtml(text) {
  return text.replace(/[&<>"']/g, (ch) => ({
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    '"': "&quot;",
    "'": "&#39;"
  }[ch]));
}

function showGateError(msg) {
  gateErrorEl.hidden = false;
  gateErrorEl.textContent = msg;
}

function hideGateError() {
  gateErrorEl.hidden = true;
  gateErrorEl.textContent = "";
}

function showStatus(msg) {
  statusEl.hidden = false;
  statusEl.textContent = msg;
}

function hideStatus() {
  statusEl.hidden = true;
  statusEl.textContent = "";
}
