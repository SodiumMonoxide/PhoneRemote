"""Prepare a local iPad Web Clip and serve the two-device pairing flow."""

import argparse
import base64
import datetime as dt
import hashlib
import html
import io
import ipaddress
import json
import os
import plistlib
import re
import secrets
import threading
import time
import uuid
from functools import lru_cache
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit

import qrcode
import qrcode.image.svg
from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.x509.oid import ExtendedKeyUsageOID, NameOID
from host.pairing import load_daily_key, daily_code, daily_expiry


ROOT = Path(__file__).resolve().parent
STATE = Path(os.environ["LOCALAPPDATA"]) / "iPadWiFiDisplay" / "session"
APPROVAL = STATE / "approval"
PEM = serialization.Encoding.PEM


def write_private(path, data):
    temp = path.with_suffix(".new")
    temp.write_bytes(data)
    os.replace(temp, path)


def make_certificates(ip):
    address = ipaddress.IPv4Address(ip)
    leaf_path = STATE / "server.pem"
    if all((STATE / name).is_file() for name in ("server.pem", "key.pem", "ca.cer")):
        cert = x509.load_pem_x509_certificate(leaf_path.read_bytes())
        sans = cert.extensions.get_extension_for_class(x509.SubjectAlternativeName).value
        if address in sans.get_values_for_type(x509.IPAddress) and cert.not_valid_after_utc > dt.datetime.now(dt.timezone.utc) + dt.timedelta(days=2):
            return
    now = dt.datetime.now(dt.timezone.utc)
    start = now - dt.timedelta(hours=1)
    end = now + dt.timedelta(days=180)
    issuer_key = ec.generate_private_key(ec.SECP256R1())
    server_key = ec.generate_private_key(ec.SECP256R1())
    issuer = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, f"iPad Wi-Fi Display {ip}")])
    leaf = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, ip)])

    def builder(subject, key):
        return (x509.CertificateBuilder().subject_name(subject).issuer_name(issuer)
                .public_key(key.public_key()).serial_number(x509.random_serial_number())
                .not_valid_before(start).not_valid_after(end))

    ca = (builder(issuer, issuer_key)
          .add_extension(x509.BasicConstraints(ca=True, path_length=0), critical=True)
          .add_extension(x509.KeyUsage(False, False, False, False, False, True, True, None, None), critical=True)
          .add_extension(x509.SubjectKeyIdentifier.from_public_key(issuer_key.public_key()), critical=False)
          .add_extension(x509.NameConstraints([x509.IPAddress(ipaddress.ip_network(ip + "/32"))], None), critical=True)
          .sign(issuer_key, hashes.SHA256()))
    cert = (builder(leaf, server_key)
            .add_extension(x509.SubjectAlternativeName([x509.IPAddress(address)]), critical=False)
            .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
            .add_extension(x509.KeyUsage(True, False, False, False, False, False, False, None, None), critical=True)
            .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH]), critical=False)
            .add_extension(x509.AuthorityKeyIdentifier.from_issuer_public_key(issuer_key.public_key()), critical=False)
            .sign(issuer_key, hashes.SHA256()))
    # Only the server's leaf key is persisted. No signing key remains on disk.
    write_private(STATE / "key.pem", server_key.private_bytes(PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))
    write_private(STATE / "ca.cer", ca.public_bytes(serialization.Encoding.DER))
    write_private(leaf_path, cert.public_bytes(PEM))


def payload(kind, suffix, label):
    identifier = "local.ipadwifidisplay." + suffix
    return dict(PayloadType=kind, PayloadVersion=1, PayloadIdentifier=identifier,
                PayloadUUID=str(uuid.uuid5(uuid.NAMESPACE_URL, identifier)).upper(), PayloadDisplayName=label)


def prepare(ip, port):
    APPROVAL.mkdir(parents=True, exist_ok=True)
    make_certificates(ip)
    ca = (STATE / "ca.cer").read_bytes()
    profile = payload("Configuration", "receiver", "Wi-Fi 副屏接收器")
    cert = payload("com.apple.security.root", "certificate", "副屏专用 HTTPS 证书")
    cert.update(PayloadContent=ca, PayloadCertificateFileName="ipad-display.cer")
    clip = payload("com.apple.webClip.managed", "webclip", "Wi-Fi 副屏")
    clip.update(URL=f"https://{ip}:{port}/", Label="Wi-Fi 副屏", IsRemovable=True, FullScreen=True)
    icon = next((p for p in (ROOT / "web").glob("*.png") if "icon" in p.name), None)
    if icon:
        clip.update(Icon=icon.read_bytes(), Precomposed=True)
    profile.update(PayloadContent=[cert, clip], PayloadRemovalDisallowed=False,
                   PayloadDescription=f"添加 Wi-Fi 副屏图标及 {ip} 的本地 HTTPS 证书。可在设置中移除。")
    write_private(STATE / "receiver.mobileconfig", plistlib.dumps(profile))
    for name in ("pair-token", "console-token", "install-token"):
        if not (STATE / name).is_file():
            write_private(STATE / name, secrets.token_urlsafe(32).encode())


