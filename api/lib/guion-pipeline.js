const DEFAULT_SELECT = "gpt-5.4-nano";
const DEFAULT_SCRIPT = "gpt-5.6-luna";
const MAX_NATIONAL = 6;
const MAX_INTERNATIONAL = 5;
const MAX_CANDIDATES = 24;
const MAX_SUMMARY = 420;

const SELECT_SCHEMA = {
  name: "selected_news",
  strict: true,
  schema: {
    type: "object",
    additionalProperties: false,
    properties: {
      national: { type: "array", items: { $ref: "#/$defs/item" } },
      international: { type: "array", items: { $ref: "#/$defs/item" } }
    },
    required: ["national", "international"],
    $defs: {
      item: {
        type: "object",
        additionalProperties: false,
        properties: {
          title: { type: "string" },
          summary: { type: "string" },
          sources: {
            type: "array",
            items: {
              type: "object",
              additionalProperties: false,
              properties: {
                name: { type: "string" },
                url: { type: "string" }
              },
              required: ["name", "url"]
            }
          },
          role: { type: "string", enum: ["main", "secondary"] },
          relevanceScore: { type: "integer" },
          reason: { type: "string" }
        },
        required: ["title", "summary", "sources", "role", "relevanceScore", "reason"]
      }
    }
  }
};

function candidatesFromSemana(semana) {
  const items = Array.isArray(semana?.destacadas) ? semana.destacadas : [];
  return items.slice(0, MAX_CANDIDATES).map((item) => {
    const title = String(item.titulo || item.title || "").trim();
    const summary = String(item.resumen || item.summary || "").trim().slice(0, MAX_SUMMARY);
    const source = String(item.fuente || item.source || "").trim();
    const url = String(item.link || item.url || "").trim();
    const extras = Array.isArray(item.fuentes) ? item.fuentes : [];
    const sources = [];
    if (source || url) sources.push({ name: source || "Fuente", url });
    for (const name of extras) {
      const n = String(name || "").trim();
      if (!n || sources.some((s) => s.name === n)) continue;
      sources.push({ name: n, url: "" });
    }
    return {
      title,
      summary,
      source,
      url,
      publicationDate: String(item.fecha || item.publicationDate || ""),
      sources
    };
  }).filter((x) => x.title);
}

function compactForScript(selected) {
  const pack = (list) => (list || []).map((item) => ({
    title: item.title,
    summary: item.summary,
    role: item.role,
    sources: (item.sources || [])
      .map((s) => ({
        name: String(s.name || "").trim(),
        url: String(s.url || "").trim()
      }))
      .filter((s) => s.name)
  }));
  return {
    national: pack(selected.national),
    international: pack(selected.international)
  };
}

function normalizeList(list, max) {
  const items = (Array.isArray(list) ? list : [])
    .filter((item) => item && String(item.title || "").trim())
    .slice(0, max)
    .map((item) => ({
      title: String(item.title || "").trim(),
      summary: String(item.summary || "").trim().slice(0, MAX_SUMMARY),
      sources: Array.isArray(item.sources) ? item.sources : [],
      role: item.role === "main" ? "main" : "secondary",
      relevanceScore: Number(item.relevanceScore) || 0,
      reason: String(item.reason || "")
    }));
  if (!items.length) return items;
  const mains = items.filter((x) => x.role === "main");
  if (mains.length === 0) items[0].role = "main";
  if (mains.length > 1) {
    items.forEach((x, i) => {
      x.role = i === items.findIndex((y) => y.role === "main") ? "main" : "secondary";
    });
  }
  return items;
}

function validateSelection(raw) {
  if (!raw || typeof raw !== "object") {
    throw new Error("Nano no devolvió JSON válido.");
  }
  const national = normalizeList(raw.national, MAX_NATIONAL);
  const international = normalizeList(raw.international, MAX_INTERNATIONAL);
  if (national.length + international.length === 0) {
    throw new Error("No hay noticias suficientes para armar el episodio esta semana.");
  }
  return { national, international };
}

