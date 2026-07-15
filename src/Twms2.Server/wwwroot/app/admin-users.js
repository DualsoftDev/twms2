/* ============================================================================
 * 사용자 관리(Admin/UserManagement) — UserManagement.razor 의 사용자 목록 그리드를
 * 정적 페이지로 이식. GET /api/admin/users 스냅샷 1회 조회 → 목록 + KPI 렌더.
 * 원본은 조회 전용(UserService 에 생성/수정/삭제 메서드 없음)이라 읽기 전용 유지.
 * 30초 폴링 + 탭 복귀 시 갱신. 이름 검색은 클라이언트 필터.
 * ==========================================================================*/
(function () {
  'use strict';

  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const $ = (id) => document.getElementById(id);

  const S = { users: [], search: '', loaded: false };

  async function load() {
    if (!S.loaded) $('au-loading').style.display = 'block';
    try {
      const res = await fetch('/api/admin/users', { headers: { 'Accept': 'application/json' } });
      if (!res.ok) {
        if (!S.loaded) renderError(res.status);
        return;
      }
      const d = await res.json();
      S.users = d.users || [];
      S.loaded = true;
      renderKpi(d);
      renderTable();
    } catch (e) {
      if (!S.loaded) renderError();
    } finally {
      $('au-loading').style.display = 'none';
    }
  }

  function renderKpi(d) {
    const total = d.total ?? S.users.length;
    const admin = d.adminCount ?? S.users.filter(u => u.admin).length;
    $('kpi-total').textContent = total.toLocaleString();
    $('kpi-admin').textContent = admin.toLocaleString();
    $('kpi-normal').textContent = (total - admin).toLocaleString();
  }

  function renderTable() {
    const host = $('au-table');
    const q = S.search.trim().toLowerCase();
    const rows = q
      ? S.users.filter(u => (u.name || '').toLowerCase().includes(q))
      : S.users;

    if (!rows.length) {
      host.innerHTML = `<div class="au-empty">${S.users.length ? '검색 결과가 없습니다.' : '등록된 사용자가 없습니다.'}</div>`;
      return;
    }

    host.innerHTML = `<table class="nm-table"><thead><tr>
        <th style="width:80px;">ID</th>
        <th>이름</th>
        <th style="width:100px;">관리자</th>
        <th>역할</th>
        <th style="width:120px;">배정 권한</th>
      </tr></thead><tbody>
      ${rows.map(u => `<tr>
        <td><span class="au-id">${esc(u.id)}</span></td>
        <td>${esc(u.name || '-')}</td>
        <td>${u.admin
          ? `<span class="material-symbols-outlined au-admin-ico" title="관리자">admin_panel_settings</span>`
          : ''}</td>
        <td>${u.augRoles
          ? `<span class="chip chip-info">${esc(u.augRoles)}</span>`
          : `<span class="text-on-surface-variant" style="font-size:13px;">-</span>`}</td>
        <td><span class="chip ${u.permissionCount > 0 ? 'chip-info' : 'chip-default'}">${(u.permissionCount ?? 0).toLocaleString()}개</span></td>
      </tr>`).join('')}
      </tbody></table>`;
  }

  function renderError(status) {
    $('au-table').innerHTML = `<div class="au-empty">사용자 목록을 불러오지 못했습니다.${status ? ' (' + status + ')' : ''}</div>`;
  }

  document.addEventListener('DOMContentLoaded', async () => {
    if (window.Shell) await Shell.init({ active: 'admin' });

    $('au-search-input').addEventListener('input', (e) => {
      S.search = e.target.value || '';
      renderTable();
    });
    $('au-refresh').addEventListener('click', () => { load(); });

    await load();
    setInterval(() => { if (!document.hidden) load(); }, 30000);
    document.addEventListener('visibilitychange', () => { if (!document.hidden) load(); });
  });
})();
