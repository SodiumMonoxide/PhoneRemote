"""Exercise pairing and real H.264 delivery locally, using only synthetic pixels."""

import asyncio
import hashlib
import io
import json
import secrets
import tempfile
import math
from fractions import Fraction
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import patch

import aioice.ice
import av
from aiohttp.test_utils import TestClient, TestServer
from aiortc import RTCConfiguration, RTCPeerConnection, RTCSessionDescription

from display import DesktopTrack, selected_display
from pairing import daily_code, daily_expiry, load_daily_key
from server import Host, manual_code
from video_quality import DesktopEncoder, choose_offer


def check_selection():
    primary = dict(unique_id="physical", left=0, top=0, width=1920, height=1080, is_primary=True)
    virtual = dict(unique_id="DISPLAY#MTT1337#unit", left=-1600, top=0,
                   width=1600, height=1200, is_primary=False)
    assert selected_display("auto", [primary]) is None
    assert selected_display("physical", [primary, virtual]) is None
    assert selected_display("auto", [primary, virtual]) == virtual
    assert selected_display("auto", [primary, dict(virtual, left=0)]) is None


def check_codec_limits(offer):
    def negotiate(profile, extra=""):
        # The stock aiortc test receiver offers Baseline 3.1 in both variants.
        raw = offer.replace("profile-level-id=42001f", f"profile-level-id=4200{profile}{extra}")
        raw = raw.replace("profile-level-id=42e01f", f"profile-level-id=42e0{profile}{extra}")
        return choose_offer(raw, 1600, 1200, 60)[1]
    basic = negotiate("1f")
    blocks = math.ceil(basic.width / 16) * math.ceil(basic.height / 16)
    assert blocks <= 3600 and blocks * basic.fps <= 108000
    high = negotiate("2a")
    assert (high.width, high.height, high.fps, high.level) == (1600, 1200, 60, 42)
    extended = negotiate("1f", ";max-fs=8192;max-mbps=500000")
    assert (extended.width, extended.height, extended.fps, extended.level) == (1600, 1200, 60, 31)
    capped = negotiate("1f", ";max-fs=1000;max-mbps=15000;max-br=500")
    blocks = math.ceil(capped.width / 16) * math.ceil(capped.height / 16)
    assert blocks <= 1000 and blocks * capped.fps <= 15000 and capped.max_bitrate == 500000
    for mode in (extended, high):
        enc = DesktopEncoder()
        frame = av.VideoFrame(mode.width, mode.height, "yuv420p")
        frame.pts, frame.time_base, frame.opaque = 0, Fraction(1, mode.fps), mode
        nals = list(enc._encode_frame(frame, True))
        sps = next(n for n in nals if n[0] & 31 == 7)
        assert sps[3] == mode.level, "SPS must reflect the receiver's nominal level"
        frame.pts = 1
        assert any(n[0] & 31 == 5 for n in enc._encode_frame(frame, True)), "PLI must force an IDR"
        enc.target_bitrate = 100000000
        assert enc.target_bitrate == mode.max_bitrate


