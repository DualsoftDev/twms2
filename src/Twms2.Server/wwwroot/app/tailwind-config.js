/*
 * TWMS2.0 정적 페이지 — 공통 Tailwind(Play CDN) 설정.
 * 색상 토큰은 전부 CSS 변수(var(--c-*))로 매핑 → theme.css 의 :root / html.dark 가
 * 값을 공급하므로 .dark 토글만으로 라이트/다크가 즉시 전환된다(뉴모피즘 그림자 포함).
 * ⚠ Play CDN 은 개발/검증용. 운영 배포 전 `tailwindcss` 컴파일(self-host)로 교체할 것.
 */
tailwind.config = {
  darkMode: 'class',
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
        // 백업 상태 색 (LayoutHelpers.GetHealthColor 와 동일)
        'health-backedup': 'var(--health-backedup)',
        'health-unchanged': 'var(--health-unchanged)',
        'health-failed': 'var(--health-failed)',
        'health-inprogress': 'var(--health-inprogress)',
        'health-unknown': 'var(--health-unknown)',
      },
      borderRadius: { DEFAULT: '0.25rem', lg: '0.5rem', xl: '0.75rem', full: '9999px' },
      spacing: { gutter: '20px', unit: '8px', 'container-padding': '24px', 'element-gap': '16px' },
      fontFamily: {
        'body-md': ['Hanken Grotesk', 'Pretendard', 'sans-serif'],
        'display': ['Hanken Grotesk', 'Pretendard', 'sans-serif'],
        'label-mono': ['JetBrains Mono', 'monospace'],
        'headline-lg': ['Hanken Grotesk', 'Pretendard', 'sans-serif'],
      },
      fontSize: {
        'body-md': ['16px', { lineHeight: '24px', fontWeight: '400' }],
        'display': ['48px', { lineHeight: '56px', letterSpacing: '-0.02em', fontWeight: '800' }],
        'label-mono': ['12px', { lineHeight: '16px', letterSpacing: '0.05em', fontWeight: '500' }],
        'headline-lg': ['32px', { lineHeight: '40px', fontWeight: '700' }],
      },
    },
  },
}
