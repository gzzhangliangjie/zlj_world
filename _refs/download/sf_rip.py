# -*- coding: utf-8 -*-
# CDP Fetch-domain interceptor: capture Sketchfab viewer model files (binz/osgjs/glb/bin)
# Usage: python sf_rip.py <model_page_url> <out_dir>
import json, urllib.request, websocket, time, threading, base64, os, sys

URL = sys.argv[1] if len(sys.argv) > 1 else 'https://sketchfab.com/3d-models/hxd3d-electric-locomotive-hxd3d-aa40849b8687426ea5da47deacb8c12f'
OUT = sys.argv[2] if len(sys.argv) > 2 else '_refs/download/hxd3d_rip'
os.makedirs(OUT, exist_ok=True)

tabs = json.load(urllib.request.urlopen('http://127.0.0.1:9224/json/list'))
page = [t for t in tabs if t['type'] == 'page'][0]
ws = websocket.create_connection(page['webSocketDebuggerUrl'], timeout=300, suppress_origin=True)
lock = threading.Lock()
mid = [0]; pending = {}; saved = []; counter = [0]

WANT = ('.binz', 'osgjs', 'model_file', '.glb', 'file.bin', 'geometry', '.bin')

def want(url):
    u = url.lower()
    return any(k in u for k in WANT) and 'static.sketchfab.com' in u or any(k in u for k in ('binz','osgjs','model_file'))

def receiver():
    while True:
        try:
            m = json.loads(ws.recv())
        except Exception:
            break
        if 'id' in m:
            with lock: pending[m['id']] = m
        elif m.get('method') == 'Fetch.requestPaused':
            p = m['params']; rid = p['requestId']; url = p['request']['url']
            status = p.get('responseStatusCode')
            try:
                if status is not None and want(url):
                    with lock:
                        counter[0] += 1; i = 800000 + counter[0]
                        ws.send(json.dumps({'id': i, 'method': 'Fetch.getResponseBody', 'params': {'requestId': rid}}))
                        pending['__body_%d' % i] = (rid, url)
                with lock:
                    ws.send(json.dumps({'method': 'Fetch.continueRequest', 'params': {'requestId': rid}}))
            except Exception:
                pass

t = threading.Thread(target=receiver, daemon=True); t.start()

def cmd(method, params=None, timeout=60):
    with lock:
        mid[0] += 1; i = mid[0]
        ws.send(json.dumps({'id': i, 'method': method, 'params': params or {}}))
    deadline = time.time() + timeout
    while time.time() < deadline:
        with lock:
            if i in pending:
                return pending.pop(i)
        time.sleep(0.05)
    return {'error': 'timeout'}

cmd('Page.enable'); cmd('Network.enable')
cmd('Network.setCacheDisabled', {'cacheDisabled': True})
r = cmd('Fetch.enable', {'patterns': [{'urlPattern': '*', 'requestStage': 'Response'}]})
if 'result' not in r:
    print('Fetch.enable failed:', r); sys.exit(1)
print('intercepting; loading', URL)
cmd('Page.navigate', {'url': URL}, timeout=90)

# 持续收割 body:任何 id>=800001 的 pending 就是 getResponseBody 结果
deadline = time.time() + 75
while time.time() < deadline:
    with lock:
        keys = [k for k in pending if isinstance(k, str) and k.startswith('__body_')]
        for k in keys:
            i = int(k.split('_')[-1]); rid, url = pending.pop(k)
            body_resp = pending.pop(i, None)
            if body_resp and 'result' in body_resp:
                res = body_resp['result']
                data = base64.b64decode(res['base64']) if res.get('base64') else None
                if data:
                    name = url.split('?')[0].split('/')[-1] or 'file_%d' % i
                    if not os.path.splitext(name)[1]: name += '.binz'
                    path = os.path.join(OUT, name)
                    n = 1
                    while os.path.exists(path):
                        path = os.path.join(OUT, '%s_%d%s' % (os.path.splitext(name)[0], n, os.path.splitext(name)[1] or '.binz')); n += 1
                    open(path, 'wb').write(data)
                    saved.append((name, len(data)))
                    print('SAVED', name, len(data))
            elif body_resp is None:
                # 结果还没到,塞回去等
                pending[k] = (rid, url)
    time.sleep(0.5)
print('total saved:', len(saved))
for s in saved: print(' ', s)
