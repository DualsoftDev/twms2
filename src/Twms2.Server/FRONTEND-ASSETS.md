# 정적 페이지 프런트 자산 빌드 (CDN self-host)

폐쇄망/저속망 첫 로딩 지연 제거를 위해 외부 CDN 4종을 전부 로컬 번들로 교체.

## 무엇이 바뀌었나
- **Tailwind**: `cdn.tailwindcss.com` (브라우저 런타임 JIT) → 빌드타임 컴파일 `wwwroot/app/twms.css`
- **폰트**: Google Fonts / jsdelivr → `wwwroot/lib/fonts/` 로컬 번들 (`fonts.css` + `files/*.woff2`)
  - Inter(가변, 라틴), Noto Sans KR(한글 음절 전체 서브셋 woff2), Material Symbols Outlined
  - DSPilot 과 동일 스택. 구 Hanken Grotesk/JetBrains Mono/Pretendard 는 2026-07 폐기.
- 15개 `wwwroot/app/*.html` head 의 외부 참조 제거. 구 `tailwind-config.js` 삭제(→ `tailwind.config.js`).

결과물(`twms.css`, `lib/fonts/`)은 **커밋**한다 → 폐쇄망 배포 시 node 불필요.

## 재빌드 (인터넷 되는 개발 PC에서만)
```bash
cd src/Twms2.Server
npm install
npm run build        # Tailwind CSS 컴파일
npm run watch:css    # 개발 중 클래스 변경 감지 재컴파일
```

## 폰트 재생성 (거의 필요 없음)
NotoSansKR 서브셋 woff2 는 `scripts/subset-fonts.py` 로 생성한다(`npm run subset:fonts`,
요구: `pip install fonttools brotli`). 원본 TTF 는 저장소에 없으므로 Google Noto 에서 받아
`wwwroot/lib/fonts/files/` 에 놓고 실행 — 자세한 범위는 스크립트 주석 참조.

## 주의
- 새 Tailwind 유틸 클래스를 HTML/JS 에 추가하면 `npm run build:css` 재실행 필요(미컴파일 시 미적용).
- `tailwind.config.js` 의 `content` 글롭이 스캔 대상(`wwwroot/app/**`, `wwwroot/js/**`).
- `node_modules/` 는 git·.NET 빌드에서 제외(`.gitignore`, csproj `Remove`).
