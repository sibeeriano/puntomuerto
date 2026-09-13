const crypto = require("crypto");
const { candidatesFromSemana, crearBorradorGuion } = require("./lib/guion-pipeline");

const ITERATIONS = 120000;
const KEY_LEN = 32;

module.exports = async (req, res) => {
  if (req.method !== "POST") {
    res.status(405).json({ ok: false, error: "Usá POST." });
    return;
  }

  const password = String(req.body?.password || "");
  const fecha = String(req.body?.fecha || "").trim();
  const extra = String(req.body?.extra || "").trim().slice(0, 4000);
  const markdownIn = String(req.body?.markdown || "");
  const rangoIn = String(req.body?.rango || "").trim();
  const accion = String(req.body?.accion || "crear");
  const nuevo = Boolean(req.body?.nuevo);
  const expected = process.env.PUNTO_PODCAST_PASSWORD || "";
  if (!expected || password !== expected) {
    res.status(401).json({ ok: false, error: "Contraseña incorrecta." });
    return;
  }
  if (!/^\d{4}-\d{2}-\d{2}$/.test(fecha)) {
    res.status(400).json({ ok: false, error: "Fecha inválida." });
    return;
  }

  const repo = process.env.GITHUB_REPO || "sibeeriano/puntomuerto";
  const token = process.env.GITHUB_TOKEN;
  const apiKey = process.env.OPENAI_API_KEY;
  if (!token) {
    res.status(500).json({ ok: false, error: "Falta GITHUB_TOKEN en Vercel." });
    return;
  }

  try {
    const indexFile = await ghGet(repo, token, "docs/guiones.json");
    if (!indexFile) {
      res.status(404).json({ ok: false, error: "No hay índice de guiones." });
      return;
    }
    const lista = JSON.parse(Buffer.from(indexFile.content, "base64").toString("utf8"));
    const fuente =
      lista.find((x) => x.fecha === fecha && x.esqueleto) ||
      [...lista].filter((x) => x.esqueleto).sort((a, b) => b.fecha.localeCompare(a.fecha))[0];
    if (!fuente || !fuente.esqueleto) {
      res.status(404).json({ ok: false, error: "No hay esqueleto publicado." });
      return;
    }

    if (accion === "guardar") {
      if (!markdownIn.trim()) {
        res.status(400).json({ ok: false, error: "No hay texto para guardar." });
        return;
      }
      const rel = `guiones/${fecha}-guion.json`;
      const packed = encryptGuion(markdownIn, password);
      await ghPut(
        repo,
        token,
        `docs/${rel}`,
        JSON.stringify(packed, null, 2) + "\n",
        `chore: guardar borrador ${fecha}`
      );
      const prev = lista.find((x) => x.fecha === fecha);
      const item = {
        fecha,
        rango: rangoIn || prev?.rango || fecha,
        esqueleto: prev?.esqueleto || fuente.esqueleto,
        guion: rel
      };
      const next = [item, ...lista.filter((x) => x.fecha !== fecha)].sort((a, b) =>
        b.fecha.localeCompare(a.fecha)
      );
      await ghPut(
        repo,
        token,
        "docs/guiones.json",
        JSON.stringify(next, null, 2) + "\n",
        `chore: índice ${fecha}`,
        indexFile.sha
      );
      res.status(200).json({ ok: true, item, guion: rel });
      return;
    }

    if (!nuevo) {
      const item = lista.find((x) => x.fecha === fecha);
      if (item?.guion) {
        const existente = await ghGet(repo, token, `docs/${item.guion}`);
        if (existente) {
          const packed = JSON.parse(Buffer.from(existente.content, "base64").toString("utf8"));
          const markdown = decryptGuion(packed, password);
          res.status(200).json({ ok: true, already: true, guion: item.guion, markdown });
          return;
        }
      }
    }

    if (!apiKey) {
      res.status(500).json({ ok: false, error: "Falta OPENAI_API_KEY en Vercel." });
      return;
    }

    const semanaFile = await ghGet(repo, token, "docs/semana.json");
    if (!semanaFile) {
      res.status(404).json({ ok: false, error: "No hay semana.json. Primero actualizá las noticias." });
      return;
    }
    const semana = JSON.parse(Buffer.from(semanaFile.content, "base64").toString("utf8"));
    const candidates = candidatesFromSemana(semana);
    const selectPrompt = await readRepoText(repo, token, "guion/select.txt");
    const scriptPrompt = await readRepoText(repo, token, "guion/prompt.txt");
    if (!selectPrompt || !scriptPrompt) {
      res.status(500).json({ ok: false, error: "Faltan los prompts en guion/." });
      return;
    }
    const rango = rangoIn || (semana.desde && semana.hasta
      ? `${semana.desde} a ${semana.hasta}`
      : fecha);
    const { markdown } = await crearBorradorGuion({
      candidates,
      fecha,
      rango,
      extra,
      apiKey,
      selectPrompt,
      scriptPrompt
    });

    if (nuevo) {
      res.status(200).json({ ok: true, pendiente: true, fecha, markdown });
      return;
    }

    const rel = `guiones/${fecha}-guion.json`;
    const packed = encryptGuion(markdown, password);
    await ghPut(
      repo,
      token,
      `docs/${rel}`,
      JSON.stringify(packed, null, 2) + "\n",
      `chore: guion GPT ${fecha}`
    );
    const next = lista.map((x) =>
      x.fecha === fecha ? { ...x, rango: x.rango, esqueleto: x.esqueleto, guion: rel } : x
    );
    await ghPut(
      repo,
      token,
      "docs/guiones.json",
      JSON.stringify(next, null, 2) + "\n",
      `chore: índice guion ${fecha}`,
      indexFile.sha
    );
    res.status(200).json({ ok: true, already: false, guion: rel, markdown });
  } catch (err) {
    res.status(502).json({ ok: false, error: err.message || "No se pudo crear el borrador." });
  }
};