async function chatCompletions({ apiKey, model, messages, jsonSchema, temperature }) {
  const body = { model, messages };
  if (jsonSchema) {
    body.response_format = { type: "json_schema", json_schema: jsonSchema };
  }
  if (typeof temperature === "number") body.temperature = temperature;

  const res = await fetch("https://api.openai.com/v1/chat/completions", {
    method: "POST",
    headers: {
      Authorization: `Bearer ${apiKey}`,
      "Content-Type": "application/json"
    },
    body: JSON.stringify(body)
  });
  const json = await res.json();
  if (!res.ok) {
    throw new Error(json.error?.message || `OpenAI ${res.status} (${model})`);
  }
  let content = json.choices?.[0]?.message?.content || "";
  content = String(content).trim();
  if (content.startsWith("```")) {
    const nl = content.indexOf("\n");
    if (nl > 0) content = content.slice(nl + 1);
    if (content.endsWith("```")) content = content.slice(0, -3).trimEnd();
  }
  return content;
}

async function selectPodcastNews(candidates, { apiKey, model, prompt }) {
  if (!candidates.length) {
    throw new Error("No hay candidatos semanales. Corré primero la consolita para armar semana.json.");
  }
  const content = await chatCompletions({
    apiKey,
    model,
    jsonSchema: SELECT_SCHEMA,
    messages: [
      { role: "system", content: prompt },
      {
        role: "user",
        content: "Candidatos de la semana (ya agrupados). Seleccioná y clasificá. No escribas el guion.\n\n" +
          JSON.stringify(candidates)
      }
    ]
  });
  let parsed;
  try {
    parsed = JSON.parse(content);
  } catch {
    throw new Error("Nano devolvió un JSON inválido. No se llama a Luna.");
  }
  return validateSelection(parsed);
}

async function generatePodcastScript({ selected, fecha, rango, extra, apiKey, model, prompt }) {
  const compact = compactForScript(selected);
  let user = `Fecha: ${fecha}\nSemana: ${rango}\n\nEscribí el borrador completo en markdown, sin fences.\nDejá [OPINIÓN], [DATO] y [CTA] vacíos.\nNo inventes hechos. No pongas URLs en el texto hablado.\n\nNoticias seleccionadas:\n${JSON.stringify(compact, null, 2)}`;
  extra = String(extra || "").trim().slice(0, 4000);
  if (extra) user += `\n\nIndicaciones extra para este borrador:\n${extra}`;

  const markdown = await chatCompletions({
    apiKey,
    model,
    messages: [
      { role: "system", content: prompt },
      { role: "user", content: user }
    ]
  });
  if (!markdown) throw new Error("Luna no devolvió un borrador.");
  return markdown.endsWith("\n") ? markdown : `${markdown}\n`;
}

async function crearBorradorGuion({
  candidates,
  fecha,
  rango,
  extra,
  apiKey,
  selectPrompt,
  scriptPrompt,
  selectModel,
  scriptModel
}) {
  const modelSelect = selectModel || process.env.OPENAI_MODEL_SELECT || DEFAULT_SELECT;
  const modelScript = scriptModel || process.env.OPENAI_MODEL_SCRIPT || DEFAULT_SCRIPT;
  const list = Array.isArray(candidates) ? candidates.slice(0, MAX_CANDIDATES) : [];

  console.error(`[guion] candidatos=${list.length} select=${modelSelect} script=${modelScript}`);
  const selected = await selectPodcastNews(list, {
    apiKey,
    model: modelSelect,
    prompt: selectPrompt
  });
  console.error(
    `[guion] seleccionadas=${selected.national.length + selected.international.length}` +
    ` national=${selected.national.length} international=${selected.international.length}`
  );

  const markdown = await generatePodcastScript({
    selected,
    fecha,
    rango,
    extra,
    apiKey,
    model: modelScript,
    prompt: scriptPrompt
  });
  return {
    markdown,
    stats: {
      candidates: list.length,
      national: selected.national.length,
      international: selected.international.length,
      selectModel: modelSelect,
      scriptModel: modelScript
    }
  };
}

module.exports = {
  candidatesFromSemana,
  compactForScript,
  validateSelection,
  selectPodcastNews,
  generatePodcastScript,
  crearBorradorGuion
};
