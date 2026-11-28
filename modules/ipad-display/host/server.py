"""Single-receiver Windows extended-display WebRTC host."""

import argparse
import asyncio
import hashlib
import hmac
import ipaddress
import json
import logging
import math
import re
import secrets
import ssl
import sys
import time
from pathlib import Path
from fractions import Fraction

import aioice.ice
import av
from aiohttp import web
from aiortc import RTCConfiguration, RTCPeerConnection, RTCRtpSender, RTCSessionDescription

from display import DesktopTrack, displays, selected_display
from pairing import daily_code, daily_expiry, load_daily_key
from video_quality import choose_offer, install_encoder, local_answer

WEB = Path(__file__).resolve().parents[1] / "web"
LOG = logging.getLogger("ipad-display")
JPEG_FPS = 15
JPEG_INTERVAL = 1 / JPEG_FPS
JPEG_TIMEOUT = 25


def bearer(request):
    kind, _, value = request.headers.get("Authorization", "").partition(" ")
    return value if kind.lower() == "bearer" and value.isascii() else ""


def matches(presented, expected):
    return bool(expected) and hmac.compare_digest(presented, expected)


def manual_code(token):
    digest = hashlib.sha256(("manual:" + token).encode()).digest()
    return str(int.from_bytes(digest[:8], "big") % 10**10).zfill(10)


def error(status, message):
    return web.json_response({"error": message}, status=status)


def numeric(value, minimum, maximum):
    return type(value) in (int, float) and minimum <= value <= maximum


def viewport_of(payload):
    view = payload.get("viewport")
    if (not isinstance(view, dict) or not numeric(view.get("width"), 1, 8192)
            or not numeric(view.get("height"), 1, 8192) or not numeric(view.get("dpr"), 0.5, 5)):
        return None
    return {k: round(view[k], 2) for k in ("width", "height", "dpr")}


def desired_mode(modes, viewport, max_width):
    long_edge, short_edge = sorted((viewport["width"], viewport["height"]), reverse=True)
    aspect = long_edge / short_edge
    physical_width = min(max_width, long_edge * viewport["dpr"])
    usable = [m for m in modes if m["width"] <= max_width and 30 <= m["hz"] <= 60]
    if not usable:
        return None
    return min(usable, key=lambda m: (round(abs(math.log(m["width"] / m["height"] / aspect)), 4),
                                      abs(m["width"] - physical_width), -m["hz"]))


