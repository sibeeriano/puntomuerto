const STORAGE_KEY = "pm-podcast-clave";
const logoutEl = document.getElementById("cerrar-sesion");
const loginEl = document.getElementById("admin-login");
const gateEl = document.getElementById("gate");
const claveEl = document.getElementById("clave");
const gateErrorEl = document.getElementById("gate-error");
const studioEl = document.getElementById("studio");
const semanaFiltroEl = document.getElementById("semana-filtro");
const vistaEl = document.getElementById("accion-vista");
const nuevoEl = document.getElementById("accion-nuevo");
const guardarEl = document.getElementById("accion-guardar");
const crearPanelEl = document.getElementById("crear-panel");
const crearPasoClaveEl = document.getElementById("crear-paso-clave");
const crearPasoExtraEl = document.getElementById("crear-paso-extra");
const crearClaveEl = document.getElementById("crear-clave");
const crearExtraEl = document.getElementById("crear-extra");
const crearErrorEl = document.getElementById("crear-error");
const crearEnviarEl = document.getElementById("crear-enviar");
const statusEl = document.getElementById("status");
const guionEl = document.getElementById("guion");

let claveCrear = "";
let index = [];
let clave = sessionStorage.getItem(STORAGE_KEY) || "";
let vista = "esqueleto";
let borradorNuevo = null;

init();

async function init() {
  try {
    await reloadIndex();
  } catch (err) {
    showGateError("No se pudieron cargar los borradores. Abrí el sitio por HTTP.");
    console.error(err);
    return;
  }

  gateEl.addEventListener("submit", async (ev) => {
    ev.preventDefault();
    await unlock(claveEl.value);
  });

  semanaFiltroEl.addEventListener("change", () => {
    if (borradorNuevo && semanaFiltroEl.value === borradorNuevo.fecha) {
      vista = "guion";
      mostrarBorradorNuevo();
      return;
    }
    vista = "esqueleto";
    if (clave) loadSemana();
  });

  vistaEl.addEventListener("click", onVista);
  nuevoEl.addEventListener("click", abrirCrear);
  guardarEl.addEventListener("click", onGuardar);
  crearPanelEl.addEventListener("submit", onCrearClave);
  crearEnviarEl.addEventListener("click", onCrearEnviar);
  document.getElementById("crear-volver").addEventListener("click", cerrarCrear);
  document.getElementById("crear-cerrar").addEventListener("click", cerrarCrear);
  if (logoutEl) logoutEl.addEventListener("click", logout);

  if (clave) {
    const ok = await unlock(clave, true);
    if (!ok) clave = "";
  }
}

function fechaHoy() {
  const d = new Date();
  const y = d.getFullYear();
  const m = String(d.getMonth() + 1).padStart(2, "0");
  const day = String(d.getDate()).padStart(2, "0");
  return `${y}-${m}-${day}`;
}

function etiquetaFecha(fecha, rango) {
  const d = new Date(`${fecha}T12:00:00`);
  if (Number.isNaN(d.getTime())) return rango || fecha;
  const text = d.toLocaleDateString("es-AR", {
    weekday: "long",
    day: "numeric",
    month: "long",
    year: "numeric"
  }).replace(",", "");
  return text.charAt(0).toUpperCase() + text.slice(1);
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
  throw new Error("Todavía no hay borrador GPT para esa fecha.");
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
  if (loginEl) loginEl.hidden = true;
  gateEl.hidden = true;
  studioEl.hidden = false;
  syncLogout();
  renderSemanas();
  await loadSemana();
  return true;
}

function syncLogout() {
  if (logoutEl) logoutEl.hidden = !clave;
}

function logout() {
  clave = "";
  claveCrear = "";
  borradorNuevo = null;
  vista = "esqueleto";
  sessionStorage.removeItem(STORAGE_KEY);
  const stale = [];
  for (let i = 0; i < sessionStorage.length; i += 1) {
    const key = sessionStorage.key(i);
    if (key && key.startsWith("pm-guion-")) stale.push(key);
  }
  stale.forEach((key) => sessionStorage.removeItem(key));
  cerrarCrear();
  studioEl.hidden = true;
  if (loginEl) loginEl.hidden = false;
  gateEl.hidden = false;
  guionEl.replaceChildren();
  hideStatus();
  hideGateError();
  claveEl.value = "";
  syncLogout();
  claveEl.focus();
}

function renderSemanas() {
  const selected = semanaFiltroEl.value;
  semanaFiltroEl.replaceChildren();
  for (const item of index) {
    const option = document.createElement("option");
    option.value = item.fecha;
    option.textContent = etiquetaFecha(item.fecha, item.rango);
    semanaFiltroEl.append(option);
  }
  if (selected && index.some((item) => item.fecha === selected)) {
    semanaFiltroEl.value = selected;
  }
}

