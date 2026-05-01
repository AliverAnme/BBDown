const $ = id => document.getElementById(id);

function render() {
  chrome.storage.local.get(['bbdown_keys'], data => {
    const keys = data.bbdown_keys || [];
    const content = $('content');
    const actions = $('actions');
    actions.innerHTML = '';

    if (!keys.length) {
      content.innerHTML = '<div style="padding:30px;text-align:center;color:#888;font-size:13px">打开B站课程页面<br>播放视频后密钥自动出现</div>';
      return;
    }

    content.innerHTML = keys.map((k, i) => `
      <div style="background:#16213e;border-radius:6px;padding:10px;margin:6px 0">
        <div style="color:#888;font-size:10px;margin-bottom:4px">KID: ${k.kid}</div>
        <div style="color:#00ff88;word-break:break-all;font-size:11px;margin-bottom:4px">KEY: ${k.key}</div>
        <div style="background:#0a0a1a;color:#aaa;padding:6px;border-radius:4px;font-size:10px;word-break:break-all;margin-bottom:6px">BBDown "${k.url}" --decrypt-drm --key ${k.key} --kid ${k.kid}</div>
        <button style="background:#00d4ff;color:#000;border:none;padding:4px 8px;border-radius:3px;cursor:pointer;margin:2px;font:11px monospace" class="copy-cmd" data-cmd="BBDown &quot;${k.url}&quot; --decrypt-drm --key ${k.key} --kid ${k.kid}">复制命令</button>
        <button style="background:#555;color:#fff;border:none;padding:4px 8px;border-radius:3px;cursor:pointer;margin:2px;font:11px monospace" class="copy-key" data-key="${k.key}">仅复制KEY</button>
      </div>
    `).join('');

    actions.innerHTML = `
      <button style="background:#00d4ff;color:#000;border:none;padding:6px 14px;border-radius:4px;cursor:pointer;margin:4px 2px;font:12px monospace" id="copyAll">复制全部</button>
      <button style="background:#ff4757;color:#fff;border:none;padding:6px 14px;border-radius:4px;cursor:pointer;margin:4px 2px;font:12px monospace" id="clearAll">清除</button>`;

    document.querySelectorAll('.copy-cmd').forEach(btn => {
      btn.onclick = () => { navigator.clipboard.writeText(btn.dataset.cmd); btn.textContent='已复制!'; setTimeout(()=>btn.textContent='复制命令',1500); };
    });
    document.querySelectorAll('.copy-key').forEach(btn => {
      btn.onclick = () => { navigator.clipboard.writeText(btn.dataset.key); btn.textContent='已复制!'; setTimeout(()=>btn.textContent='仅复制KEY',1500); };
    });
    $('copyAll').onclick = () => {
      navigator.clipboard.writeText(keys.map(k => `BBDown "${k.url}" --decrypt-drm --key ${k.key} --kid ${k.kid}`).join('\n'));
      $('copyAll').textContent = '已复制!';
    };
    $('clearAll').onclick = () => { chrome.storage.local.set({bbdown_keys: []}); render(); };
  });
}

render();
