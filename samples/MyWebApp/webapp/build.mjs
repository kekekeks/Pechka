import { build } from "esbuild";
import { mkdirSync, readFileSync, writeFileSync } from "fs";

mkdirSync("build", { recursive: true });
await build({
  entryPoints: ["src/index.tsx"],
  bundle: true,
  minify: true,
  sourcemap: true,
  outfile: "build/main.js",
  define: { "process.env.NODE_ENV": '"production"' },
});
const html = readFileSync("src/index.html", "utf8")
  .replace("</body>", '<script src="./main.js"></script></body>');
writeFileSync("build/index.html", html);