def qr(data):
    image = qrcode.make(data, image_factory=qrcode.image.svg.SvgPathFillImage, box_size=5)
    buf = io.BytesIO()
    image.save(buf)
    return "data:image/svg+xml;base64," + base64.b64encode(buf.getvalue()).decode()


def read_request():
    try:
        req = json.loads((APPROVAL / "request.json").read_text(encoding="utf-8-sig"))
        if req["expires"] > time.time() and re.fullmatch("[a-fA-F0-9]{16,128}", req["id"]):
            return {"id": req["id"], "ip": str(ipaddress.ip_address(req["ip"]))}
    except (OSError, ValueError, TypeError, KeyError):
        pass
    return None


@lru_cache(maxsize=2)
def make_console(ip, port, control_path, install_path, short_code, code_until):
    pair_url = f"https://{ip}:{port}/#token={short_code}"
    profile_url = f"http://{ip}:8442{install_path}"
    digest = hashlib.sha256((STATE / "ca.cer").read_bytes()).hexdigest().upper()
    return f"""<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
<title>Wi-Fi 副屏 · 电脑控制台</title><style>
*{{box-sizing:border-box}}body{{margin:3em auto;max-width:960px;padding:0 20px;color:#173435;background:#f7f9f7;font:16px/1.55 system-ui,"Microsoft YaHei",sans-serif}}
h1{{margin-bottom:.1em}}h2{{font-size:1.15rem}}section{{background:white;border:1px solid #c6d6d1;border-radius:12px;padding:18px 24px}}
main{{display:grid;grid-template-columns:repeat(auto-fit,minmax(260px,1fr));gap:1em;align-items:start}}img{{width:180px;max-width:100%;height:180px}}
#digits{{font:650 clamp(34px,5vw,52px)/1.2 ui-monospace,monospace;color:#064e46;letter-spacing:.12em;user-select:all;margin:.2em 0}}summary{{cursor:pointer}}
#guide{{grid-column:1/-1;scroll-margin-top:5rem}}#guide h2{{margin-top:0}}#guide ol{{padding-inline-start:1.6em}}
#guide li+li,#guide details p{{margin-top:.55em}}#guide details{{border-top:1px solid #d9e4df}}#guide summary{{padding:.65em 0}}
small,code{{overflow-wrap:anywhere}}#prompt{{background:#fff5e5;position:sticky;top:8px;border-color:#a46208;margin-bottom:12px}}
button{{padding:8px 14px;cursor:pointer}}a{{color:#00655d}}[hidden]{{display:none!important}}
</style><h1>Wi-Fi 副屏</h1><p>让 iPad 和电脑连入同一局域网。首次连接时保持此页打开。 <a href="#guide">操作说明</a></p><p id="playback" aria-live="polite">等待接收端播放。</p>
<section id="prompt" hidden><strong id="client"></strong> <button id="yes">允许连接</button> <button id="no">拒绝</button></section><main>
<section><h2>今日配对码</h2><p id="digits">{short_code}</p>
<p>新接收端输入这六位数字，再在电脑上点「允许连接」。配对码每天北京时间 00:00 更新，已配对的 iPad 可直接重连。</p>
<details><summary>用二维码或链接配对</summary><img src="{qr(pair_url)}" alt="今日配对二维码"><br><small>{html.escape(pair_url)}</small></details></section>
<section><h2>首次安装到 iPad</h2><details><summary>显示安装二维码和证书说明</summary><img src="{qr(profile_url)}" alt="安装接收器二维码">
<p>用 iPad 相机扫码，在 Safari 允许下载，再点「设置 → 已下载描述文件 → 安装」。安装后主屏幕会出现「Wi-Fi 副屏」。</p>
<p>找不到「已下载描述文件」时，可以到「设置 → 通用 → VPN 与设备管理」查看是否已安装「Wi-Fi 副屏接收器」。下载后超过 8 分钟仍未安装，请重新扫上方的安装码。 <a href="https://support.apple.com/zh-cn/102400">Apple 安装说明</a></p>
<p>在「设置 → 通用 → 关于本机 → 证书信任设置」启用 <b>iPad Wi-Fi Display {ip}</b>。先在描述文件的证书详情中核对 SHA-256 指纹。</p>
<small>证书 SHA-256<br><code>{digest}</code></small></details></section>
<section id="guide" aria-labelledby="guide-title"><h2 id="guide-title">操作说明</h2>
<ol>
<li><strong>启动副屏。</strong> 在电脑上打开 PhoneRemote，选择「iPad 副屏」。将 iPad 横放，从原来的「Wi-Fi 副屏」图标打开，点一次「连接屏幕」。</li>
<li><strong>移动窗口。</strong> 连上后，用电脑鼠标拖住窗口标题栏，越过主屏右边缘，窗口就会出现在 iPad 上。也可以选中窗口后按 <kbd>Win + Shift + →</kbd>。继续用电脑的鼠标和键盘操作窗口。</li>
<li><strong>全屏或断开。</strong> 轻点 iPad 画面调出工具栏，点「全屏」扩大显示区域。「填满屏幕」可能裁去桌面边缘，再点一次可恢复完整桌面。用完点「断开」，下次仍可从原图标连接。</li>
</ol>
<details><summary>iPad 又提示输入配对码</summary>
<p>先确认打开的是上次使用的图标或 Safari 页面。换入口或清除浏览器数据后，输入上方的「今日配对码」，点「连接屏幕」，再回到电脑点「允许连接」。</p></details>
<details><summary>连接后没有画面，或只看到桌面壁纸</summary>
<p>如果这里显示「正在播放」，iPad 上只有壁纸，可以按第 2 步移入一个窗口。正在使用扩展屏时，可在 Windows「设置 → 系统 → 屏幕」查看两块屏幕的排列。</p>
<p>如果一直没有画面，确认两台设备在可互访的同一局域网。iPad 若出现「播放」就轻点一次。仍收不到画面时可选「兼容连接」。兼容模式的帧率较低。</p></details>
</section></main>
<script>const base={json.dumps(control_path)},box=document.querySelector('#prompt');let id='';
function refreshDay(){{if(Date.now()>={code_until}*1000)location.reload()}}
setTimeout(refreshDay,Math.max(1,{code_until}*1000-Date.now()+100));
window.addEventListener('pageshow',refreshDay);document.addEventListener('visibilitychange',refreshDay);
async function poll(){{try{{let r=await fetch(base+'/request',{{cache:'no-store'}});if(!r.ok)throw Error();let p=await r.json();id=p?.id||'';box.hidden=!id;if(id)document.querySelector('#client').textContent='允许 '+p.ip+' 查看扩展屏？'}}catch{{box.hidden=true}}}}
async function act(action){{if(!id)return;await fetch(base+'/'+action+'/'+id,{{method:'POST'}});box.hidden=true;id=''}}
function playbackText(info) {{
  if (info.state !== 'playing') return info.state === 'closed' ? '接收端已断开。' : '等待接收端播放。';
  const recent = Date.now()/1000 - info.updated_at < 20;
  const parts = [(info.platform === 'iPad' ? 'iPad' : '浏览器') + (recent ? ' 正在播放' : ' 最近一次播放回报'),
                 info.width + ' × ' + info.height];
  if (info.transport) parts.push(info.transport === 'https-jpeg' ? 'HTTPS 兼容模式' : 'WebRTC');
  if (Number.isFinite(info.stats?.fps)) parts.push(info.stats.fps.toFixed(1) + ' 帧/秒');
  if (Number.isFinite(info.stats?.jitterMs)) parts.push('播放缓冲 ' + Math.round(info.stats.jitterMs) + ' ms');
  if (Number.isFinite(info.stats?.rttMs)) parts.push('网络往返 ' + Math.round(info.stats.rttMs) + ' ms');
  const modes = {{native:'系统全屏', standalone:'主屏幕 App', windowed:'窗口显示'}};
  if (modes[info.fullscreen]) parts.push(modes[info.fullscreen]);
  if (info.fit) parts.push(info.fit === 'cover' ? '铺满画面' : '完整桌面');
  return parts.join(' · ');
}}
async function showPlayback() {{
  try {{
    const response = await fetch(base+'/playback', {{cache:'no-store'}});
    if (response.ok) document.querySelector('#playback').textContent = playbackText(await response.json());
  }} catch {{ /* Poll again when the service is reachable. */ }}
}}
document.querySelector('#yes').onclick=()=>act('approve');document.querySelector('#no').onclick=()=>act('decline');poll();showPlayback();setInterval(poll,800);setInterval(showPlayback,2500);</script></html>""".encode()


