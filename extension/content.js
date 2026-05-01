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

  console.log('%c[BBDown DRM Extractor] Active — keys will appear in extension popup', 'color:lime;font-size:14px');

  // Intercept WebAssembly.instantiate to capture the DRM WASM instance
  const origInstantiate = WebAssembly.instantiate;
  WebAssembly.instantiate = async function(...args) {
    const result = await origInstantiate.apply(this, args);
    if (result.instance?.exports?._biliDRMGenSPC) {
      window.__bbdown_wasm = result.instance.exports;
      window.__bbdown_drm = {
        malloc: result.instance.exports._malloc || result.instance.exports.malloc,
        free: result.instance.exports._free || result.instance.exports.free,
        biliDRMGenSPC: function(kid, nonce, cert) {
          return result.instance.exports._biliDRMGenSPC(kid, nonce, cert);
        },
        biliDRMParseCKC: function(ckc, nonce) {
          return result.instance.exports._biliDRMParseCKC(ckc, nonce);
        }
      };
      console.log('%c[BBDown] DRM WASM captured! Functions available on window.__bbdown_drm', 'color:lime;font-size:16px');
    }
    return result;
  };

  const origInstantiateStreaming = WebAssembly.instantiateStreaming;
  if (origInstantiateStreaming) {
    WebAssembly.instantiateStreaming = async function(...args) {
      const result = await origInstantiateStreaming.apply(this, args);
      if (result.instance?.exports?._biliDRMGenSPC) {
        window.__bbdown_wasm = result.instance.exports;
        window.__bbdown_drm = {
          biliDRMGenSPC: result.instance.exports._biliDRMGenSPC,
          biliDRMParseCKC: result.instance.exports._biliDRMParseCKC,
        };
        console.log('%c[BBDown] DRM WASM captured via streaming!', 'color:lime;font-size:16px');
      }
      return result;
    };
  }

  async function runCkcFlow(kidHex) {
    if (!window.__bbdown_drm?.biliDRMGenSPC) return null;
    try {
      const certResp = await fetch('https://bvc-drm.bilivideo.com/cer/bilibili_certificate.bin');
      const cert = new Uint8Array(await certResp.arrayBuffer());
      const nonce = Math.random().toString(36).slice(2, 18);
      const spc = window.__bbdown_drm.biliDRMGenSPC(kidHex, nonce, cert);
      const ckcResp = await fetch('https://bvc-drm.bilivideo.com/bilidrm', {
        method: 'POST', headers: {'Content-Type':'application/json'},
        body: JSON.stringify({spc: spc})
      });
      const data = await ckcResp.json();
      if (!data.data?.ckc) return null;
      const ckcBytes = Uint8Array.from(atob(data.data.ckc), c => c.charCodeAt(0));
      const result = window.__bbdown_drm.biliDRMParseCKC(ckcBytes, nonce);
      const keyHex = Array.from(result.key||result).map(b => b.toString(16).padStart(2,'0')).join('');
      return { kid: kidHex, key_hex: keyHex };
    } catch(e) {
      console.error('[BBDown] CKC error:', e);
      return null;
    }
  }

  setTimeout(async () => {
    if (window.__bbdown_drm?.biliDRMGenSPC) {
      const drm = await runCkcFlow('9e12d76dc9714c86bdaf00d3ecf6f081');
      if (drm) {
        window.__bbdown_auto_key = drm;
        addKey(drm.kid, drm.key_hex, 'CKC-auto');
      }
    }
  }, 8000);
})();
