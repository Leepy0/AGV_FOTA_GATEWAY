// 확인창: window.confirm 대신 <dialog>. 기본 초점은 취소, Esc 는 취소. 확인 버튼은 동사형 라벨을 받는다.
window.gwConfirm = (text, okLabel, cancelLabel, danger) => new Promise(resolve => {
    const d = document.createElement('dialog');
    d.className = 'confirm';
    d.setAttribute('aria-label', '확인');
    // 첫 줄 = 제목, '⚠' 로 시작하는 줄 = 경고(강조색), '라벨: 값' 줄 = 표 형태, 나머지는 본문
    const body = document.createElement('div');
    const lines = text.split(String.fromCharCode(10)).filter(l => l.trim());
    const h = document.createElement('h3'); h.textContent = lines.shift() || ''; body.append(h);
    for (const l of lines) {
        const d = document.createElement('div'); d.className = 'ln';
        const m = /^([^:⚠]{1,8}): (.+)$/.exec(l);
        if (l.startsWith('⚠')) { d.classList.add('warn'); d.textContent = l; }
        else if (m) { const k = document.createElement('span'); k.className = 'k'; k.textContent = m[1]; const v = document.createElement('span'); v.className = 'v'; v.textContent = m[2]; d.append(k, v); }
        else d.textContent = l;
        body.append(d);
    }
    const p = body;
    const btns = document.createElement('div'); btns.className = 'btns';
    const cancel = document.createElement('button'); cancel.type = 'button'; cancel.textContent = cancelLabel; cancel.autofocus = true;
    const ok = document.createElement('button'); ok.type = 'button'; ok.textContent = okLabel; ok.className = danger ? 'danger' : 'primary';
    cancel.onclick = () => d.close('cancel'); ok.onclick = () => d.close('ok');
    btns.append(cancel, ok); d.append(p, btns);
    d.addEventListener('close', () => { resolve(d.returnValue === 'ok'); d.remove(); });
    document.body.appendChild(d);
    d.showModal(); cancel.focus();
});
// 테마: 지금 보이는 쪽의 반대로 바꾸고 기억한다
window.gwTheme = () => {
    const r = document.documentElement;
    const dark = r.dataset.theme ? r.dataset.theme === 'dark' : matchMedia('(prefers-color-scheme: dark)').matches;
    const n = dark ? 'light' : 'dark';
    r.dataset.theme = n;
    try { localStorage.setItem('gw-theme', n); } catch (e) { }
};
