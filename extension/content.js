(function() {
  'use strict';
  const KEYS = [];
  const WIDEVINE_DATA = [];

  function save() {
    chrome.storage.local.set({ bbdown_keys: KEYS, bbdown_wv: WIDEVINE_DATA });
  }

  function addKey(kid, key, source) {
    const exists = KEYS.find(k => k.kid === kid);
    if (exists) { exists.key = key; return; }
    KEYS.push({ kid, key, source, url: location.href, title: document.title, time: Date.now() });
    save();
    console.log(`%c[BBDown] Key captured: ${kid.slice(0,8)}... → ${key.slice(0,8)}... [${source}]`, 'color:lime');
  }

  function addWidevineData(pssh, license) {
    // If called with no PSSH (license-only), try to pair with the most recent
    // entry that already has a PSSH but is still waiting for a license.
    if (!pssh) {
      const last = WIDEVINE_DATA[WIDEVINE_DATA.length - 1];
      if (last && last.pssh && !last.license_hex) {
        last.license_hex = license;
        save();
        console.log('%c[BBDown] Widevine license paired with PSSH entry', 'color:cyan');
        return;
      }
    }
    WIDEVINE_DATA.push({ pssh: pssh || '', license_hex: license, url: location.href, title: document.title, time: Date.now() });
    save();
    console.log('%c[BBDown] Widevine license captured', 'color:cyan');
  }

  // ── Hook 1: MediaKeySession.update ──
  // Captures ClearKey license (courses) which contains plaintext keys
  const origUpdate = MediaKeySession.prototype.update;
  MediaKeySession.prototype.update = function(response) {
    try {
      const data = new Uint8Array(response);
      const text = new TextDecoder().decode(data);
      if (text.startsWith('{') && text.includes('"keys"')) {
        const lic = JSON.parse(text);
        for (const k of lic.keys || []) {
          if (k.k && k.kid) {
            const keyHex = Array.from(Uint8Array.from(atob(k.k.replace(/-/g,'+').replace(/_/g,'/')), c => c.charCodeAt(0)))
              .map(b => b.toString(16).padStart(2,'0')).join('');
            const kidHex = Array.from(Uint8Array.from(atob(k.kid.replace(/-/g,'+').replace(/_/g,'/')), c => c.charCodeAt(0)))
              .map(b => b.toString(16).padStart(2,'0')).join('');
            addKey(kidHex, keyHex, 'ClearKey');
          }
        }
      }
      // Widevine license - capture for offline decrypt
      if (text.includes('\b') || data[0] === 0x08) {
        const hex = Array.from(data.slice(0, 1000)).map(b => b.toString(16).padStart(2,'0')).join('');
        const wvEntry = WIDEVINE_DATA[WIDEVINE_DATA.length - 1];
        if (wvEntry && !wvEntry.license_hex) {
          wvEntry.license_hex = hex;
          save();
        }
      }
    } catch(e) {}
    return origUpdate.call(this, response);
  };

  // ── Hook 2: Fetch interceptor ──
  const origFetch = window.fetch;
  window.fetch = function(url, opts) {
    const urlStr = typeof url === 'string' ? url : url?.url || '';

    // Capture CKC response
    if (urlStr.includes('/bilidrm') && !urlStr.includes('cert')) {
      const body = opts?.body;
      if (typeof body === 'string') {
        try {
          const parsed = JSON.parse(body);
          if (parsed.spc) {
            console.log('%c[BBDown] CKC request captured', 'color:yellow');
          }
        } catch(e) {}
      }
      return origFetch.apply(this, arguments).then(async r => {
        const clone = r.clone();
        const text = await clone.text();
        console.log('%c[BBDown] CKC response captured (encrypted - needs WASM to decrypt)', 'color:yellow');
        return r;
      });
    }

    // Capture Widevine license
    if (urlStr.includes('/bili_widevine') && !urlStr.includes('cert')) {
      console.log('%c[BBDown] Widevine license request captured', 'color:cyan');
      return origFetch.apply(this, arguments).then(async r => {
        const clone = r.clone();
        const buf = await clone.arrayBuffer();
        const hex = Array.from(new Uint8Array(buf).slice(0, 1000))
          .map(b => b.toString(16).padStart(2,'0')).join('');
        addWidevineData('', hex);
        return r;
      });
    }

    return origFetch.apply(this, arguments);
  };

  // ── Hook 3: MediaKeySession.generateRequest (capture PSSH) ──
  const origGenReq = MediaKeySession.prototype.generateRequest;
  MediaKeySession.prototype.generateRequest = function(type, initData) {
    const psshB64 = btoa(String.fromCharCode(...new Uint8Array(initData)));
    WIDEVINE_DATA.push({ pssh: psshB64, license_hex: '', url: location.href, title: document.title, time: Date.now() });
    save();
    console.log('%c[BBDown] PSSH captured', 'color:cyan');
    return origGenReq.call(this, type, initData);
  };

  console.log('%c[BBDown DRM Extractor] Active', 'color:lime;font-size:14px');

  const origInstantiate = WebAssembly.instantiate;
  WebAssembly.instantiate = async function(...args) {
    const result = await origInstantiate.apply(this, args);
    if (result.instance?.exports?._biliDRMGenSPC)
      window.__bbdown_wasm = result.instance.exports;
    return result;
  };
})();
