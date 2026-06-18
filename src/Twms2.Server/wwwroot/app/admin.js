/* ============================================================================
 * 관리 허브(Admin) — AdminIndex.razor 의 네비게이션 타일을 정적 페이지로 이식.
 * 라이브 데이터 없음(순수 네비게이션) → Shell.init 로 공통 셸만 구성.
 * 각 타일은 기존 Blazor 관리 하위 페이지(/assets/table, /admin/layout,
 * /settings, /admin/config, /admin/database)로 그대로 이동한다.
 * ==========================================================================*/
(function () {
  'use strict';

  document.addEventListener('DOMContentLoaded', async () => {
    if (window.Shell) await Shell.init({ active: 'admin' });
  });
})();