def serve(ip, port):
    local_host = "127.0.0.1:8442"
    control_path = "/c/" + (STATE / "console-token").read_text().strip()
    install_path = "/install/" + (STATE / "install-token").read_text().strip() + ".mobileconfig"
    key = load_daily_key(STATE)

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def send(self, data, content, status=200):
            self.send_response(status)
            self.send_header("Content-Type", content)
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.send_header("Referrer-Policy", "no-referrer")
            self.send_header("X-Frame-Options", "DENY")
            self.end_headers()
            self.wfile.write(data)

        def is_console(self):
            return self.server.server_address[0] == "127.0.0.1" and self.headers.get("Host") == local_host

        def do_GET(self):
            path = urlsplit(self.path).path
            if self.is_console() and path == "/":
                self.send_response(303)
                self.send_header("Location", control_path)
                self.send_header("Cache-Control", "no-store")
                self.send_header("Referrer-Policy", "no-referrer")
                self.end_headers()
            elif self.is_console() and path == control_path:
                now = dt.datetime.now(dt.timezone.utc)
                console = make_console(ip, port, control_path, install_path, daily_code(key, now), int(daily_expiry(now).timestamp()))
                self.send(console, "text/html; charset=utf-8")
            elif self.is_console() and path == control_path + "/request":
                self.send(json.dumps(read_request()).encode(), "application/json")
            elif self.is_console() and path == control_path + "/playback":
                try:
                    data = (APPROVAL / "playback.json").read_bytes()
                except OSError:
                    data = b'{"state":"idle"}'
                self.send(data, "application/json")
            elif self.server.server_address[0] == ip and path == install_path:
                if self.client_address[0] != ip:
                    (STATE / "last-profile-download.txt").write_text(str(int(time.time())), encoding="ascii")
                self.send((STATE / "receiver.mobileconfig").read_bytes(), "application/x-apple-aspen-config")
            elif self.server.server_address[0] == ip and path == "/":
                self.send(('<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>安装 Wi-Fi 副屏</title>'
                           '<main style="font:18px/1.6 system-ui;margin:10vw auto;max-width:30em;padding:1.5em"><h1>安装 Wi-Fi 副屏</h1>'
                           f'<p><a href="{install_path}">下载 iPad 接收器描述文件</a></p>'
                           '<p>下载后打开「设置 → 已下载描述文件」安装，再按照电脑控制台的说明信任证书并配对。</p></main>').encode(), "text/html; charset=utf-8")
            else:
                self.send(b"", "text/plain", 404)

        def do_POST(self):
            req = read_request()
            if (not self.is_console() or not req or self.headers.get("Origin") != "http://" + local_host):
                return self.send(b"", "text/plain", 403)
            for action in ("approve", "decline"):
                if self.path == f"{control_path}/{action}/{req['id']}":
                    (APPROVAL / f"{action}-{req['id']}").touch()
                    return self.send(b"", "text/plain", 204)
            self.send(b"", "text/plain", 404)

    local = ThreadingHTTPServer(("127.0.0.1", 8442), Handler)
    lan = ThreadingHTTPServer((ip, 8442), Handler)
    threading.Thread(target=local.serve_forever, daemon=True).start()
    lan.serve_forever()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=("prepare", "serve", "rotate"))
    parser.add_argument("ip", type=ipaddress.IPv4Address)
    parser.add_argument("--video-port", type=int, choices=(8443, 8453), default=8453)
    args = parser.parse_args()
    if args.action == "rotate":
        prepare(str(args.ip), args.video_port)
        write_private(STATE / "pair-token", secrets.token_urlsafe(32).encode())
        for item in ["request.json", "paired-receiver.json"]:
            (APPROVAL / item).unlink(missing_ok=True)
        for pattern in ["approve-*", "decline-*"]:
            for item in APPROVAL.glob(pattern):
                if re.fullmatch(r"(approve|decline)-[a-f0-9]{32}", item.name):
                    item.unlink(missing_ok=True)
    elif args.action == "prepare":
        prepare(str(args.ip), args.video_port)
    else:
        serve(str(args.ip), args.video_port)