async def test_session(directory):
    seed = secrets.token_urlsafe(32)
    host = Host(seed, "auto", demo=True, approval_dir=directory)
    peer = RTCPeerConnection(RTCConfiguration(iceServers=[]))
    async with TestClient(TestServer(host.application())) as client:
        try:
            peer.addTransceiver("video", direction="recvonly")
            await peer.setLocalDescription(await peer.createOffer())
            offer = {"sdp": peer.localDescription.sdp, "type": "offer"}
            check_codec_limits(offer["sdp"])

            def post(token):
                return client.post("/api/offer", json=offer, headers={"Authorization": "Bearer " + token})

            assert (await post("unauthorized")).status == 401
            # Exercise the same PC-side confirmation protocol as the launcher.
            async def approve(action):
                for _ in range(100):
                    pending = directory / "request.json"
                    if pending.exists():
                        record = json.loads(pending.read_text("utf-8"))
                        (directory / (action + "-" + record["id"])).touch()
                        return
                    await asyncio.sleep(0.02)
                raise TimeoutError("Pairing request did not appear")

            denied, _ = await asyncio.gather(post(seed), approve("decline"))
            assert denied.status == 403
            alias = manual_code(seed)
            assert not Host(seed, "auto", demo=True).valid_initial(alias)
            expired = Host(seed, "auto", demo=True, approval_dir=directory, manual_expires=0)
            assert not expired.valid_initial(alias) and expired.valid_initial(seed)
            wrong_code = "0000000000" if alias != "0000000000" else "1111111111"
            for _ in range(5):
                assert (await post(wrong_code)).status == 401
            assert (await post(alias)).status == 429
            assert (await post("invalid-long-credential")).status == 401
            host.alias_failures.clear()  # Simulate expiry of the rate-limit window.
            response, _ = await asyncio.gather(post(alias), approve("approve"))
            assert response.status == 200, await response.text()
            answer = await response.json()
            await peer.setRemoteDescription(RTCSessionDescription(type="answer", sdp=answer["sdp"]))
            assert "H264/90000" in answer["sdp"] and "VP8/90000" not in answer["sdp"]
            assert (await post(seed)).status == 401
            assert (await post(alias)).status == 401
            receiver = peer.getReceivers()[0]
            for _ in range(4):
                frame = await asyncio.wait_for(receiver.track.recv(), 10)
                assert (frame.width, frame.height) == (640, 360)
            headers = {"Authorization": "Bearer " + answer["session"]}
            receipt = {"stream": answer["stream"], "platform": "browser", "width": frame.width,
                       "height": frame.height}
            async def report(body, credential=headers):
                return await client.post("/api/playing", json=body, headers=credential)
            assert (await report(receipt, {})).status == 401
            assert (await report(dict(receipt, width=0))).status == 400
            assert (await report(dict(receipt, stream="previous"))).status == 409
            metrics = dict(receipt, fullscreen="standalone", fit="contain",
                           viewport={"width": 1024, "height": 768, "dpr": 2},
                           stats={"fps": 58.23, "rttMs": 4.12, "jitterMs": -1, "unexpected": 8})
            assert (await report(metrics)).status == 204
            ledger = directory / "playback.json"
            playing = json.loads(ledger.read_text("utf-8"))
            assert playing["state"] == "playing" and playing["width"] == 640
            assert playing["stats"] == {"fps": 58.23, "rttMs": 4.12} and playing["fullscreen"] == "standalone"
            assert not ({"stream", "session", "ip"} & playing.keys())
            assert (await client.delete("/api/session", headers=headers)).status == 204
            assert json.loads(ledger.read_text("utf-8"))["state"] == "closed"
            assert (await report(receipt)).status == 409
            assert await (await client.get("/api/status")).json() == {
                "ready": True, "mode": "demo", "display": "测试画面"}
            assert (await post(answer["session"])).status == 200
            compat = await client.post("/api/compat", json={}, headers=headers)
            assert compat.status == 200
            jpg_session = await compat.json()
            frame_headers = dict(headers, **{"X-Display-Stream": jpg_session["stream"]})
            assert (await client.get("/api/frame")).status == 401
            assert (await client.get("/api/frame", headers=headers)).status == 409
            response = await client.get("/api/frame", headers=frame_headers)
            assert response.status == 200 and response.content_type == "image/jpeg"
            with av.open(io.BytesIO(await response.read()), format="mjpeg") as container:
                frame = next(container.decode(video=0))
                assert (frame.width, frame.height) == (640, 360)
            receipt["stream"] = jpg_session["stream"]
            assert (await report(receipt)).status == 204
            assert json.loads(ledger.read_text())["transport"] == "https-jpeg"
            restored = Host(seed, "auto", demo=True, approval_dir=directory)
            assert restored.authorized(answer["session"]) and restored.token is None
            rotated = Host(secrets.token_urlsafe(32), "auto", demo=True, approval_dir=directory)
            assert not rotated.authorized(answer["session"]) and rotated.token is not None
            with patch("server.JPEG_TIMEOUT", 0.1):
                await asyncio.wait_for(host.expire_frames(jpg_session["stream"]), timeout=2)
            assert host.track is None and (await client.get("/api/frame", headers=frame_headers)).status == 409
        finally:
            await peer.close()


async def test_soft_display():
    # Exercise session ownership without changing any of the user's displays.
    seed = secrets.token_urlsafe(32)
    host = Host(seed, "auto", manage_display=True)
    state = {"attached": False, "actions": []}
    monitor = {"unique_id": "DISPLAY#MTT1337#test", "width": 1600, "height": 1200}
    host.monitor = lambda: monitor if state["attached"] else None
    async def layout(action):
        if action != "status":
            state["actions"].append(action)
            state["attached"] = action == "attach"
        return dict(available=True, managed=True, attached=state["attached"])
    host.layout = layout
    peer = RTCPeerConnection(RTCConfiguration(iceServers=[]))
    with patch("server.DesktopTrack", side_effect=lambda identity: DesktopTrack(None)), \
            patch("server.selected_display", return_value=monitor), patch("server.displays", return_value=[]):
        async with TestClient(TestServer(host.application())) as client:
            try:
                assert (await (await client.get("/api/status")).json())["ready"] is True
                peer.addTransceiver("video", direction="recvonly")
                await peer.setLocalDescription(await peer.createOffer())
                payload = {"sdp": peer.localDescription.sdp, "type": "offer"}
                async def connect(credential):
                    return await client.post("/api/offer", json=payload,
                                             headers={"Authorization": "Bearer " + credential})
                assert (await connect("invalid")).status == 401 and not state["actions"]
                response = await connect(seed)
                assert response.status == 200 and state["actions"] == ["attach"]
                session = (await response.json())["session"]
                assert (await connect(session)).status == 200 and state["actions"] == ["attach"]
                await client.delete("/api/session", headers={"Authorization": "Bearer " + session})
                assert state["actions"] == ["attach", "detach"]
                assert (await (await client.get("/api/status")).json())["ready"] is True
                assert (await connect(session)).status == 200
                assert state["actions"] == ["attach", "detach", "attach"]
            finally:
                await peer.close()


