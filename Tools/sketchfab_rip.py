#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
sketchfab_rip.py — Sketchfab 模型一键下载解密工具(封装 sketchfang 管线)

用法:
  python Tools/sketchfab_rip.py <模型URL或32位uid> [-o 输出目录]

流程(全部无需登录):
  1. GET api.sketchfab.com/i/models/{uid}           → 元数据(files[].p.b 保护块+osgjsUrl)
     ※ 直连被墙时自动经本机 Chrome CDP(端口 9224)顶层导航抓取
  2. 下载 file.binz + model_file.binz(media.sketchfab.com CDN,公开)
  3. 从线上 viewer JS 挖当前 40-hex 静态密钥(经 Chrome fetch,本地缓存)
  4. 解密: p.b→session VM→xorshift16→r4Cz→Zstd→OSGJS JSON + model_file.bin
  5. OSGJS→GLB(sketchfang pipeline,61 mesh 级全量几何)
  6. (可选)贴图: 元数据 textures[].url CDN 直链

依赖: pip install zstandard requests;sketchfang 库在
      _refs/download/sketchfang/(sys.path 自动注入)
密钥轮换: viewer 换 key 后重跑即可(步骤3自动挖新 key 并写回
      _refs/download/sketchfab_static_key.json)
"""
import argparse, base64, json, os, re, struct, sys, time, urllib.request

WORLD = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
SKETCHFANG_DIR = os.path.join(WORLD, '_refs', 'download', 'sketchfang')
KEY_CACHE = os.path.join(WORLD, '_refs', 'download', 'sketchfab_static_key.json')
JS_URL_CACHE = os.path.join(WORLD, '_refs', 'download', 'embed_js_urls.json')
sys.path.insert(0, SKETCHFANG_DIR)

API = 'https://api.sketchfab.com/i/models/{uid}'
CDN = 'https://media.sketchfab.com'

# ---------------------------------------------------------------- HTTP 层 --
def http_get(url, timeout=60, referer=None):
    h = {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/128'}
    if referer: h['Referer'] = referer
    req = urllib.request.Request(url, headers=h)
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.read()

def http_get_via_chrome(url, chrome_port=9224, timeout=90):
    """直连失败时:起 headless Chrome 顶层导航到 url,读 body 文本。
    顶层导航不受 CORS 限制;api/static/media 域全公开。"""
    import subprocess, websocket
    try:
        tabs = json.load(urllib.request.urlopen(
            'http://127.0.0.1:%d/json/list' % chrome_port, timeout=3))
    except Exception:
        chrome = os.path.expanduser(
            '~/.agent-browser/browsers/chrome-154.0.8037.57/chrome.exe')
        prof = os.path.join(os.environ.get('TMPDIR', os.getcwd()), 'sfrip_chrome')
        subprocess.Popen([chrome, '--headless=new', '--disable-gpu',
                          '--remote-debugging-port=%d' % chrome_port,
                          '--remote-allow-origins=*', '--user-data-dir=' + prof,
                          '--no-first-run', '--no-sandbox', 'about:blank'])
        for _ in range(20):
            time.sleep(1)
            try:
                tabs = json.load(urllib.request.urlopen(
                    'http://127.0.0.1:%d/json/list' % chrome_port, timeout=3))
                break
            except Exception:
                pass
        else:
            raise RuntimeError('CDP chrome 起不来(端口 %d)' % chrome_port)
    page = [t for t in tabs if t['type'] == 'page'][0]
    ws = websocket.create_connection(page['webSocketDebuggerUrl'],
                                     timeout=timeout, suppress_origin=True)
    mid = [0]
    def cmd(method, params=None):
        mid[0] += 1
        ws.send(json.dumps({'id': mid[0], 'method': method, 'params': params or {}}))
        while True:
            m = json.loads(ws.recv())
            if m.get('id') == mid[0]:
                return m.get('result', m)
    def ev(expr):
        r = cmd('Runtime.evaluate', {'expression': expr, 'returnByValue': True,
                                     'awaitPromise': True})
        return r.get('result', {}).get('value')
    cmd('Page.navigate', {'url': url})
    for _ in range(int(timeout / 2)):
        time.sleep(2)
        if ev('document.readyState') == 'complete':
            break
    body = ev('document.body.innerText')
    ws.close()
    return (body or '').encode('utf-8')

def get_json_resilient(url):
    for attempt in (1, 2):
        try:
            return json.loads(http_get(url, referer='https://sketchfab.com/'))
        except Exception:
            if attempt == 2:
                raw = http_get_via_chrome(url)
                return json.loads(raw)
    raise RuntimeError('unreachable')

# --------------------------------------------------------------- 元数据层 --
def parse_uid(s):
    m = re.search(r'[0-9a-f]{32}', s)
    if not m: raise SystemExit('无法从 %r 解析 32 位 uid' % s)
    return m.group(0)

def fetch_meta(uid):
    d = get_json_resilient(API.format(uid=uid))
    files = [x for x in d.get('files', []) if x.get('p')]
    if not files: raise SystemExit('元数据无 files[].p(模型可能不公开)')
    files.sort(key=lambda x: -x.get('modelSize', 0))
    best = files[0]
    p = best['p']
    pb = p[0]['b'] if isinstance(p, list) else p['b']
    return {'name': d.get('name', uid), 'uid': uid, 'pb': pb,
            'osgjsUrl': best['osgjsUrl'], 'vertexCount': d.get('vertexCount'),
            'faceCount': d.get('faceCount'),
            'textures': [t.get('url') for t in d.get('textures', []) if t.get('url')]}

# ----------------------------------------------------------------- 密钥层 --
def load_cached_key():
    try:
        return json.load(open(KEY_CACHE, encoding='utf-8'))['key']
    except Exception:
        return None

def save_cached_key(key):
    os.makedirs(os.path.dirname(KEY_CACHE), exist_ok=True)
    json.dump({'key': key, 'ts': int(time.time())}, open(KEY_CACHE, 'w', encoding='utf-8'))

def _reveal(prot_bytes, hexkey):
    """static_key_schedule + 前 24 字节减法混合 → 应显 Zstd magic。
    与 sketchfang.protection.reveal_zstd_frame 相同,本地复制避免 import 顺序问题。"""
    key40 = hexkey.lower()[:40].encode('ascii')
    nib = lambda c: c + 10 - (97 if c >= 97 else 65) if c >= 65 else c - 48
    out = bytearray(prot_bytes)
    for i in range(24):
        a = nib(key40[(2 * i) % 40]); b = nib(key40[(2 * i + 1) % 40])
        out[i] = (out[i] - ((a << 4) | b)) & 0xFF
    return bytes(out)

def validate_key(pb_b64, hexkey):
    prot = base64.b64decode(pb_b64.replace('\n', '').replace('\r', '').replace(' ', ''))
    return _reveal(prot, hexkey)[:4] == b'\x28\xb5\x2f\xfd'

def discover_key(pb_b64):
    """经 Chrome 在 sketchfab 域内 fetch viewer JS,收集全部 40-hex 候选,
    逐个对 p.b 验证。命中即返回。"""
    import websocket
    tabs = json.load(urllib.request.urlopen('http://127.0.0.1:9224/json/list', timeout=3))
    page = [t for t in tabs if t['type'] == 'page'][0]
    ws = websocket.create_connection(page['webSocketDebuggerUrl'], timeout=240, suppress_origin=True)
    mid = [0]
    def cmd(method, params=None):
        mid[0] += 1
        ws.send(json.dumps({'id': mid[0], 'method': method, 'params': params or {}}))
        while True:
            m = json.loads(ws.recv())
            if m.get('id') == mid[0]:
                return m.get('result', m)
    def ev(expr):
        r = cmd('Runtime.evaluate', {'expression': expr, 'returnByValue': True,
                                     'awaitPromise': True})
        return r.get('result', {}).get('value')
    # 先到主站(任意公开页)建立 origin
    cmd('Page.navigate', {'url': 'https://sketchfab.com/featured'})
    time.sleep(8)
    urls = []
    if os.path.exists(JS_URL_CACHE):
        urls = json.load(open(JS_URL_CACHE, encoding='utf-8'))
    if not urls:
        r = ev('''(async () => {
          const h = await (await fetch('https://sketchfab.com/models/aa40849b8687426ea5da47deacb8c12f/embed?autostart=1')).text();
          return JSON.stringify([...h.matchAll(/src="(https:[^"]+?-v2\\.js)"/g)].map(m => m[1]));
        })()''')
        urls = json.loads(r or '[]')
    js = '''(async () => {
      const urls = %s;
      const out = [];
      for (const u of urls) {
        try {
          const t = await (await fetch(u)).text();
          for (const m of t.match(/[0-9a-fA-F]{40}/g) || []) out.push(m.toLowerCase());
        } catch(e) {}
      }
      return JSON.stringify([...new Set(out)]);
    })()''' % json.dumps(urls[:40])
    cands = json.loads(ev(js) or '[]')
    ws.close()
    for k in cands:
        if k == 'a' * 40: continue
        if validate_key(pb_b64, k):
            save_cached_key(k)
            return k
    raise SystemExit('viewer JS 里没有能解 p.b 的密钥(候选 %d 个)' % len(cands))

def ensure_key(pb_b64):
    key = load_cached_key()
    if key and validate_key(pb_b64, key):
        return key
    # 也试 sketchfang 内置历史密钥
    try:
        from sketchfang.crypto.protection import KNOWN_STATIC_KEY_HEX
        for k in KNOWN_STATIC_KEY_HEX:
            if validate_key(pb_b64, k):
                save_cached_key(k)
                return k
    except Exception:
        pass
    return discover_key(pb_b64)

# ---------------------------------------------------------------- 解密层 --
def decrypt_all(file_binz_path, model_binz_path, pb_b64, key_hex):
    from sketchfang.crypto.protection import register_static_key
    from sketchfang.crypto.stream import decrypt_binz
    register_static_key(key_hex)
    entry = {'b': pb_b64}
    osgjs = decrypt_binz(open(file_binz_path, 'rb').read(), [entry], progress=False)
    open(os.path.splitext(file_binz_path)[0] + '.osgjs', 'wb').write(osgjs)
    geoms = decrypt_binz(open(model_binz_path, 'rb').read(), [entry], progress=False)
    open(os.path.splitext(model_binz_path)[0] + '.bin', 'wb').write(geoms)
    return (os.path.splitext(file_binz_path)[0] + '.osgjs',
            os.path.splitext(model_binz_path)[0] + '.bin')

# ----------------------------------------------------------------- GLB 层 --
def osgjs_to_glb(uid, out_dir, osgjs_path, bin_path):
    from pathlib import Path
    from sketchfang.pipeline import rip_model
    return rip_model(uid, Path(out_dir), no_textures=False, progress=True,
                     osgjs_path=Path(osgjs_path), model_bin_path=Path(bin_path))

# ------------------------------------------------------------------ main --
def rip(url_or_uid, out_dir=None, with_textures=True):
    uid = parse_uid(url_or_uid)
    meta = fetch_meta(uid)
    name = meta['name']
    print('[1/5] 模型: %s | %s verts %s faces | 贴图 %d 张'
          % (name, meta['vertexCount'], meta['faceCount'], len(meta['textures'])))
    if out_dir is None:
        safe = re.sub(r'[^\w\-]+', '_', name)[:40]
        out_dir = os.path.join(WORLD, '_refs', 'download', safe)
    os.makedirs(out_dir, exist_ok=True)
    osgjs_url = meta['osgjsUrl']
    base = osgjs_url.rsplit('/', 1)[-1].replace('file.binz', '')
    # CDN 两个流
    file_binz = os.path.join(out_dir, 'file.binz')
    model_binz = os.path.join(out_dir, 'model_file.binz')
    for url, path in ((osgjs_url, file_binz),
                      (osgjs_url.replace('file.binz', 'model_file.binz'), model_binz)):
        if os.path.exists(path) and os.path.getsize(path) > 0:
            print('      已缓存', os.path.basename(path))
            continue
        print('      下载', url.rsplit('/', 1)[-1], '...')
        data = http_get(url, referer='https://sketchfab.com/',
                        timeout=300 if 'model_file' in url else 60)
        open(path, 'wb').write(data)
    print('[2/5] 静态密钥: ', end='')
    key = ensure_key(meta['pb'])
    print(key)
    print('[3/5] 解密 binz → OSGJS + geometry bin ...')
    osgjs_path, bin_path = decrypt_all(file_binz, model_binz, meta['pb'], key)
    print('      %s (%d B) + %s (%d B)' % (os.path.basename(osgjs_path),
          os.path.getsize(osgjs_path), os.path.basename(bin_path), os.path.getsize(bin_path)))
    print('[4/5] OSGJS → GLB ...')
    glb_dir = os.path.join(out_dir, 'glb')
    glb = osgjs_to_glb(uid, glb_dir, osgjs_path, bin_path)
    print('[5/5] 贴图 %d 张 ...' % len(meta['textures']))
    if with_textures:
        texdir = os.path.join(out_dir, 'textures')
        os.makedirs(texdir, exist_ok=True)
        for i, u in enumerate(meta['textures']):
            fn = os.path.join(texdir, u.rsplit('/', 1)[-1].split('?')[0])
            if os.path.exists(fn) and os.path.getsize(fn) > 0: continue
            try:
                open(fn, 'wb').write(http_get(u, referer='https://sketchfab.com/'))
            except Exception as e:
                print('      贴图失败 %s: %s' % (u[-30:], e))
    print('DONE  →', glb)
    print('输出目录:', out_dir)
    return glb

if __name__ == '__main__':
    ap = argparse.ArgumentParser(description='Sketchfab 免登录下载解密 → GLB')
    ap.add_argument('model', help='模型 URL 或 32 位 uid')
    ap.add_argument('-o', '--out', default=None)
    ap.add_argument('--no-textures', action='store_true')
    a = ap.parse_args()
    rip(a.model, a.out, with_textures=not a.no_textures)
