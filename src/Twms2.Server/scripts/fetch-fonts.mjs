// TWMS2.0 폰트 self-host 페처.
// Google Fonts(css2) + Pretendard(jsdelivr) CSS 를 받아 @font-face 의 woff2 를 로컬로 내려받고
// url() 을 로컬 경로로 치환 → wwwroot/lib/fonts/fonts.css 한 장 + files/*.woff2 생성.
// 실행: node scripts/fetch-fonts.mjs   (인터넷 되는 곳에서 1회. 결과물은 커밋 → 폐쇄망 배포)
import { mkdir, writeFile, rm } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const OUT_DIR = join(__dirname, '..', 'wwwroot', 'lib', 'fonts');
const FILES_DIR = join(OUT_DIR, 'files');

// 최신 Chrome UA → 구글이 woff2 + unicode-range 서브셋을 반환 (없으면 ttf 폴백)
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36';

const SOURCES = [
  {
    name: 'Hanken Grotesk + JetBrains Mono',
    url: 'https://fonts.googleapis.com/css2?family=Hanken+Grotesk:wght@400;500;700;800;900&family=JetBrains+Mono:wght@500&display=swap',
  },
  {
    name: 'Material Symbols Outlined',
    url: 'https://fonts.googleapis.com/css2?family=Material+Symbols+Outlined:opsz,wght,FILL,GRAD@20..48,100..700,0..1,-50..200&display=swap',
  },
  {
    name: 'Pretendard (Korean)',
    url: 'https://cdn.jsdelivr.net/gh/orioncactus/pretendard/dist/web/static/pretendard.css',
  },
];

// Material Symbols 아이콘 폰트 기본 클래스 (구글 css2 가 같이 주던 규칙 — theme.css 엔 없음)
const MATERIAL_BASE = `
/* Material Symbols 아이콘 폰트 기본 규칙 (구글 css2 동봉분 이식) */
.material-symbols-outlined {
  font-family: 'Material Symbols Outlined';
  font-weight: normal;
  font-style: normal;
  font-size: 24px;
  line-height: 1;
  letter-spacing: normal;
  text-transform: none;
  display: inline-block;
  white-space: nowrap;
  word-wrap: normal;
  direction: ltr;
  -webkit-font-feature-settings: 'liga';
  -webkit-font-smoothing: antialiased;
}
`;

async function fetchText(url) {
  const r = await fetch(url, { headers: { 'User-Agent': UA } });
  if (!r.ok) throw new Error(`GET ${url} → ${r.status}`);
  return await r.text();
}

async function fetchBin(url) {
  const r = await fetch(url, { headers: { 'User-Agent': UA } });
  if (!r.ok) throw new Error(`GET ${url} → ${r.status}`);
  return Buffer.from(await r.arrayBuffer());
}

function slugFamily(css) {
  const m = css.match(/font-family:\s*['"]?([^;'"]+)/i);
  return (m ? m[1] : 'font').trim().replace(/[^a-z0-9]+/gi, '-').toLowerCase();
}

async function main() {
  await rm(OUT_DIR, { recursive: true, force: true });
  await mkdir(FILES_DIR, { recursive: true });

  let combined = `/* ============================================================
 * TWMS2.0 self-host 폰트 — fetch-fonts.mjs 가 생성. 직접 수정 금지.
 * Hanken Grotesk / JetBrains Mono / Material Symbols Outlined / Pretendard
 * ========================================================== */
`;
  let counter = 0;

  for (const src of SOURCES) {
    let css = await fetchText(src.url);
    const base = new URL(src.url);

    // @font-face 블록 단위로 url() 치환 + 다운로드
    const faces = css.match(/@font-face\s*\{[^}]*\}/g) || [];
    let out = `\n/* ── ${src.name} ── */\n`;

    for (const face of faces) {
      const fam = slugFamily(face);
      let newFace = face;
      const urls = [...face.matchAll(/url\(([^)]+)\)/g)];
      for (const u of urls) {
        let raw = u[1].trim().replace(/^['"]|['"]$/g, '');
        const abs = new URL(raw, base).href;
        const ext = (abs.match(/\.(woff2|woff|ttf|otf)(\?|$)/i)?.[1] || 'woff2').toLowerCase();
        const fname = `${fam}-${counter++}.${ext}`;
        const buf = await fetchBin(abs);
        await writeFile(join(FILES_DIR, fname), buf);
        newFace = newFace.replace(u[0], `url('./files/${fname}')`);
        process.stdout.write(`  ↓ ${fname} (${(buf.length / 1024).toFixed(0)} KB)\n`);
      }
      out += newFace + '\n';
    }

    if (/material\+symbols/i.test(src.url) || /Material Symbols/i.test(src.name)) {
      out += MATERIAL_BASE;
    }
    combined += out;
  }

  await writeFile(join(OUT_DIR, 'fonts.css'), combined);
  console.log(`\n✓ ${counter} 개 폰트 파일 + fonts.css 생성 → wwwroot/lib/fonts/`);
}

main().catch((e) => { console.error('✗', e.message); process.exit(1); });