async def test_daily(state):
    with ThreadPoolExecutor(8) as workers:
        keys = list(workers.map(lambda _: load_daily_key(state), range(16)))
    assert len(set(keys)) == 1 and list(state.iterdir()) == [state / "daily-key.bin"]
    before = datetime(2026, 11, 28, 15, 59, 59, tzinfo=timezone.utc)
    midnight = before + timedelta(seconds=1)
    known = bytes(range(32))
    assert daily_code(known, before) == daily_code(known, before - timedelta(hours=20))
    assert daily_expiry(before) == midnight
    assert daily_code(known, before) != daily_code(known, midnight)
    assert daily_expiry(midnight) == midnight + timedelta(days=1)

    seed = secrets.token_urlsafe(32)
    key = keys[0]
    code = daily_code(key)
    assert len(code) == 6 and code.isdigit()
    assert not Host(seed, "auto", demo=True).valid_initial(code)
    approval = state / "approval"
    approval.mkdir()
    # Reproduce the original on-disk single receiver record.
    original = secrets.token_urlsafe(32)
    digest = lambda value: hashlib.sha256(value.encode()).hexdigest()
    (approval / "paired-receiver.json").write_text(
        json.dumps({"pair": digest(seed), "receiver": digest(original)}), encoding="utf-8")
    host = Host(seed, "auto", demo=True, approval_dir=approval)
    async with TestClient(TestServer(host.application())) as client:
        async def start(secret):
            return await client.post("/api/compat", headers={"Authorization": "Bearer " + secret}, json={})
        async def allow():
            pending = approval / "request.json"
            for _ in range(150):
                if pending.is_file():
                    token = json.loads(pending.read_text())["id"]
                    (approval / ("approve-" + token)).touch()
                    return
                await asyncio.sleep(0.01)
            raise AssertionError("Expected explicit PC confirmation")
        assert host.authorized(original) and not host.valid_initial(seed)
        assert (await start(original)).status == 200
        # Simulate a PIN that rolls over while the user is confirming on PC.
        old_approval = host.approve
        async def advance(remote):
            with_expired_day.return_value = "invalid"
            return True
        with patch("server.daily_code", return_value=code) as with_expired_day, patch.object(host, "approve", advance):
            assert (await start(code)).status == 401
        assert host.approve == old_approval and host.owns_stream(original)
        clients = []
        for _ in range(2):
            response, _ = await asyncio.gather(start(code), allow())
            assert response.status == 200
            clients.append(await response.json())
        previous, current = clients
        assert host.authorized(previous["session"]) and host.authorized(original)
        stale = {"Authorization": "Bearer " + previous["session"], "X-Display-Stream": current["stream"]}
        assert (await client.get("/api/frame", headers=stale)).status == 409
        assert (await client.delete("/api/session", headers=stale)).status == 409
        assert host.owns_stream(current["session"])
        wrong = "000000" if code != "000000" else "111111"
        for _ in range(5):
            assert (await start(wrong)).status == 401
        assert (await start(code)).status == 429
        assert (await start(original)).status == 200
    restarted = Host(seed, "auto", demo=True, approval_dir=approval)
    assert all(restarted.authorized(c) for c in (original, previous["session"], current["session"]))
    assert restarted.daily_key == key and restarted.valid_initial(code)
    reset = Host(secrets.token_urlsafe(32), "auto", demo=True, approval_dir=approval)
    assert reset.daily_key == key and reset.valid_initial(code) and not reset.authorized(original)


if __name__ == "__main__":
    check_selection()
    aioice.ice.get_host_addresses = lambda use_ipv4, use_ipv6: ["127.0.0.1"] if use_ipv4 else []
    with tempfile.TemporaryDirectory() as temp:
        asyncio.run(test_session(Path(temp) / "first" / "approval"))
        asyncio.run(test_daily(Path(temp) / "daily"))
    asyncio.run(test_soft_display())
    print("Passed: secondary-only selection, PC approval, token replay rejection, H.264 decode, "
          "private playback receipt, reconnect, JPEG decode, pairing persistence, idle expiry, "
          "daily PIN rollover, repeat approval and active session isolation.")
