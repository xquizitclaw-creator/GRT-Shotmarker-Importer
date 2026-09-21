// Extracts every ShotMarker target face from an archived copy of the device's web
// bundle. It runs the bundle's own init_targetfaces() rather than pattern-matching
// the source: faces carry expressions (8/INCH), helper calls (polybox, text_rings)
// and locals shared across entries, none of which survive a regex — an early regex
// pass saw 144 of them.
const fs = require("fs");
const [, , bundlePath, outPath] = process.argv;
const src = fs.readFileSync(bundlePath, "utf8");

/** Index of the brace closing the one that opens at or after `i`. */
function closeBrace(s, i) {
  let depth = 0, quote = null;
  for (; i < s.length; i++) {
    const c = s[i];
    if (quote) { if (c === "\\") i++; else if (c === quote) quote = null; continue; }
    if (c === '"' || c === "'") { quote = c; continue; }
    if (c === "{") depth++;
    else if (c === "}" && --depth === 0) return i;
  }
  throw new Error("unbalanced braces");
}

/** The full source of a named top-level function declaration. */
function fnSource(name) {
  const i = src.indexOf("function " + name + "(");
  if (i < 0) throw new Error("function not found in bundle: " + name);
  return src.slice(i, closeBrace(src, src.indexOf("{", i)) + 1);
}

const consts = src.match(/VERSION="[^"]*",FPS=[^;]*;/);
if (!consts) throw new Error("constant block not found in bundle");

const body = [
  "var " + consts[0],
  "var targetfaces = {}, targetface_categories = [];",
  ...["update_object", "polybox", "polyarc", "text_rings", "add_default_ring_text"].map(fnSource),
  fnSource("init_targetfaces"),
  "init_targetfaces();",
  "return { faces: targetfaces, categories: targetface_categories };",
].join("\n");

const { faces, categories } = new Function(body)();
console.log(`extracted ${Object.keys(faces).length} faces, ${categories.length} categories`);
fs.writeFileSync(outPath, JSON.stringify({ faces, categories }, null, 1));
