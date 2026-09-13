#!/usr/bin/env node
const fs = require("fs");
const path = require("path");
const { crearBorradorGuion } = require("./guion-pipeline");

async function main() {
  const raw = fs.readFileSync(0, "utf8");
  const input = JSON.parse(raw || "{}");
  const root = path.resolve(__dirname, "../..");
  const selectPrompt = fs.readFileSync(path.join(root, "guion", "select.txt"), "utf8");
  const scriptPrompt = fs.readFileSync(path.join(root, "guion", "prompt.txt"), "utf8");
  const apiKey = process.env.OPENAI_API_KEY;
  if (!apiKey) {
    console.log(JSON.stringify({ ok: false, error: "Falta OPENAI_API_KEY." }));
    process.exit(1);
  }
  try {
    const result = await crearBorradorGuion({
      candidates: input.candidates || [],
      fecha: input.fecha,
      rango: input.rango,
      extra: input.extra,
      apiKey,
      selectPrompt,
      scriptPrompt
    });
    process.stdout.write(JSON.stringify({ ok: true, ...result }));
  } catch (err) {
    console.error(`[guion] error: ${err.message}`);
    process.stdout.write(JSON.stringify({ ok: false, error: err.message || "Falló el pipeline." }));
    process.exit(1);
  }
}

main();
