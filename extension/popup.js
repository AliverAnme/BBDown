const $ = id => document.getElementById(id);
let activeTab = 'keys';

function escapeHtml(str) {
  if (str == null) return '';
  return String(str)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

function render() {
  chrome.storage.local.get(['bbdown_keys','bbdown_wv'], data => {
    const keys = data.bbdown_keys || [];
    const wv = data.bbdown_wv || [];
    $('keyCount').textContent = keys.length;
    $('wvCount').textContent = wv.length;

    const content = $('content');
    const actions = $('actions');
    actions.innerHTML = '';

    if (activeTab === 'keys') {
      if (!keys.length) { content.innerHTML = '<div class="empty">打开B站课程页面，播放视频后密钥自动出现</div>'; return; }
      content.innerHTML = keys.map((k,i) => `
        <div class="key-row">
          <div class="kid">KID: ${escapeHtml(k.kid)}</div>
          <div class="key">KEY: ${escapeHtml(k.key)}</div>
          <div style="color:#666;margin-top:2px">${escapeHtml(k.source)} · ${escapeHtml(new Date(k.time).toLocaleTimeString())}</div>
          <div class="cmd">BBDown &quot;${escapeHtml(k.url)}&quot; --decrypt-drm --key ${escapeHtml(k.key)} --kid ${escapeHtml(k.kid)}</div>
          <button class="btn" data-copy="${i}">复制命令</button>
          <button class="btn" data-copy-key="${i}">仅复制KEY</button>
        </div>
      `).join('');
      actions.innerHTML = keys.length > 1
        ? `<button class="btn" id="copyAll">复制全部命令</button>
           <button class="btn" id="exportJson">导出 JSON</button>
           <button class="btn danger" id="clearKeys">清除</button>`
        : `<button class="btn danger" id="clearKeys">清除</button>`;
    } else {
      if (!wv.length) { content.innerHTML = '<div class="empty">打开B站番劇页面，播放DRM视频后数据自动出现</div>'; return; }
      content.innerHTML = wv.map((w,i) => `
        <div class="key-row">
          <div style="color:#0af">PSSH: ${w.pssh ? escapeHtml(w.pssh.slice(0,60))+'...' : '(待捕获)'}</div>
          <div style="color:#0af">License: ${w.license_hex ? escapeHtml(w.license_hex.slice(0,60))+'...' : '(待捕获)'}</div>
          <div style="color:#666;margin-top:2px">${escapeHtml(new Date(w.time).toLocaleTimeString())}</div>
          <div class="cmd" style="color:#ff0">
            离线解密: python3 widevine_decrypt.py &quot;${escapeHtml(w.pssh || 'PSSH')}&quot; device.wvd<br>
            然后: BBDown &quot;${escapeHtml(w.url)}&quot; --decrypt-drm --key KEY --kid KID
          </div>
        </div>
      `).join('');
      actions.innerHTML = `<button class="btn danger" id="clearWv">清除</button>`;
    }

    bindEvents(keys, wv);
  });
}

function bindEvents(keys, wv) {
  document.querySelectorAll('[data-copy]').forEach(btn => {
    btn.onclick = () => {
      const i = parseInt(btn.dataset.copy);
      const k = keys[i];
      navigator.clipboard.writeText(`BBDown "${k.url}" --decrypt-drm --key ${k.key} --kid ${k.kid}`);
      btn.textContent = '已复制!';
      setTimeout(() => btn.textContent = '复制命令', 1500);
    };
  });
  document.querySelectorAll('[data-copy-key]').forEach(btn => {
    btn.onclick = () => {
      navigator.clipboard.writeText(keys[parseInt(btn.dataset.copyKey)].key);
      btn.textContent = '已复制!';
      setTimeout(() => btn.textContent = '仅复制KEY', 1500);
    };
  });
  $('copyAll')?.addEventListener('click', () => {
    const cmds = keys.map(k => `BBDown "${k.url}" --decrypt-drm --key ${k.key} --kid ${k.kid}`).join('\n');
    navigator.clipboard.writeText(cmds);
    $('copyAll').textContent = '已复制!';
  });
  $('exportJson')?.addEventListener('click', () => {
    const blob = new Blob([JSON.stringify(keys, null, 2)], {type:'application/json'});
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = 'bbdown_keys.json';
    a.click();
  });
  $('clearKeys')?.addEventListener('click', () => {
    chrome.storage.local.set({bbdown_keys: []});
    render();
  });
  $('clearWv')?.addEventListener('click', () => {
    chrome.storage.local.set({bbdown_wv: []});
    render();
  });
}

document.querySelectorAll('.tab').forEach(tab => {
  tab.addEventListener('click', () => {
    document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
    tab.classList.add('active');
    activeTab = tab.dataset.tab;
    render();
  });
});

render();
