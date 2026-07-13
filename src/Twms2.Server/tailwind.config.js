/*
 * TWMS2.0 — Tailwind 빌드타임 컴파일 설정 (self-host).
 * 이전 wwwroot/app/tailwind-config.js (Play CDN 런타임 설정) 을 1:1 이식.
 * 색상은 전부 CSS 변수(var(--c-*)) → theme.css(:root / html.dark) 가 값 공급.
 * 컴파일: `npm run build:css` → wwwroot/app/twms.css
 */
module.exports = {
  darkMode: 'class',
  // 정적 페이지 HTML + JS 안의 유틸 클래스를 스캔 (JS 의 문자열 리터럴 포함)
  content: [
    './wwwroot/app/**/*.html',
    './wwwroot/app/**/*.js',
    './wwwroot/js/**/*.js',
  ],
  theme: {
    extend: {
      colors: {
        'background': 'var(--c-background)',
        'surface': 'var(--c-surface)',
        'surface-dim': 'var(--c-surface-dim)',
        'surface-bright': 'var(--c-surface-bright)',
        'surface-container-lowest': 'var(--c-surface-container-lowest)',
        'surface-container-low': 'var(--c-surface-container-low)',
        'surface-container': 'var(--c-surface-container)',
        'surface-container-high': 'var(--c-surface-container-high)',
        'surface-container-highest': 'var(--c-surface-container-highest)',
        'surface-variant': 'var(--c-surface-variant)',
        'on-surface': 'var(--c-on-surface)',
        'on-surface-variant': 'var(--c-on-surface-variant)',
        'on-background': 'var(--c-on-background)',
        'primary': 'var(--c-primary)',
        'on-primary': 'var(--c-on-primary)',
        'primary-container': 'var(--c-primary-container)',
        'on-primary-container': 'var(--c-on-primary-container)',
        'primary-fixed-dim': 'var(--c-primary-fixed-dim)',
        'on-primary-fixed': 'var(--c-on-primary-fixed)',
        'secondary': 'var(--c-secondary)',
        'on-secondary': 'var(--c-on-secondary)',
        'secondary-container': 'var(--c-secondary-container)',
        'on-secondary-container': 'var(--c-on-secondary-container)',
        'tertiary': 'var(--c-tertiary)',
        'tertiary-container': 'var(--c-tertiary-container)',
        'tertiary-fixed-dim': 'var(--c-tertiary-fixed-dim)',
        'error': 'var(--c-error)',
        'on-error': 'var(--c-on-error)',
        'error-container': 'var(--c-error-container)',
        'on-error-container': 'var(--c-on-error-container)',
        'outline': 'var(--c-outline)',
        'outline-variant': 'var(--c-outline-variant)',
        'inverse-on-surface': 'var(--c-inverse-on-surface)',
        'health-backedup': 'var(--health-backedup)',
        'health-unchanged': 'var(--health-unchanged)',
        'health-failed': 'var(--health-failed)',
        'health-inprogress': 'var(--health-inprogress)',
        'health-unknown': 'var(--health-unknown)',
      },
      borderRadius: { DEFAULT: '0.25rem', lg: '0.5rem', xl: '0.75rem', full: '9999px' },
      spacing: { gutter: '20px', unit: '8px', 'container-padding': '24px', 'element-gap': '16px' },
      fontFamily: {
        'body-md': ['Inter', 'Noto Sans KR', 'sans-serif'],
        'display': ['Inter', 'Noto Sans KR', 'sans-serif'],
        'label-mono': ['Inter', 'Noto Sans KR', 'ui-monospace', 'Cascadia Code', 'Consolas', 'monospace'],
        'headline-lg': ['Inter', 'Noto Sans KR', 'sans-serif'],
      },
      fontSize: {
        'body-md': ['16px', { lineHeight: '24px', fontWeight: '400' }],
        'display': ['48px', { lineHeight: '56px', letterSpacing: '-0.02em', fontWeight: '800' }],
        'label-mono': ['12px', { lineHeight: '16px', letterSpacing: '0.05em', fontWeight: '500' }],
        'headline-lg': ['32px', { lineHeight: '40px', fontWeight: '700' }],
      },
    },
  },
  plugins: [
    require('@tailwindcss/forms'),
    require('@tailwindcss/container-queries'),
  ],
}
