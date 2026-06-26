// 정적 페이지 head 의 외부 CDN 참조를 로컬 self-host 로 일괄 치환 (1회용).
import { readdir, readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const APP = join(__dirname, '..', 'wwwroot', 'app');

const FONTS_LINK = '  <link rel="stylesheet" href="/lib/fonts/fonts.css" />';
const TW_LINK = '  <link rel="stylesheet" href="/app/twms.css" />';

// [찾을 정확한 줄, 바꿀 내용(빈 문자열이면 줄 삭제)]
const RULES = [
  ['  <link rel="stylesheet" href="https://cdn.jsdelivr.net/gh/orioncactus/pretendard/dist/web/static/pretendard.css" />', FONTS_LINK],
  ['  <link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Hanken+Grotesk:wght@400;500;700;800;900&family=JetBrains+Mono:wght@500&display=swap" />', null],
  ['  <link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Material+Symbols+Outlined:opsz,wght,FILL,GRAD@20..48,100..700,0..1,-50..200&display=swap" />', null],
  ['  <script src="https://cdn.tailwindcss.com?plugins=forms,container-queries"></script>', TW_LINK],
  ['  <script src="/app/tailwind-config.js"></script>', null],
  ['  <!-- ⚠ Tailwind Play CDN (개발/검증용). 운영 전 self-host 컴파일로 교체. -->', '  <!-- Tailwind: 빌드타임 컴파일(twms.css) + 폰트 self-host(fonts.css). CDN 제거됨. -->'],
];

const files = (await readdir(APP)).filter((f) => f.endsWith('.html'));
let changed = 0;
for (const f of files) {
  const p = join(APP, f);
  let s = await readFile(p, 'utf8');
  const eol = s.includes('\r\n') ? '\r\n' : '\n';
  let hit = false;
  for (const [find, repl] of RULES) {
    if (repl === null) {
      if (s.includes(find + eol)) { s = s.replace(find + eol, ''); hit = true; }
      else if (s.includes(find)) { s = s.replace(find, ''); hit = true; }
    } else if (s.includes(find)) {
      s = s.replace(find, repl); hit = true;
    }
  }
  if (hit) { await writeFile(p, s); changed++; console.log(`✓ ${f}`); }
  else console.log(`- ${f} (변경 없음)`);
}
console.log(`\n${changed}/${files.length} 파일 치환 완료`);
