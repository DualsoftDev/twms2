// 인증 세션 헬퍼 — 서버 발급 HttpOnly 쿠키(twms_auth)가 단일 신원 소스.
// AuthStateProvider(Blazor) 와 정적 페이지(shell.js)가 공용으로 사용.
window.authSession = {
    // 현재 쿠키 세션 신원 조회 → { authenticated, name, isAdmin }
    me: async () => {
        try {
            const r = await fetch('/api/auth/me', { headers: { 'Accept': 'application/json' } });
            return await r.json();
        } catch (e) { return { authenticated: false }; }
    },
    login: async (userName, password) => {
        const r = await fetch('/api/auth/login', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
            body: JSON.stringify({ userName, password }),
        });
        const d = await r.json().catch(() => ({}));
        return { ok: r.ok, ...d };
    },
    logout: async () => { try { await fetch('/api/auth/logout', { method: 'POST' }); } catch (e) {} },

    // 레거시 호환(미사용) — 예전 호출부가 남아 있어도 무해하도록 유지
    set: () => {},
    check: () => true,
    clear: () => {},
};
