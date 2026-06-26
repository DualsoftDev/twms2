/* ============================================================================
 * 자산 상세(AssetDetail) — /assets/{id} 및 /qr/{id} 정적 페이지.
 * URL pathname 에서 정수 id 추출 → GET /api/assets/{id}.
 * AssetDetail.razor 의 읽기 전용 섹션(기본정보/연결정보/설명/상태/매뉴얼/
 * 백업이력/관련링크)을 형태 유지하여 재현. 편집은 Blazor 페이지에서 수행(아래 deviation).
 * 30초 폴링 + 탭 복귀 시 갱신.
 * ==========================================================================*/
(function () {
  'use strict';

  const RESULT = {
    backedup:   { label: '백업 갱신', chip: 'chip-success', icon: 'check_circle' },
    unchanged:  { label: '변경 없음', chip: 'chip-info',    icon: 'remove' },
    failed:     { label: '작업 실패', chip: 'chip-error',   icon: 'error' },
    incomplete: { label: '미완료',    chip: 'chip-error',   icon: 'hourglass_disabled' },
    inprogress: { label: '작업중',    chip: 'chip-warning', icon: 'hourglass_top' },
  };
  const HEALTH = {
    backedup:   { label: '백업 갱신', chip: 'chip-success', icon: 'check_circle' },
    unchanged:  { label: '변경 없음', chip: 'chip-info',    icon: 'remove' },
    failed:     { label: '작업 실패', chip: 'chip-error',   icon: 'error' },
    inprogress: { label: '작업중',    chip: 'chip-warning', icon: 'hourglass_top' },
    unknown:    { label: '내역 없음', chip: 'chip-default', icon: 'help' },
  };

  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const $ = (id) => document.getElementById(id);

  // 타입명 → 아이콘 파일 (asset-explorer.typeIcon 과 동일 규칙)
  function typeIcon(a) {
    const t = (a.typeName || '').toLowerCase();
    if (t.includes('robot')) return 'robot.png';
    if (t.includes('plc') && a.isRobotPlc) return 'robot.png';
    if (t.includes('plc')) return 'plc.png';
    if (t.includes('servo')) return 'servo.png';
    if (t.includes('drive')) return 'drive.png';
    if (t.includes('hmi') || t.includes('xp')) return 'hmi.png';
    if (t.includes('ftp')) return 'ftp.png';
    return '';
  }

  let ASSET_ID = null;
  // 이 자산의 IP 를 경유 IP(via)로 사용하는 다른 자산 목록 (이 자산을 게이트웨이로 연결).
  let VIA_ASSETS = [];

  function readId() {
    // /assets/123 또는 /qr/123 → 123
    const m = location.pathname.match(/\/(?:assets|qr)\/(\d+)/);
    return m ? parseInt(m[1], 10) : null;
  }

  function fmtDateTime(s) {
    if (!s) return '-';
    const d = new Date(s); if (isNaN(d)) return s;
    const p = (n) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
  }
  function fmtMinute(s) {
    if (!s) return '-';
    const d = new Date(s); if (isNaN(d)) return s;
    const p = (n) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
  }
  function fmtTime(s) {
    if (!s) return '';
    const d = new Date(s); if (isNaN(d)) return '';
    const p = (n) => String(n).padStart(2, '0');
    return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
  }

  async function load() {
    if (ASSET_ID == null) {
      $('ad-root').innerHTML = `<div class="ad-empty">잘못된 자산 주소입니다.</div>`;
      return;
    }
    try {
      const res = await fetch(`/api/assets/${ASSET_ID}`, { headers: { 'Accept': 'application/json' } });
      if (res.status === 404) {
        $('ad-root').innerHTML = `<div class="ad-empty">자산을 찾을 수 없습니다. (#${ASSET_ID})</div>`;
        return;
      }
      if (!res.ok) return;
      const d = await res.json();
      await loadViaAssets(d);
      render(d);
    } catch (e) { /* 무시 */ }
  }

  // 이 자산의 IP 를 경유 IP 로 갖는 다른 자산을 통합 목록(/api/assets)에서 추려낸다.
  // (해당 자산들은 이 자산을 게이트웨이로 거쳐 연결됨) — IP 가 없으면 비교 불가.
  async function loadViaAssets(d) {
    VIA_ASSETS = [];
    const myIp = d.ip == null ? '' : String(d.ip).trim();
    if (!myIp) return;
    try {
      const res = await fetch('/api/assets', { headers: { 'Accept': 'application/json' } });
      if (!res.ok) return;
      const data = await res.json();
      VIA_ASSETS = (data.assets || []).filter(a =>
        a.assetId !== d.assetId &&
        a.ipVia != null && String(a.ipVia).trim() === myIp);
    } catch (e) { /* 무시 */ }
  }

  // 정보 필드 (라벨 + 인셋 값). 값이 비어있으면 옵션에 따라 생략.
  function field(label, value, opts) {
    opts = opts || {};
    const empty = value == null || value === '';
    if (empty && opts.hideEmpty) return '';
    const shown = empty ? (opts.dash || '-') : value;
    return `<div class="ad-field"><div class="ad-field-label">${esc(label)}</div><div class="ad-field-value">${esc(shown)}</div></div>`;
  }

  function basicInfo(d) {
    const rows = [];
    rows.push(field('자산명', d.name));
    rows.push(field('자산 타입', d.typeName));
    rows.push(field('라인', d.lineName, { hideEmpty: true }));
    rows.push(field('제조사', d.vendor, { hideEmpty: true }));
    rows.push(field('사양', d.spec, { hideEmpty: true }));
    rows.push(field('에이전트', d.agent, { hideEmpty: true }));
    return rows.join('');
  }

  // 연결 정보 — AssetDetail.razor NotAuthorized 분기의 조건부 표시 이식.
  function connInfo(d) {
    const rows = [];
    const isDrive = d.typeId === 4;
    // 경유 연결 표시 조건 (Drive는 ViaEnabled=true 일 때만)
    const showVia = (d.ipVia != null && d.ipVia !== '') && (!isDrive || d.viaEnabled === true);

    if (isDrive && d.viaEnabled === true) {
      rows.push(`<div class="ad-field"><div class="ad-field-value" style="text-align:center;color:var(--health-unchanged);font-weight:700;">경유 연결 (2단)</div></div>`);
    }
    if (showVia) rows.push(field('경유 IP', d.ipVia));
    rows.push(field('IP', d.ip));
    if (showVia) {
      rows.push(`<div class="ad-row2">
        ${field('Base', d.baseNumber == null ? '-' : d.baseNumber)}
        ${field('Slot', d.slotNumber == null ? '-' : d.slotNumber)}
      </div>`);
    }
    if (d.stationNumber != null) rows.push(field('Station', d.stationNumber));
    if ((d.modelName != null && d.modelName !== '') || (d.modelVersion != null && d.modelVersion !== '')) {
      rows.push(`<div class="ad-row2">
        ${field('모델명', d.modelName || '-')}
        ${field('버전', d.modelVersion || '-')}
      </div>`);
    }
    if (d.typeId === 6) rows.push(field('로봇 PLC', d.isRobotPlc ? 'Yes' : 'No'));
    return rows.join('');
  }

  function manualBlock(d) {
    const matched = d.matchedManuals || [];
    const all = d.allManuals || [];
    let html = '';
    if (matched.length > 0) {
      html += `<div class="ad-section-title"><span class="material-symbols-outlined">menu_book</span>매뉴얼</div>`;
      html += matched.map(m => manualRow(m, true)).join('');
      html += `<div class="ad-divider"></div>`;
    }
    if (all.length > 0) {
      html += `<details class="ad-manuals-all">
        <summary><span class="material-symbols-outlined">folder_open</span>전체 매뉴얼 목록 <span class="chip chip-default">${all.length}</span></summary>
        ${all.map(m => manualRow(m, false)).join('')}
      </details>`;
    }
    return html;
  }
  function manualRow(m, primary) {
    const chipCls = primary ? 'chip-success' : 'chip-default';
    return `<div class="ad-manual-row">
      <span class="chip ${chipCls}">${esc(m.keyword)}</span>
      <span class="ad-manual-name">${esc(m.fileName)}</span>
      <a class="ad-btn" href="/manuals/${encodeURIComponent(m.storedFileName)}" target="_blank">
        <span class="material-symbols-outlined">picture_as_pdf</span>보기
      </a>
    </div>`;
  }

  function backupTable(d) {
    const rows = d.backupHistory || [];
    if (rows.length === 0) return `<div class="ad-empty">백업 이력이 없습니다.</div>`;
    const body = rows.map(a => {
      const r = RESULT[a.result] || RESULT.unchanged;
      let verCell = a.version == null ? '-' : esc(a.version);
      if (a.result === 'backedup' && a.version > 1)
        verCell += ` <span class="chip chip-ghost" style="border-color:var(--health-backedup);color:var(--health-backedup);">${a.version - 1} → ${a.version}</span>`;
      const resultChip = `<span class="chip ${r.chip}"><span class="material-symbols-outlined">${r.icon}</span>${r.label}</span>`;
      let dl = '';
      if (a.downloadableVersion != null && !a.isInProgress) {
        let color = 'var(--health-unchanged)', title = `현재 백업 v${a.version} 다운로드`;
        if (a.result === 'backedup') { color = 'var(--health-backedup)'; title = `v${a.version} 새 백업 다운로드`; }
        else if (!a.isSuccess) { color = 'var(--health-failed)'; title = `마지막 성공 백업 v${a.downloadableVersion} 다운로드`; }
        dl = `<a class="ad-iconbtn" href="/api/download/backup/${ASSET_ID}/${a.downloadableVersion}" target="_blank" title="${title}" style="color:${color};"><span class="material-symbols-outlined">download</span></a>`;
      }
      const report = a.hasReport
        ? `<a class="ad-iconbtn" href="/report/${ASSET_ID}/${a.version}/index.html" target="_blank" title="리포트 보기"><span class="material-symbols-outlined">open_in_new</span></a>` : '';
      return `<tr>
        <td>${verCell}</td>
        <td>${fmtDateTime(a.started)}</td>
        <td>${fmtDateTime(a.finished)}</td>
        <td>${resultChip}</td>
        <td>${dl}</td>
        <td>${report}</td>
      </tr>`;
    }).join('');
    return `<div class="ad-table-wrap"><table class="nm-table">
      <thead><tr><th>버전</th><th>작업 시작</th><th>작업 종료</th><th>결과</th><th>다운로드</th><th>리포트</th></tr></thead>
      <tbody>${body}</tbody>
    </table></div>`;
  }

  function render(d) {
    document.title = `${d.name || ('자산 #' + d.assetId)} — TWMS`;
    const h = HEALTH[d.health] || HEALTH.unknown;

    // 헤더 아이콘
    const iconHtml = d.iconName
      ? `<img src="/images/icons/${esc(d.iconName)}" alt="" />`
      : `<span class="material-symbols-outlined">devices</span>`;

    // 헤더 칩 (health + ping)
    const healthChip = `<span class="chip ${h.chip}"><span class="material-symbols-outlined">${h.icon}</span>${esc(d.healthLabel || h.label)}</span>`;
    let pingChip = '';
    if (d.pingReachable != null) {
      pingChip = d.pingReachable
        ? `<span class="chip chip-success" title="${fmtTime(d.pingCheckedAt)}"><span class="material-symbols-outlined">wifi</span>온라인</span>`
        : `<span class="chip chip-error" title="${fmtTime(d.pingCheckedAt)}"><span class="material-symbols-outlined">wifi_off</span>오프라인</span>`;
    }
    const agentChip = d.agentOnline
      ? `<span class="chip chip-success"><span class="material-symbols-outlined">cloud</span>에이전트${d.agentName ? ' · ' + esc(d.agentName) : ''}</span>`
      : `<span class="chip chip-default"><span class="material-symbols-outlined">cloud_off</span>에이전트 오프라인</span>`;

    // 배지(마지막 변경 / 최신 버전) + 다운로드/백업정보 버튼
    const badges = [];
    if (d.lastBackupChangedTime)
      badges.push(`<span class="chip chip-ghost"><span class="material-symbols-outlined">schedule</span>${fmtMinute(d.lastBackupChangedTime)}</span>`);
    if (d.latestVersion != null)
      badges.push(`<span class="chip chip-ghost" style="border-color:var(--health-unchanged);color:var(--health-unchanged);">v${d.latestVersion}</span>`);

    const dlBtn = d.latestVersion != null
      ? `<a class="ad-btn" href="/api/download/backup/${ASSET_ID}/${d.latestVersion}" target="_blank"><span class="material-symbols-outlined">download</span>다운로드</a>`
      : `<button class="ad-btn" disabled style="opacity:0.45;cursor:not-allowed;"><span class="material-symbols-outlined">download</span>다운로드</button>`;

    // 이 자산을 경유 게이트웨이로 사용하는 자산이 있으면 다이얼로그 진입 버튼 노출.
    const viaBtn = VIA_ASSETS.length > 0
      ? `<button class="ad-btn" id="ad-via-btn" style="color:var(--c-primary);"><span class="material-symbols-outlined">hub</span>경유 자산 ${VIA_ASSETS.length}</button>`
      : '';

    // 관련 링크 (백업 이력 — 이름 검색으로 통합조회 진입; AssetDetail.GoToBackupHistory 이식)
    const nameQ = encodeURIComponent(d.name || '');
    const today = (() => { const t = new Date(); const p = (n) => String(n).padStart(2, '0'); return `${t.getFullYear()}-${p(t.getMonth() + 1)}-${p(t.getDate())}`; })();
    const histUrl = `/history?tab=1&q=${nameQ}&mode=contains&start=2000-01-01&end=${today}`;

    $('ad-root').innerHTML = `
      <div class="ad-header">
        <div class="ad-icon">${iconHtml}</div>
        <div style="flex:1;min-width:0;">
          <div class="ad-chips">
            <span class="ad-title">${esc(d.name)}</span>
            ${healthChip}${pingChip}
          </div>
          <div class="ad-subtitle">${esc(d.typeName || '')} | ID: ${d.assetId}</div>
        </div>
      </div>

      <div class="ad-actions">
        ${badges.join('')}
        ${viaBtn}
        ${dlBtn}
        <a class="ad-btn" href="${histUrl}"><span class="material-symbols-outlined">history</span>백업 정보</a>
      </div>

      <div class="ad-divider"></div>

      ${manualBlock(d)}

      <div class="ad-grid2">
        <div>
          <div class="ad-section-title"><span class="material-symbols-outlined">info</span>자산 정보</div>
          ${basicInfo(d)}
          <div style="margin-top:8px;">${agentChip}</div>
        </div>
        <div>
          <div class="ad-section-title"><span class="material-symbols-outlined">lan</span>연결 정보</div>
          ${connInfo(d)}
        </div>
      </div>

      ${d.description ? `<div style="margin-top:8px;">${field('설명', d.description)}</div>` : ''}

      <div class="ad-divider"></div>

      <div class="ad-section-title"><span class="material-symbols-outlined">history</span>백업 이력</div>
      ${backupTable(d)}

      <div class="ad-divider"></div>

      <div class="ad-section-title"><span class="material-symbols-outlined">link</span>관련 링크</div>
      <div class="ad-links">
        <a class="ad-btn" href="${histUrl}"><span class="material-symbols-outlined">history</span>이 자산의 백업 이력</a>
        <a class="ad-btn" href="/history?tab=2&q=${nameQ}&mode=contains"><span class="material-symbols-outlined">wifi</span>통신 이력</a>
        <a class="ad-btn" href="/assets"><span class="material-symbols-outlined">grid_view</span>전체 자산 목록</a>
      </div>
    `;

    // render() 는 폴링마다 ad-root 를 재생성하므로 버튼 핸들러를 매번 다시 연결.
    const viaBtnEl = $('ad-via-btn');
    if (viaBtnEl) viaBtnEl.addEventListener('click', () => openViaDialog(d));
  }

  // "경유 자산" 다이얼로그: 이 자산을 게이트웨이로 거쳐 연결되는 자산 목록 + 바로가기.
  function openViaDialog(d) {
    const sub = $('ad-via-sub');
    if (sub) sub.innerHTML = `<strong>${esc(d.name)}</strong> (${esc(d.ip || '-')}) 을(를) 경유 IP 로 사용하는 자산 ${VIA_ASSETS.length}개입니다.`;
    $('ad-via-list').innerHTML = VIA_ASSETS.map(a => {
      const icon = typeIcon(a);
      const iconHtml = icon
        ? `<img src="/images/icons/${icon}" alt="" />`
        : `<span class="material-symbols-outlined">devices</span>`;
      const meta = [
        a.typeName ? `<span>${esc(a.typeName)}</span>` : '',
        a.ip ? `<span class="ad-via-ip">${esc(a.ip)}</span>` : '',
        a.baseNumber != null ? `<span>Base ${esc(a.baseNumber)}</span>` : '',
        a.slotNumber != null ? `<span>Slot ${esc(a.slotNumber)}</span>` : '',
        a.lineName ? `<span>${esc(a.lineName)}</span>` : '',
      ].filter(Boolean).join('');
      return `
      <div class="ad-via-row">
        <div class="ad-via-icon">${iconHtml}</div>
        <div class="ad-via-info">
          <div class="ad-via-name">${esc(a.name) || ('자산 #' + a.assetId)}</div>
          <div class="ad-via-meta">${meta}</div>
        </div>
        <a class="ad-btn" href="/assets/${a.assetId}"><span class="material-symbols-outlined">arrow_forward</span>바로 가기</a>
      </div>`;
    }).join('');
    $('ad-via-modal').classList.add('show');
  }
  function closeViaModal() { $('ad-via-modal').classList.remove('show'); }

  document.addEventListener('DOMContentLoaded', async () => {
    if (window.Shell) await Shell.init({ active: '' });
    ASSET_ID = readId();

    // 경유 자산 다이얼로그 닫기: 배경 클릭 / 닫기 버튼 / ESC
    const viaModal = $('ad-via-modal');
    if (viaModal) {
      viaModal.addEventListener('click', (e) => {
        if (e.target === viaModal || e.target.closest('[data-close]')) closeViaModal();
      });
    }
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape') closeViaModal(); });

    await load();
    setInterval(load, 30000);
    document.addEventListener('visibilitychange', () => { if (!document.hidden) load(); });
  });
})();