function decryptGuion(packed, password) {
  const salt = Buffer.from(packed.salt, "base64");
  const iv = Buffer.from(packed.iv, "base64");
  const data = Buffer.from(packed.ct, "base64");
  const key = crypto.pbkdf2Sync(password, salt, ITERATIONS, KEY_LEN, "sha256");
  const tag = data.subarray(data.length - 16);
  const ciphertext = data.subarray(0, data.length - 16);
  const decipher = crypto.createDecipheriv("aes-256-gcm", key, iv);
  decipher.setAuthTag(tag);
  return Buffer.concat([decipher.update(ciphertext), decipher.final()]).toString("utf8");
}

function encryptGuion(plaintext, password) {
  const salt = crypto.randomBytes(16);
  const iv = crypto.randomBytes(12);
  const key = crypto.pbkdf2Sync(password, salt, ITERATIONS, KEY_LEN, "sha256");
  const cipher = crypto.createCipheriv("aes-256-gcm", key, iv);
  const enc = Buffer.concat([cipher.update(plaintext, "utf8"), cipher.final()]);
  const tag = cipher.getAuthTag();
  return {
    v: 1,
    salt: salt.toString("base64"),
    iv: iv.toString("base64"),
    ct: Buffer.concat([enc, tag]).toString("base64")
  };
}

async function readRepoText(repo, token, path) {
  const file = await ghGet(repo, token, path);
  if (!file) return "";
  return Buffer.from(file.content, "base64").toString("utf8");
}

async function ghGet(repo, token, path) {
  const res = await fetch(`https://api.github.com/repos/${repo}/contents/${path}`, {
    headers: ghHeaders(token)
  });
  if (res.status === 404) return null;
  const data = await res.json();
  if (!res.ok) throw new Error(data.message || `GitHub GET ${res.status}`);
  return data;
}

async function ghPut(repo, token, path, text, message, sha) {
  const current = sha ? { sha } : await ghGet(repo, token, path);
  const res = await fetch(`https://api.github.com/repos/${repo}/contents/${path}`, {
    method: "PUT",
    headers: { ...ghHeaders(token), "Content-Type": "application/json" },
    body: JSON.stringify({
      message,
      content: Buffer.from(text, "utf8").toString("base64"),
      branch: "main",
      sha: current?.sha
    })
  });
  const data = await res.json();
  if (!res.ok) throw new Error(data.message || `GitHub PUT ${res.status}`);
  return data;
}

function ghHeaders(token) {
  return {
    Authorization: `Bearer ${token}`,
    Accept: "application/vnd.github+json",
    "User-Agent": "puntomuerto"
  };
}
