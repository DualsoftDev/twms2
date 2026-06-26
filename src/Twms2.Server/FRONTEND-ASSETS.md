# 정적 페이지 프런트 자산 빌드 (CDN self-host)

폐쇄망/저속망 첫 로딩 지연 제거를 위해 외부 CDN 4종을 전부 로컬 번들로 교체.

## 무엇이 바뀌었나
- **Tailwind**: `cdn.tailwindcss.com` (브라우저 런타임 JIT) → 빌드타임 컴파일 `wwwroot/app/twms.css`
- **폰트**: Google Fonts / jsdelivr → `wwwroot/lib/fonts/` 로컬 번들 (`fonts.css` + `files/*.woff2`)
  - Hanken Grotesk, JetBrains Mono, Material Symbols Outlined, Pretendard(한글)
- 15개 `wwwroot/app/*.html` head 의 외부 참조 제거. 구 `tailwind-config.js` 삭제(→ `tailwind.config.js`).

결과물(`twms.css`, `lib/fonts/`)은 **커밋**한다 → 폐쇄망 배포 시 node 불필요.

## 재빌드 (인터넷 되는 개발 PC에서만)
```bash
cd src/Twms2.Server
npm install
npm run build        # 폰트 페치 + CSS 컴파일
# 또는 개별:
npm run fetch:fonts  # 폰트 재다운로드 (scripts/fetch-fonts.mjs)
npm run build:css    # Tailwind 컴파일
npm run watch:css    # 개발 중 클래스 변경 감지 재컴파일
```

## 주의
- 새 Tailwind 유틸 클래스를 HTML/JS 에 추가하면 `npm run build:css` 재실행 필요(미컴파일 시 미적용).
- `tailwind.config.js` 의 `content` 글롭이 스캔 대상(`wwwroot/app/**`, `wwwroot/js/**`).
- `node_modules/` 는 git·.NET 빌드에서 제외(`.gitignore`, csproj `Remove`).
