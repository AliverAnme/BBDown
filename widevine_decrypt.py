#!/usr/bin/env python3
"""Widevine license decryptor for B站 番劇/电影 (drm_tech_type=2).
Usage: python3 widevine_decrypt.py <pssh_b64> <device_wvd_path>
Output: JSON { "keys": [{"kid": "hex", "key": "hex"}] }
"""
import sys, json, base64, requests
from pywidevine import PSSH, Cdm, Device

LICENSE_URL = "https://bvc-drm.bilivideo.com/bili_widevine"
CERT_URL   = "https://bvc-drm.bilivideo.com/cer/bilibili_certificate.bin"

HEADERS = {
    "User-Agent": "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36",
    "Referer": "https://www.bilibili.com",
}

def main():
    if len(sys.argv) < 3:
        print(json.dumps({"error": "Usage: widevine_decrypt.py <pssh_b64> <device.wvd>"}))
        sys.exit(1)

    pssh_b64 = sys.argv[1]
    wvd_path = sys.argv[2]

    try:
        device = Device.load(wvd_path)
    except Exception as e:
        print(json.dumps({"error": f"Failed to load device: {e}"}))
        sys.exit(1)

    pssh = PSSH(pssh_b64)
    cdm = Cdm.from_device(device)
    session_id = cdm.open()

    challenge = cdm.get_license_challenge(session_id, pssh)

    resp = requests.post(LICENSE_URL, data=challenge, headers={
        **HEADERS, "Content-Type": "application/x-protobuf"
    })
    resp.raise_for_status()

    cdm.parse_license(session_id, resp.content)
    keys = cdm.get_keys(session_id)
    cdm.close(session_id)

    result_keys = []
    for k in keys:
        if k.type == 'CONTENT':
            kid_hex = str(k.kid).replace('-', '')
            key_hex = k.key.hex()
            result_keys.append({"kid": kid_hex, "key": key_hex})

    print(json.dumps({"keys": result_keys}))

if __name__ == '__main__':
    main()
