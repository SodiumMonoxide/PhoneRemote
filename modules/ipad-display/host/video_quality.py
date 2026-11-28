"""Negotiate an H.264 envelope and use a low-delay desktop encoder within it."""

import logging
import math
from dataclasses import dataclass
from fractions import Fraction

import av
import aiortc.codecs
from aiortc import RTCSessionDescription, sdp
from aiortc.codecs.h264 import H264Encoder


# H.264 Annex A, Baseline: MaxFS, MaxMBPS, MaxBR (kbit/s).
LEVEL_LIMITS = {
    10: (99, 1485, 64), 11: (396, 3000, 192), 12: (396, 6000, 384),
    13: (396, 11880, 768), 20: (396, 11880, 2000), 21: (792, 19800, 4000),
    22: (1620, 20250, 4000), 30: (1620, 40500, 10000), 31: (3600, 108000, 14000),
    32: (5120, 216000, 20000), 40: (8192, 245760, 20000), 41: (8192, 245760, 50000),
    42: (8704, 522240, 50000), 50: (22080, 589824, 135000), 51: (36864, 983040, 240000),
}
SUPPORTED = {sdp.H264Profile.PROFILE_BASELINE, sdp.H264Profile.PROFILE_CONSTRAINED_BASELINE}


@dataclass(frozen=True)
class VideoMode:
    width: int
    height: int
    fps: int
    level: int
    max_bitrate: int

    @property
    def initial_bitrate(self):
        return min(self.max_bitrate, max(500_000, round(self.width * self.height * self.fps * 0.09)))


