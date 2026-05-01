(function() {
  'use strict';
  const KEYS = [];

  function save() {
    chrome.storage.local.set({ bbdown_keys: KEYS });
  }

  function addKey(kid, key) {
    if (KEYS.find(k => k.kid === kid)) return;
    KEYS.push({ kid, key, url: location.href, title: document.title, time: Date.now() });
    save();
  }

  const origUpdate = MediaKeySession.prototype.update;
  MediaKeySession.prototype.update = function(response) {
    try {
      const data = new Uint8Array(response);
      const text = new TextDecoder().decode(data);
      if (text.startsWith('{') && text.includes('"keys"')) {
        const lic = JSON.parse(text);
        for (const k of lic.keys || []) {
          if (k.k && k.kid) {
            const decodeB64 = b => Uint8Array.from(
              atob(b.replace(/-/g,'+').replace(/_/g,'/').replace(/=+$/,'')),
              c => c.charCodeAt(0)
            );
            const toHex = arr => [...arr].map(b => b.toString(16).padStart(2,'0')).join('');
            addKey(toHex(decodeB64(k.kid)), toHex(decodeB64(k.k)));
          }
        }
      }
    } catch(e) {}
    return origUpdate.call(this, response);
  };
})();