class Host:
    def __init__(self, token, selector, demo=False, approval_dir=None, manage_display=False,
                 manual_expires=None):
        install_encoder()
        self.token = token
        self.selector = selector
        self.demo = demo
        self.approval_dir = approval_dir
        self.manage_display = manage_display and selector == "auto" and not demo
        self.owns_layout = False
        self.pair_hash = hashlib.sha256(token.encode()).hexdigest()
        self.daily_key = load_daily_key(approval_dir.parent) if approval_dir else None
        self.alias = manual_code(token) if approval_dir else None
        self.alias_expiry = manual_expires if manual_expires is not None else time.time() + 600
        self.alias_failures = {}
        self.receivers = []
        self.active_receiver = None
        if approval_dir:
            try:
                record = json.loads((approval_dir / "paired-receiver.json").read_text("utf-8"))
                if record["pair"] == self.pair_hash:
                    saved = record.get("receivers", [record.get("receiver")])
                    if isinstance(saved, list):
                        self.receivers = [v for v in saved[-16:] if isinstance(v, str)
                                          and re.fullmatch("[0-9a-f]{64}", v)]
                    if self.receivers:
                        self.token = None
            except (OSError, ValueError, KeyError, TypeError):
                pass
        self.pc = None
        self.track = None
        self.lock = asyncio.Lock()
        self.stream = None
        self.playback = {}
        self.jpeg = None
        self.frame_lock = asyncio.Lock()
        self.last_frame = 0
        self.watchdog = None
        self.publish_state("idle")

    def authorized(self, credential):
        digest = hashlib.sha256(credential.encode()).hexdigest()
        return any(matches(digest, saved) for saved in self.receivers)

    def owns_stream(self, credential):
        return matches(hashlib.sha256(credential.encode()).hexdigest(), self.active_receiver)

    def valid_initial(self, value):
        if self.daily_key and re.fullmatch("[0-9]{6}", value):
            return matches(value, daily_code(self.daily_key))
        return bool(self.token) and (matches(value, self.token) or
                                     (time.time() < self.alias_expiry and matches(value, self.alias)))

    def check_alias(self, credential, address):
        if not self.approval_dir or not re.fullmatch("(?:[0-9]{6}|[0-9]{10})", credential):
            return None
        now = time.monotonic()
        attempts = [at for at in self.alias_failures.get(address, []) if at > now - 60]
        if len(attempts) >= 5:
            return error(429, "尝试次数过多，请在一分钟后重试。")
        if not self.valid_initial(credential):
            self.alias_failures[address] = attempts + [now]
        return None

    def new_session(self):
        session = secrets.token_urlsafe(32)
        digest = hashlib.sha256(session.encode()).hexdigest()
        updated = (self.receivers + [digest])[-16:]
        if self.approval_dir:
            target = self.approval_dir / "paired-receiver.json"
            tmp = target.with_suffix(".tmp")
            tmp.write_text(json.dumps({"pair": self.pair_hash, "receiver": digest, "receivers": updated}), "utf-8")
            tmp.replace(target)
        self.receivers = updated
        self.token = None
        return session

    def publish_state(self, state):
        self.playback.update(state=state, updated_at=int(time.time()))
        if self.approval_dir:
            try:
                self.approval_dir.mkdir(parents=True, exist_ok=True)
                tmp = self.approval_dir / "playback.tmp"
                tmp.write_text(json.dumps(self.playback), encoding="utf-8")
                tmp.replace(self.approval_dir / "playback.json")
            except OSError as exc:
                LOG.warning("Local playback status unavailable: %s", type(exc).__name__)

    def monitor(self):
        return None if self.demo else selected_display(self.selector, displays())

    async def layout(self, action, *options):
        if not self.manage_display:
            return {}
        helper = WEB.parent / "driver" / "display_lifecycle.py"
        proc = await asyncio.create_subprocess_exec(
            sys.executable, str(helper), action, *options, stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.DEVNULL, creationflags=0x08000000,  # CREATE_NO_WINDOW
        )
        stdout, _ = await proc.communicate()
        try:
            result = json.loads(stdout)
            if proc.returncode == 0:
                return result
        except ValueError:
            pass
        LOG.warning("Virtual display %s unavailable", action)
        return {}

    async def status(self, request):
        monitor = self.monitor()
        waiting = not self.demo and not monitor and (await self.layout("status")).get("available")
        label = "测试画面" if self.demo else "等待 Windows 扩展显示器"
        if monitor:
            label = f'{monitor.get("name", "扩展显示器")} · {monitor["width"]} × {monitor["height"]}'
        elif waiting:
            label = "连接后启用扩展显示器"
        return web.json_response({
            "ready": bool(self.demo or monitor or waiting), "mode": "demo" if self.demo else "display",
            "display": label,
        })

    async def approve(self, remote):
        if self.approval_dir is None:
            return True
        directory = self.approval_dir
        directory.mkdir(parents=True, exist_ok=True)
        identifier = secrets.token_hex(16)
        pending = directory / "request.json"
        staging = directory / f"{identifier}.tmp"
        allow = directory / f"approve-{identifier}"
        deny = directory / f"decline-{identifier}"
        deadline = time.monotonic() + 45
        try:
            staging.write_text(json.dumps({
                "id": identifier, "ip": remote, "expires": int(time.time()) + 45,
            }), encoding="utf-8")
            staging.replace(pending)
            while time.monotonic() < deadline:
                if deny.is_file():
                    return False
                if allow.is_file():
                    return True
                await asyncio.sleep(0.25)
            return False
        finally:
            for path in (pending, staging, allow, deny):
                path.unlink(missing_ok=True)

    async def close(self, detach=False):
        pc, track = self.pc, self.track
        owns_layout, self.owns_layout = self.owns_layout, False
        self.pc, self.track = None, None
        self.stream = None
        self.active_receiver = None
        self.jpeg = None
        if self.watchdog and self.watchdog is not asyncio.current_task():
            self.watchdog.cancel()
        self.watchdog = None
        if track:
            self.publish_state("closed")
        if track:
            track.stop()
        if pc:
            await pc.close()
        if detach and owns_layout:
            await self.layout("detach")

    async def offer(self, request):
        credential = bearer(request)
        limited = self.check_alias(credential, request.remote)
        if limited is not None:
            return limited
        if not (self.valid_initial(credential) or self.authorized(credential)):
            return error(401, "配对信息已失效，请使用电脑上的配对链接。")
        if self.lock.locked():
            return error(409, "电脑正在处理另一项连接请求。")
        try:
            payload = await request.json()
        except (ValueError, UnicodeError):
            return error(400, "无效的连接请求。")
        compatibility = request.path == "/api/compat"
        if not isinstance(payload, dict):
            return error(400, "需要视频连接请求。")
        if not compatibility:
            if payload.get("type") != "offer" or not isinstance(payload.get("sdp"), str):
                return error(400, "需要视频连接请求。")
            media = re.findall(r"^m=(\w+)", payload["sdp"], re.MULTILINE)
            if media != ["video"]:
                return error(400, "需要单个视频接收通道。")
        async with self.lock:
            first = self.valid_initial(credential)
            expiry = daily_expiry().timestamp() if first and len(credential) == 6 else None
            if not first and not self.authorized(credential):
                return error(401, "配对信息已失效。")
            monitor = self.monitor()
            layout = await self.layout("status")
            if not self.demo and not monitor and not (layout.get("managed") and layout.get("available")):
                return error(503, "Windows 扩展显示器尚未就绪。")
            if first and not await self.approve(request.remote):
                return error(403, "电脑未批准连接，请重新连接。")
            if first and (not self.valid_initial(credential) or (expiry and time.time() >= expiry)):
                return error(401, "配对码已过期，请输入电脑上今天的配对码。")
            attached_now = False
            if not self.demo and not monitor:
                result = await self.layout("attach")
                attached_now = bool(result.get("attached"))
                monitor = self.monitor()
                if not attached_now or not monitor:
                    if attached_now:
                        await self.layout("detach")
                    return error(503, "Windows 尚未恢复扩展显示器。")
            elif monitor and not selected_display(monitor["unique_id"], displays()):
                return error(503, "配对期间 Windows 扩展显示器已断开。")
            pc = None
            track = None
            try:
                hz = 60  # Capture ceiling. Older drivers may refresh less frequently.
                viewport = viewport_of(payload)
                if monitor and layout.get("managed") and viewport:
                    published = await self.layout("modes")
                    current = published.get("current", {})
                    # Rendering a larger desktop than the decoder can receive
                    # reduces legibility. Use the 1600-wide variant in that case.
                    max_width = 1920
                    if not compatibility and choose_offer(payload["sdp"], 1920, 1440, 60)[1].width < 1680:
                        max_width = 1600
                    target = desired_mode(published.get("modes", []), viewport, max_width)
                    if target and any(target[k] != current.get(k) for k in ("width", "height", "hz")):
                        changed = await self.layout("mode", "--width", str(target["width"]),
                                                    "--height", str(target["height"]), "--hz", str(target["hz"]))
                        current = changed.get("current", current)
                    hz = max(1, min(60, int(current.get("hz", 30))))
                    monitor = selected_display(monitor["unique_id"], displays())
                    if not monitor:
                        raise ValueError("Virtual monitor unavailable after mode selection")
                track = DesktopTrack(monitor["unique_id"] if monitor else None)
                if compatibility:
                    track.max_size = (1920, 1440)
                    target_fps = JPEG_FPS
                else:
                    width, height = (monitor["width"], monitor["height"]) if monitor else (640, 360)
                    offer, mode = choose_offer(payload["sdp"], width, height, hz)
                    target_fps = track.fps = mode.fps
                    track.video_mode = mode
                    track.max_size = (mode.width, mode.height)
                    pc = RTCPeerConnection(RTCConfiguration(iceServers=[]))
                    video = pc.addTransceiver("video", direction="sendonly")
                    codecs = RTCRtpSender.getCapabilities("video").codecs
                    preferred = [c for c in codecs if c.mimeType.lower() == "video/h264"]
                    video.setCodecPreferences(preferred + [c for c in codecs if c.mimeType == "video/rtx"])
                    await pc.setRemoteDescription(offer)
                    video.sender.replaceTrack(track)
                    await pc.setLocalDescription(local_answer(await pc.createAnswer(), mode))
                    if "a=sendonly" not in pc.localDescription.sdp:
                        raise ValueError("No sending video track negotiated")
            except Exception as exc:
                LOG.warning("Video negotiation failed: %s", type(exc).__name__)
                if track:
                    track.stop()
                if pc:
                    await pc.close()
                if attached_now:
                    await self.layout("detach")
                return error(400, "未能协商 H.264 视频，请用 Safari 重新连接。")
            if first:
                expired = not self.valid_initial(credential) or (expiry and time.time() >= expiry)
                try:
                    session = None if expired else self.new_session()
                except OSError:
                    session = None
                    LOG.warning("Could not persist the new receiver approval")
                if session is None:
                    track.stop()
                    if pc:
                        await pc.close()
                    if attached_now:
                        await self.layout("detach")
                    if expired:
                        return error(401, "配对码已过期，请使用电脑上今天的配对码。")
                    return error(503, "电脑未能保存配对信息，请稍后重试。")
            else:
                session = credential
            await self.close()
            self.pc, self.track = pc, track
            self.owns_layout = bool(layout.get("managed"))
            self.playback = {"transport": "https-jpeg" if compatibility else "webrtc", "targetFps": target_fps}
            self.publish_state("idle")
            self.stream = secrets.token_urlsafe(18)
            self.active_receiver = hashlib.sha256(session.encode()).hexdigest()
            answer = {"session": session, "stream": self.stream, "fps": target_fps}
            if compatibility:
                self.last_frame = time.monotonic() - JPEG_INTERVAL
                self.watchdog = asyncio.create_task(self.expire_frames(self.stream))
                return web.json_response(answer)

            @pc.on("connectionstatechange")
            async def changed():
                if pc.connectionState in {"closed", "failed"}:
                    await close_current()

            @track.on("ended")
            async def display_ended():
                await close_current()

            async def close_current():
                async with self.lock:
                    if self.pc is pc:
                        await self.close(detach=True)

            return web.json_response(dict(answer, sdp=pc.localDescription.sdp, type="answer"))

    async def expire_frames(self, stream):
        while self.stream == stream:
            remaining = JPEG_TIMEOUT - (time.monotonic() - self.last_frame)
            if remaining > 0:
                await asyncio.sleep(remaining)
                continue
            async with self.lock:
                if self.stream == stream and time.monotonic() - self.last_frame >= JPEG_TIMEOUT:
                    await self.close(detach=True)
                    return

    async def frame(self, request):
        if not self.authorized(bearer(request)):
            return error(401, "当前会话无效。")
        stream = request.headers.get("X-Display-Stream")
        if (not self.owns_stream(bearer(request)) or not stream or self.stream != stream or self.pc is not None
                or self.track is None):
            return error(409, "图像会话已结束。")
        if self.frame_lock.locked():
            return error(429, "请等待前一帧完成。")
        async with self.frame_lock:
            await asyncio.sleep(max(0, self.last_frame + JPEG_INTERVAL - time.monotonic()))
            if self.stream != stream:
                return error(409, "图像会话已结束。")
            try:
                captured_at = time.monotonic()
                frame = await self.track.still()
                if self.stream != stream:
                    return error(409, "图像会话已结束。")
                if not self.jpeg or (self.jpeg.width, self.jpeg.height) != (frame.width, frame.height):
                    self.jpeg = av.CodecContext.create("mjpeg", "w")
                    self.jpeg.width, self.jpeg.height = frame.width, frame.height
                    self.jpeg.time_base = Fraction(1, JPEG_FPS)
                    self.jpeg.pix_fmt = "yuvj420p"
                    self.jpeg.thread_count = 4
                packets = self.jpeg.encode(frame)
                data = b"".join(bytes(packet) for packet in packets)
                if not data:
                    raise ValueError("Empty JPEG")
            except Exception as exc:
                LOG.warning("Image capture unavailable: %s", type(exc).__name__)
                async with self.lock:
                    if self.stream == stream:
                        await self.close(detach=True)
                return error(503, "扩展显示器暂不可用。")
            self.last_frame = captured_at
            return web.Response(body=data, content_type="image/jpeg", headers={"Cache-Control": "no-store"})

    async def playing(self, request):
        if not self.authorized(bearer(request)):
            return error(401, "当前会话无效。")
        try:
            payload = await request.json()
        except (ValueError, UnicodeError):
            return error(400, "播放状态无效。")
        if not isinstance(payload, dict):
            return error(400, "播放状态无效。")
        width, height = payload.get("width"), payload.get("height")
        platform = payload.get("platform")
        if (type(width) is not int or type(height) is not int
                or not (0 < width <= 16384 and 0 < height <= 16384)
                or platform not in ("iPad", "browser")):
            return error(400, "播放尺寸或平台无效。")
        if (not self.owns_stream(bearer(request)) or self.track is None
                or not self.stream or self.stream != payload.get("stream")):
            return error(409, "视频会话已结束。")
        now = int(time.time())
        if self.playback.get("state") == "playing" and self.playback["updated_at"] >= now:
            return web.Response(status=204)
        self.playback.setdefault("played_at", now)
        self.playback.update(platform=platform, width=width, height=height)
        for key, choices in (("fullscreen", ("native", "standalone", "windowed")), ("fit", ("contain", "cover"))):
            if payload.get(key) in choices:
                self.playback[key] = payload[key]
        viewport = viewport_of(payload)
        if viewport:
            self.playback["viewport"] = viewport
        stats = payload.get("stats")
        if isinstance(stats, dict):
            self.playback["stats"] = {k: round(stats[k], 2) for k, upper in
                                      (("fps", 240), ("jitterMs", 60000), ("rttMs", 60000))
                                      if numeric(stats.get(k), 0, upper)}
        self.publish_state("playing")
        return web.Response(status=204)

    async def disconnect(self, request):
        if not self.authorized(bearer(request)):
            return error(401, "当前会话无效。")
        async with self.lock:
            if self.track and not self.owns_stream(bearer(request)):
                return error(409, "另一台接收端正在显示副屏。")
            await self.close(detach=True)
        return web.Response(status=204)

    def application(self):
        app = web.Application(client_max_size=128 * 1024)
        app.router.add_get("/api/status", self.status)
        app.router.add_post("/api/offer", self.offer)
        app.router.add_post("/api/compat", self.offer)
        app.router.add_get("/api/frame", self.frame)
        app.router.add_post("/api/playing", self.playing)
        app.router.add_delete("/api/session", self.disconnect)
        async def index(request):
            return web.FileResponse(WEB / "index.html", headers={"Cache-Control": "no-cache"})
        app.router.add_get("/", index)
        app.router.add_static("/", WEB, show_index=False)
        async def cleanup(app):
            await self.close(detach=True)
        app.on_cleanup.append(cleanup)
        return app


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--list-displays", action="store_true")
    parser.add_argument("--bind", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8453)
    parser.add_argument("--cert", type=Path)
    parser.add_argument("--key", type=Path)
    parser.add_argument("--token-file", type=Path)
    parser.add_argument("--approval-dir", type=Path)
    parser.add_argument("--manage-display", action="store_true",
                        help="attach the owned virtual screen on connect and detach on disconnect")
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--display", default="auto", help="auto or exact monitor unique_id")
    group.add_argument("--demo", action="store_true", help="only send a synthetic test pattern")
    args = parser.parse_args()
    if args.list_displays:
        print(json.dumps(displays(), indent=2, ensure_ascii=True))
        return
    if not all((args.cert, args.key, args.token_file)):
        parser.error("--cert, --key and --token-file are required")
    try:
        bind = str(ipaddress.IPv4Address(args.bind))
        if bind == "0.0.0.0":
            raise ValueError("Select one LAN adapter's IPv4 address")
        token = args.token_file.read_text(encoding="utf-8-sig").strip()
        if not re.fullmatch(r"[a-zA-Z0-9_-]{32,}", token):
            raise ValueError("Token file requires a URL-safe random secret of at least 32 characters")
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.minimum_version = ssl.TLSVersion.TLSv1_2
        context.load_cert_chain(args.cert, args.key)
    except (OSError, ValueError) as exc:
        parser.error(str(exc))
    # aioice currently has no public per-interface option. Restrict candidate
    # gathering before creating any peer, so UDP sockets bind only to our
    # chosen LAN (also keeps tunnel and VPN addresses out of signaling).
    aioice.ice.get_host_addresses = lambda use_ipv4, use_ipv6: [bind] if use_ipv4 else []
    logging.basicConfig(level=logging.WARNING)
    host = Host(token, args.display, args.demo, args.approval_dir, args.manage_display,
                args.token_file.stat().st_mtime + 600)
    web.run_app(host.application(), host=bind, port=args.port, ssl_context=context,
                access_log=None)


if __name__ == "__main__":
    main()
