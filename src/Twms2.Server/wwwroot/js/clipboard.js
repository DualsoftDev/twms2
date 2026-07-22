// 클립보드 복사 헬퍼 — HTTP(비보안 컨텍스트)에서는 navigator.clipboard가 없으므로 execCommand로 폴백
window.twmsCopyText = function (text) {
    if (navigator.clipboard && window.isSecureContext)
        return navigator.clipboard.writeText(text);
    return new Promise(function (resolve, reject) {
        var ta = document.createElement('textarea');
        ta.value = text;
        ta.style.position = 'fixed';
        ta.style.opacity = '0';
        document.body.appendChild(ta);
        ta.focus();
        ta.select();
        try {
            if (document.execCommand('copy')) resolve();
            else reject(new Error('execCommand copy failed'));
        } catch (e) {
            reject(e);
        } finally {
            ta.remove();
        }
    });
};