def choose_offer(raw_sdp, width, height, hz):
    """Select only a packetization-mode 1 profile actually offered by the receiver.

    Keep the matching RTX payload and all other SDP fields (ICE, DTLS, RTCP).
    Restrict what we offer aiortc so its sender and wire answer use the same PT.
    """
    parsed = sdp.SessionDescription.parse(raw_sdp)
    media = next(m for m in parsed.media if m.kind == "video")
    choices = []
    for codec in media.rtp.codecs:
        if codec.mimeType.lower() != "video/h264" or str(codec.parameters.get("packetization-mode")) != "1":
            continue
        encoded_id = str(codec.parameters.get("profile-level-id", "42e01f"))
        try:
            profile, offered_level = sdp.parse_h264_profile_level_id(encoded_id)
            level = min(51, int(offered_level))
            if profile not in SUPPORTED or level not in LEVEL_LIMITS:
                continue
            fs, mbps, kbps = LEVEL_LIMITS[level]
            # RFC 6184 §8.1 permits larger pictures and/or rates when explicitly
            # signalled, without raising the receiver's nominal profile-level-id.
            # Keep one reference frame and retain the default DPB envelope.
            dpb = {31: 18000, 32: 20480, 40: 32768, 41: 32768, 42: 34816,
                   50: 110400, 51: 184320}.get(level, fs)
            fs = min(dpb, LEVEL_LIMITS[51][0], int(codec.parameters.get("max-fs", fs)))
            mbps = min(LEVEL_LIMITS[51][1], int(codec.parameters.get("max-mbps", mbps)))
            # The desktop stream doesn't require a bitrate above its nominal
            # level even when the receiver permits one.
            kbps = min(kbps, int(codec.parameters.get("max-br", kbps)))
        except (ValueError, TypeError):
            continue
        if min(fs, mbps, kbps) < 1:
            continue
        # Favor native resolution, then 60 Hz. Avoid sacrificing interactive
        # frame rate below 24 Hz solely to retain additional desktop pixels.
        aspect = width / height
        frame_budget = min(fs, max(1, mbps // min(hz, 24)))
        scale = min(1, math.sqrt(frame_budget * 256 / (width * height)))
        out_w = max(2, int(width * scale) // 2 * 2)
        while out_w >= 2:
            out_h = max(2, round(out_w / aspect / 2) * 2)
            blocks = math.ceil(out_w / 16) * math.ceil(out_h / 16)
            if blocks <= frame_budget:
                break
            out_w -= 2
        if out_w < 2:
            continue
        fps = max(1, min(hz, mbps // blocks))
        mode = VideoMode(out_w, out_h, fps, level, min(18_000_000, kbps * 1000))
        choices.append(((out_w * out_h, fps, level), codec, mode))
    if not choices:
        raise ValueError("No supported H.264 receiver profile")
    _, chosen, mode = max(choices, key=lambda item: item[0])
    accepted = [chosen] + [c for c in media.rtp.codecs if c.mimeType.lower() == "video/rtx"
                            and c.parameters.get("apt") == chosen.payloadType]
    media.rtp.codecs = accepted
    media.fmt = [c.payloadType for c in accepted]
    return RTCSessionDescription(str(parsed), "offer"), mode


def local_answer(answer, mode):
    # aiortc advertises a fixed level 3.1. Its RTP packetizer does not use
    # this field; signal the level of the encoder actually installed below.
    parsed = sdp.SessionDescription.parse(answer.sdp)
    for media in parsed.media:
        for codec in media.rtp.codecs:
            if codec.mimeType.lower() == "video/h264":
                profile = str(codec.parameters["profile-level-id"])[:4]
                codec.parameters["profile-level-id"] = profile + format(mode.level, "02x")
    return RTCSessionDescription(str(parsed), "answer")


class DesktopEncoder(H264Encoder):
    """Keep aiortc's H.264 packetization, RTX, PLI and REMB feedback handling."""

    def __init__(self):
        super().__init__()
        self.backend = "h264_nvenc"
        self.mode = None
        self.requested_bitrate = None
        self.active_bitrate = 0

    @property
    def target_bitrate(self):
        if self.mode is None:
            return super().target_bitrate
        requested = self.requested_bitrate or self.mode.initial_bitrate
        return min(self.mode.max_bitrate, max(100_000, requested))

    @target_bitrate.setter
    def target_bitrate(self, value):
        self.requested_bitrate = value

    def _open(self, frame):
        c = av.CodecContext.create(self.backend, "w")
        c.width, c.height = frame.width, frame.height
        c.pix_fmt = "yuv420p"
        c.framerate = Fraction(self.mode.fps)
        c.time_base = Fraction(1, self.mode.fps)
        c.max_b_frames = 0
        c.gop_size = self.mode.fps * 2
        c.bit_rate = self.target_bitrate
        options = {"profile": "baseline", "level": f"{self.mode.level // 10}.{self.mode.level % 10}",
                   "refs": "1", "maxrate": str(c.bit_rate), "bufsize": str(max(32000, c.bit_rate // 10))}
        if self.backend == "h264_nvenc":
            options.update(preset="p4", tune="ull", zerolatency="1", delay="0", rc="cbr", **{"forced-idr": "1"})
        else:
            options.update(preset="veryfast", tune="zerolatency")
        c.options = options
        c.open()
        self.active_bitrate = c.bit_rate
        self.codec = c

    def _encode_frame(self, frame, force_keyframe):
        if not isinstance(frame.opaque, VideoMode):
            yield from super()._encode_frame(frame, force_keyframe)
            return
        self.mode = frame.opaque
        blocks = math.ceil(frame.width / 16) * math.ceil(frame.height / 16)
        fs, mbps, _ = LEVEL_LIMITS[self.mode.level]
        if self.codec is None and (blocks > fs or blocks * self.mode.fps > mbps):
            # NVENC rejects explicit receiver extensions to a nominal level.
            # x264 emits the agreed level byte and honours the advertised limits.
            self.backend = "libx264"
        if self.codec and ((self.codec.width, self.codec.height) != (frame.width, frame.height)
                           or abs(self.target_bitrate - self.active_bitrate) > 0.25 * self.active_bitrate):
            self.reset()
        frame.pict_type = av.video.frame.PictureType.I if force_keyframe else av.video.frame.PictureType.NONE
        try:
            if self.codec is None:
                self._open(frame)
            packets = self.codec.encode(frame)
        except (av.FFmpegError, ValueError):
            if self.backend != "h264_nvenc":
                raise
            logging.getLogger("ipad-display").warning("NVENC unavailable; using libx264")
            self.backend = "libx264"
            self._open(frame)
            frame.pict_type = av.video.frame.PictureType.I
            packets = self.codec.encode(frame)
        for packet in packets:
            yield from self._split_bitstream(bytes(packet))


def install_encoder():
    # Pinned aiortc 1.17 exposes no encoder registration callback. Its H.264
    # factory constructs this module-level class for each new RTP sender.
    aiortc.codecs.H264Encoder = DesktopEncoder
