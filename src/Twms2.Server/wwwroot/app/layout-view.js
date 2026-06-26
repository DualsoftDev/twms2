/* ============================================================================
 * 레이아웃(도면 보기) — /layout 정적 페이지.
 * 렌더링은 공용 모듈 layout-render.js(LayoutRenderer)에 위임한다.
 * 이 파일은 셸 초기화 + 페이지 DOM(뷰포트/토글/탭/카운트)에 렌더러를 마운트만 한다.
 * (동일 렌더러를 대시보드 히어로 위젯도 재사용 — dashboard.js.)
 * ==========================================================================*/
(function () {
  'use strict';

  document.addEventListener('DOMContentLoaded', async () => {
    if (window.Shell) await Shell.init({ active: 'layout' });
    if (!window.LayoutRenderer) { console.error('layout-render.js 미로드'); return; }

    LayoutRenderer.mount({
      viewport: 'lv-viewport',
      viewmode: 'lv-viewmode',
      tabs: 'lv-layout-tabs',
      count: 'lv-count',
      zoom: true,       // 줌/팬 + 줌·전체화면 컨트롤 (blueprint-zoom.js)
      popover: true,    // 자산/라인 클릭 시 상세 팝오버
      poll: 30000,
    });
  });
})();