function syncBoton() {
  const item = itemActual();
  const hayPendiente = Boolean(borradorNuevo);
  const tieneGuion = Boolean(item?.guion) && !hayPendiente;
  vistaEl.hidden = !tieneGuion;
  vistaEl.disabled = false;
  vistaEl.textContent = vista === "guion" ? "Ver esqueleto" : "Ver guion";
  nuevoEl.hidden = !index.length;
  nuevoEl.disabled = false;
  guardarEl.hidden = !hayPendiente;
  guardarEl.disabled = false;
  if (hayPendiente) guardarEl.textContent = "Guardar";
}

function mostrarBorradorNuevo() {
  hideStatus();
  showStatus(`Borrador del ${etiquetaFecha(borradorNuevo.fecha)}. Tocá Guardar para sumarlo a las fechas.`);
  guionEl.innerHTML = renderMarkdown(borradorNuevo.markdown);
  syncBoton();
}

async function loadSemana() {
  if (borradorNuevo && semanaFiltroEl.value === borradorNuevo.fecha) {
    mostrarBorradorNuevo();
    return;
  }
  hideStatus();
  guionEl.replaceChildren();
  const item = itemActual();
  if (!item) {
    showStatus("No hay fechas publicadas.");
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

async function onVista() {
  const item = itemActual();
  if (!item?.guion) return;
  vista = vista === "guion" ? "esqueleto" : "guion";
  await loadSemana();
}

function abrirCrear() {
  if (!index.length) return;
  claveCrear = "";
  crearClaveEl.value = "";
  crearExtraEl.value = "";
  hideCrearError();
  crearPasoClaveEl.hidden = false;
  crearPasoExtraEl.hidden = true;
  crearPanelEl.hidden = false;
  crearClaveEl.focus();
}

function cerrarCrear() {
  crearPanelEl.hidden = true;
  claveCrear = "";
  crearClaveEl.value = "";
  crearExtraEl.value = "";
  hideCrearError();
  crearEnviarEl.disabled = false;
  crearEnviarEl.textContent = "Crear con GPT";
}

async function onCrearClave(ev) {
  ev.preventDefault();
  hideCrearError();
  const password = crearClaveEl.value;
  const item = index[0];
  if (!item) return;
  try {
    await decryptArchivo(archivoEsqueleto(item), password);
  } catch (err) {
    showCrearError("Contraseña incorrecta.");
    console.warn(err);
    return;
  }
  claveCrear = password;
  crearPasoClaveEl.hidden = true;
  crearPasoExtraEl.hidden = false;
  crearExtraEl.focus();
}

async function onCrearEnviar() {
  if (!claveCrear) return;
  const fecha = fechaHoy();
  crearEnviarEl.disabled = true;
  crearEnviarEl.textContent = "Creando…";
  hideCrearError();
  hideStatus();
  try {
    const res = await fetch("/api/crear-guion", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        fecha,
        password: claveCrear,
        extra: crearExtraEl.value.trim(),
        nuevo: true
      })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok || !data.ok) {
      throw new Error(data.error || "No se pudo crear el borrador.");
    }
    if (!data.markdown) {
      throw new Error("GPT no devolvió texto.");
    }
    clave = claveCrear;
    sessionStorage.setItem(STORAGE_KEY, claveCrear);
    syncLogout();
    sessionStorage.setItem(`pm-guion-${fecha}`, data.markdown);
    borradorNuevo = { fecha, markdown: data.markdown };
    cerrarCrear();
    vista = "guion";
    mostrarBorradorNuevo();
  } catch (err) {
    showCrearError(err.message || "No se pudo crear el borrador.");
    crearEnviarEl.disabled = false;
    crearEnviarEl.textContent = "Crear con GPT";
  }
}

async function onGuardar() {
  if (!borradorNuevo || !clave) return;
  guardarEl.disabled = true;
  guardarEl.textContent = "Guardando…";
  hideStatus();
  try {
    const res = await fetch("/api/crear-guion", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        accion: "guardar",
        fecha: borradorNuevo.fecha,
        password: clave,
        markdown: borradorNuevo.markdown,
        rango: etiquetaFecha(borradorNuevo.fecha)
      })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok || !data.ok) {
      throw new Error(data.error || "No se pudo guardar.");
    }
    const fecha = borradorNuevo.fecha;
    borradorNuevo = null;
    try {
      await reloadIndex();
    } catch {
      if (data.item) {
        index = [data.item, ...index.filter((x) => x.fecha !== data.item.fecha)];
      }
    }
    renderSemanas();
    semanaFiltroEl.value = fecha;
    vista = "guion";
    await loadSemana();
  } catch (err) {
    showStatus(err.message || "No se pudo guardar.");
    guardarEl.disabled = false;
    guardarEl.textContent = "Guardar";
  }
}

function showCrearError(msg) {
  crearErrorEl.hidden = false;
  crearErrorEl.textContent = msg;
}

function hideCrearError() {
  crearErrorEl.hidden = true;
  crearErrorEl.textContent = "";
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
