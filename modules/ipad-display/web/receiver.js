"use strict";

const el = (id) => document.getElementById(id);
const video = el("screen");
const canvas = el("frames");
const stage = el("stage");
const guide = el("guide");
const SESSION = "ipad-screen.session";
const PAIR = "ipad-screen.pending";
const FIT = "ipad-screen.fit";
const isIPad = /iPad/i.test(navigator.userAgent) ||
  (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);

function readStored(storeName, key) {
  try { return window[storeName].getItem(key) || ""; } catch { return ""; }
}

function store(storeName, key, value) {
  try {
    if (value) window[storeName].setItem(key, value);
    else window[storeName].removeItem(key);
  } catch { /* The current connection also keeps its credentials in memory. */ }
}

function readLinkCode() {
  const fragment = new URLSearchParams(location.hash.slice(1));
  if (!fragment.has("token")) return "";
  history.replaceState(null, "", location.pathname + location.search);
  const code = fragment.get("token");
  return validPairValue(code) ? code : "";
}

let scan = readLinkCode();
let session = readStored("localStorage", SESSION);
const pending = readStored("sessionStorage", PAIR);
let pairing = session ? "" : scan || (validPairValue(pending) ? pending : "");
let changingPair = false;
store("sessionStorage", PAIR, pairing);

let peer = null;
let streamId = null;
let reportedPeer = null;
let requestInFlight = false;
let lossTimer;
let startupTimer;
let toolbarTimer;
let demo = false;
let connectionVersion = 0;
let metricTimer, geometryTimer;
let measuringVersion = null;
let lastVideoSample = null;
let lastCanvasSample = null;
let paintedFrames = 0;

function viewportHint() {
  const viewport = window.visualViewport;
  return {
    width: Math.round(viewport?.width || window.innerWidth),
    height: Math.round(viewport?.height || window.innerHeight),
    dpr: window.devicePixelRatio || 1
  };
}

function hasElementFullscreen() {
  return Boolean(document.fullscreenElement || document.webkitFullscreenElement);
}

function fullscreenMode() {
  if (hasElementFullscreen() || video.webkitDisplayingFullscreen ||
      matchMedia("(display-mode: fullscreen)").matches) return "native";
  if (!(navigator.standalone || matchMedia("(display-mode: standalone)").matches)) return "windowed";
  const viewport = viewportHint();
  const [vw, vh] = [viewport.width, viewport.height].sort((a, b) => a - b);
  const [sw, sh] = [screen.width, screen.height].sort((a, b) => a - b);
  return sw && sh && vw / sw >= .95 && vh / sh >= .95 ? "standalone" : "windowed";
}

function setFit(mode) {
  const cover = mode === "cover";
  stage.dataset.fit = cover ? "cover" : "contain";
  el("fit").setAttribute("aria-pressed", String(cover));
  el("fit").title = cover ? "恢复完整桌面，保留全部边缘" : "填满屏幕，两端可能被裁切";
  scheduleGeometryReport();
}

function message(text, error = false) {
  el("message").textContent = text;
  el("message").classList.toggle("error", error);
}

function setLobby(status, text, error = false) {
  el("state-label").textContent = status;
  el("pair-field").hidden = Boolean(pairing || (session && !changingPair));
  el("copy-link").hidden = !pairing;
  el("new-pair").hidden = !el("pair-field").hidden;
  el("use-saved").hidden = !changingPair || !session;
  message(text, error);
}

function validPairValue(value) {
  if (typeof value !== "string") return false;
  if (/^\d+$/.test(value)) return /^(?:\d{6}|\d{10}|\d{32,256})$/.test(value);
  return /^[A-Za-z0-9_-]{8,256}$/.test(value);
}

function parsePairing(text) {
  const trimmed = text.trim();
  if (validPairValue(trimmed)) return trimmed;
  let url;
  try { url = new URL(trimmed); } catch { throw new Error("请输入电脑上的 6 位今日配对码，也可以粘贴完整配对链接。"); }
  if (url.origin !== location.origin) throw new Error("此链接属于另一个地址，请打开该链接对应的接收页。");
  const token = new URLSearchParams(url.hash.slice(1)).get("token");
  if (!validPairValue(token)) throw new Error("链接中的配对码无效，请输入电脑上的 6 位今日配对码。");
  return token;
}

async function getStatus() {
  const response = await timedFetch("/api/status", { cache: "no-store" }, 5000);
  if (!response.ok) throw new Error("电脑端未就绪，请确认发送端仍在运行。");
  const info = await response.json();
  demo = info.mode === "demo";
  const title = demo ? "测试画面" : "Windows 扩展桌面";
  el("source-label").textContent = info.display && info.display !== title ? title + " · " + info.display : title;
  el("live-label").lastChild.textContent = demo ? "测试画面" : "扩展桌面";
  if (!info.ready) throw new Error("电脑尚未启用副屏。请在 Windows 中启用扩展显示器，再试一次。");
}

async function timedFetch(url, options, timeout) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeout);
  try {
    return await fetch(url, { ...options, signal: controller.signal });
  } catch {
    throw new Error("无法联系电脑。请确认 iPad 与电脑连接到同一局域网，并检查电脑端服务。");
  } finally {
    clearTimeout(timer);
  }
}

function waitForIce(connection) {
  if (connection.iceGatheringState === "complete") return Promise.resolve();
  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      connection.removeEventListener("icegatheringstatechange", check);
      reject(new Error("未能取得本地网络地址，请检查 Safari 的本地网络权限后重试。"));
    }, 9000);
    function check() {
      if (connection.iceGatheringState !== "complete") return;
      connection.removeEventListener("icegatheringstatechange", check);
      clearTimeout(timeout);
      resolve();
    }
    connection.addEventListener("icegatheringstatechange", check);
    check();
  });
}

function showToolbar() {
  clearTimeout(toolbarTimer);
  el("toolbar").hidden = false;
  if (!guide.hasAttribute("open") && el("play").hidden && el("notice").hidden) {
    toolbarTimer = setTimeout(() => {
      if (!el("toolbar").querySelector(":focus-visible")) el("toolbar").hidden = true;
    }, 3600);
  }
}

function resetPeer() {
  clearTimeout(lossTimer);
  clearTimeout(startupTimer);
  clearTimeout(toolbarTimer);
  clearInterval(metricTimer);
  clearTimeout(geometryTimer);
  metricTimer = null;
  lastVideoSample = lastCanvasSample = null;
  paintedFrames = 0;
  const previous = peer;
  peer = null;
  streamId = null;
  reportedPeer = null;
  if (previous) previous.close();
  if (video.webkitDisplayingFullscreen) video.webkitExitFullscreen?.();
  video.srcObject = null;
  video.hidden = false;
  canvas.hidden = true;
  canvas.width = canvas.height = 0;
  stage.hidden = true;
  document.documentElement.classList.remove("watching");
  el("lobby").hidden = false;
  el("notice").hidden = true;
  el("play").hidden = true;
  el("stage-compat").hidden = true;
  el("compat").disabled = false;
  el("new-pair").disabled = false;
  el("use-saved").disabled = false;
  el("connect").disabled = false;
  el("connect").textContent = "重新连接";
}

function failed(text) {
  connectionVersion++;
  requestInFlight = false;
  resetPeer();
  if (hasElementFullscreen()) leaveFullscreen().catch(() => {});
  setLobby("已断开", text, true);
}

function monitorConnection(connection) {
  const changed = () => {
    if (peer !== connection) return;
    if (connection.iceConnectionState === "failed" || connection.connectionState === "failed") {
      fallback(connection);
    } else if (connection.iceConnectionState === "disconnected" || connection.connectionState === "disconnected") {
      clearTimeout(lossTimer);
      lossTimer = setTimeout(() => {
        fallback(connection);
      }, 5500);
    } else {
      clearTimeout(lossTimer);
    }
  };
  connection.addEventListener("connectionstatechange", changed);
  connection.addEventListener("iceconnectionstatechange", changed);
}

function startWatching() {
  el("lobby").hidden = true;
  stage.hidden = false;
  document.documentElement.classList.add("watching");
  updateFullscreenLabel();
  showToolbar();
}

async function playVideo(connection) {
  try {
    await video.play();
  } catch {
    if (peer !== connection) return;
    el("play").hidden = false;
    el("notice").hidden = false;
    el("notice").textContent = "点击“播放”开始显示电脑画面。";
    startWatching();
  }
}

function fallback(connection) {
  if (!connection || peer !== connection) return;
  requestInFlight = false;
  connect(null, true);
}

function clearPairing() {
  pairing = "";
  store("sessionStorage", PAIR, "");
  el("manual-copy").hidden = true;
  el("manual-copy").value = "";
  el("copy-link").textContent = "复制配对链接";
}

function forgetSession() {
  session = "";
  store("localStorage", SESSION, "");
}

async function authorize(path, payload) {
  const version = connectionVersion;
  const saved = Boolean(session && !changingPair);
  const credential = saved ? session : pairing;
  if (!saved) message("正在配对，请留意电脑端的连接提示。");
  const response = await timedFetch(path, {
    method: "POST", cache: "no-store",
    headers: { "Authorization": "Bearer " + credential, "Content-Type": "application/json" },
    body: JSON.stringify(payload)
  }, 60000);
  if (version !== connectionVersion) return null;
  if (response.status === 401) {
    if (saved) forgetSession();
    clearPairing();
    el("pair-link").value = saved ? scan : "";
    scan = "";
    throw new Error(saved ? "保存的配对已失效。请输入电脑上的今日配对码。" :
      "配对码无效或已过期。请核对电脑上显示的 6 位今日配对码。");
  }
  if (response.status === 403) throw new Error("电脑未批准此次连接。可以重新连接，并在电脑上确认。");
  if (response.status === 409) throw new Error("电脑正在处理连接，请稍后重试。");
  if (response.status === 429) throw new Error("尝试次数过多，请稍后再连接。");
  if (response.status === 404) throw new Error("此电脑端尚未提供兼容连接，请更新电脑端。");
  if (!response.ok) throw new Error("电脑未能建立画面连接，请查看发送端状态并重试。");
  return response.json();
}

function rememberAnswer(answer) {
  if (typeof answer.session !== "string" || !answer.session) throw new Error("电脑返回的配对信息不完整。");
  session = answer.session;
  streamId = answer.stream;
  store("localStorage", SESSION, session);
  changingPair = false;
  scan = "";
  clearPairing();
  el("copy-link").hidden = true;
}

async function showJpegFrames(version, fps) {
  const context = canvas.getContext("2d", { alpha: false });
  let first = true;
  while (version === connectionVersion) {
    const started = performance.now();
    const response = await timedFetch("/api/frame", {
      cache: "no-store", headers: { Authorization: "Bearer " + session, "X-Display-Stream": streamId }
    }, 9000);
    if (version !== connectionVersion) return;
    if (response.status === 401) forgetSession();
    if (!response.ok) throw new Error("兼容画面已中断，请重新连接。");
    const blob = await response.blob();
    const objectUrl = URL.createObjectURL(blob);
    try {
      const image = new Image();
      await new Promise((resolve, reject) => {
        image.onload = resolve;
        image.onerror = () => reject(new Error("iPad 未能解码电脑送来的图像。"));
        image.src = objectUrl;
      });
      if (version !== connectionVersion) return;
      if (canvas.width !== image.naturalWidth || canvas.height !== image.naturalHeight) {
        canvas.width = image.naturalWidth;
        canvas.height = image.naturalHeight;
      }
      context.drawImage(image, 0, 0);
      paintedFrames++;
      if (first) {
        first = false;
        el("notice").hidden = true;
        showToolbar();
        // Wait through a paint before announcing that the new canvas is displaying.
        requestAnimationFrame(() => requestAnimationFrame(() => {
          if (version === connectionVersion) reportSize(canvas.width, canvas.height);
        }));
      }
    } finally {
      URL.revokeObjectURL(objectUrl);
    }
    const wait = 1000 / fps - (performance.now() - started);
    if (wait > 1) await new Promise(resolve => setTimeout(resolve, wait));
  }
}

async function connect(event, compatibility = false) {
  if (event) event.preventDefault();
  if (requestInFlight) return;
  requestInFlight = true;
  const version = ++connectionVersion;
  resetPeer();
  el("connect").disabled = true;
  el("compat").disabled = true;
  el("new-pair").disabled = true;
  el("use-saved").disabled = true;
  el("connect").textContent = "连接中…";
  try {
    if (!window.isSecureContext) throw new Error("请通过电脑提供的受信任 HTTPS 地址打开接收页。");
    if (!pairing && (!session || changingPair)) {
      pairing = parsePairing(el("pair-link").value);
      store("sessionStorage", PAIR, pairing);
      el("pair-link").value = "";
    }
    // iPad Safari grants fullscreen only inside a user gesture. Request it
    // before signaling so the reported viewport matches the playback area.
    if (isIPad && event?.isTrusted && !hasElementFullscreen()) {
      try { await enterElementFullscreen(); } catch { /* The toolbar can retry. */ }
    }
    setLobby("连接中", compatibility ? "正在通过 HTTPS 建立兼容连接…" : "正在与电脑建立本地连接…");
    await getStatus();
    if (version !== connectionVersion) return;
    if (compatibility || !window.RTCPeerConnection) {
      const answer = await authorize("/api/compat", { viewport: viewportHint() });
      if (version !== connectionVersion) return;
      rememberAnswer(answer);
      video.hidden = true;
      canvas.hidden = false;
      el("live-label").lastChild.textContent = demo ? "测试画面 · 兼容" : "扩展桌面 · 兼容";
      el("notice").textContent = "正在接收第一张电脑画面…";
      el("notice").hidden = false;
      startWatching();
      const fps = Number.isFinite(answer.fps) ? Math.max(1, Math.min(30, answer.fps)) : 12;
      await showJpegFrames(version, fps);
      return;
    }
    const connection = new RTCPeerConnection({ iceServers: [] });
    peer = connection;
    monitorConnection(connection);
    const { receiver } = connection.addTransceiver("video", { direction: "recvonly" });
    if ("jitterBufferTarget" in receiver) {
      try { receiver.jitterBufferTarget = 0; } catch { /* Use the browser default. */ }
    }
    connection.addEventListener("track", (event) => {
      if (peer !== connection) return;
      el("notice").textContent = "正在等待电脑画面。若 Safari 提示需要操作，请点“播放”。";
      el("notice").hidden = false;
      el("play").hidden = false;
      el("stage-compat").hidden = false;
      startWatching();
      video.muted = true;
      video.playsInline = true;
      video.srcObject = event.streams[0] || new MediaStream([event.track]);
      playVideo(connection);
    });
    await connection.setLocalDescription(await connection.createOffer());
    try { await waitForIce(connection); }
    catch { fallback(connection); return; }
    if (version !== connectionVersion) return;
    const answer = await authorize("/api/offer", {
      type: "offer", sdp: connection.localDescription.sdp, viewport: viewportHint()
    });
    if (version !== connectionVersion) return;
    rememberAnswer(answer);
    startupTimer = setTimeout(() => {
      if (reportedPeer !== connection) fallback(connection);
    }, 10500);
    try { await connection.setRemoteDescription({ type: answer.type, sdp: answer.sdp }); }
    catch { fallback(connection); return; }
    if (peer === connection) message("正在等待电脑的第一帧画面…");
  } catch (error) {
    if (version === connectionVersion) failed(error.message || "连接失败，请重试。");
  } finally {
    if (version === connectionVersion) requestInFlight = false;
  }
}

async function disconnect() {
  connectionVersion++;
  requestInFlight = true;
  resetPeer();
  if (hasElementFullscreen()) leaveFullscreen().catch(() => {});
  el("connect").disabled = true;
  setLobby("正在断开", "正在释放连接…");
  try {
    if (session) await timedFetch("/api/session", {
      method: "DELETE", headers: { "Authorization": "Bearer " + session }
    }, 3000);
  } catch { /* Closing the peer also disconnects its media transports. */ }
  requestInFlight = false;
  el("connect").disabled = false;
  setLobby("已断开", "配对信息已保存在此设备，可以重新连接。");
}

el("connect-form").addEventListener("submit", connect);
el("compat").addEventListener("click", (event) => connect(event, true));
el("stage-compat").addEventListener("click", () => fallback(peer));
el("new-pair").addEventListener("click", () => {
  changingPair = true;
  clearPairing();
  el("pair-link").value = scan;
  scan = "";
  setLobby("需要配对", "输入电脑上的今日配对码，或粘贴这台电脑的配对链接。");
  el("pair-link").focus();
  el("pair-link").select();
});
el("use-saved").addEventListener("click", () => {
  changingPair = false;
  clearPairing();
  el("pair-link").value = "";
  setLobby("已配对", "这台 iPad 已保存配对，点“连接屏幕”即可使用。");
});
el("disconnect").addEventListener("click", disconnect);

function openGuide() {
  clearTimeout(toolbarTimer);
  if (guide.showModal) guide.showModal();
  else {
    guide.setAttribute("open", "");
    el("close-guide").focus();
  }
  guide.querySelector(".guide-body").scrollTop = 0;
}

function returnFromGuide() {
  if (!stage.hidden) showToolbar();
  el(stage.hidden ? "lobby-guide" : "stage-guide").focus();
}

el("lobby-guide").addEventListener("click", openGuide);
el("stage-guide").addEventListener("click", openGuide);
el("close-guide").addEventListener("click", () => {
  if (guide.close) guide.close();
  else {
    guide.removeAttribute("open");
    returnFromGuide();
  }
});
guide.addEventListener("close", returnFromGuide);

function addMetric(result, key, value, limit) {
  if (Number.isFinite(value) && value >= 0 && value <= limit) {
    result[key] = Math.round(value * 10) / 10;
  }
}

async function measurePlayback(connection, version) {
  const stats = {};
  if (!connection) {
    const now = performance.now();
    if (lastCanvasSample && now - lastCanvasSample.time >= 1000) {
      addMetric(stats, "fps", 1000 * (paintedFrames - lastCanvasSample.count) / (now - lastCanvasSample.time), 240);
    }
    if (!lastCanvasSample || now - lastCanvasSample.time >= 1000) {
      lastCanvasSample = { count: paintedFrames, time: now };
    }
    return stats;
  }
  const report = await connection.getStats();
  if (version !== connectionVersion) return stats;
  let inbound, pair;
  for (const item of report.values()) {
    if (item.type === "inbound-rtp" && (item.kind || item.mediaType) === "video") inbound = item;
    if (item.type === "transport" && item.selectedCandidatePairId) pair = report.get(item.selectedCandidatePairId);
    if (!pair && item.type === "candidate-pair" && item.nominated && item.state === "succeeded") pair = item;
  }
  if (pair) addMetric(stats, "rttMs", 1000 * pair.currentRoundTripTime, 60000);
  if (!inbound) return stats;
  const prev = lastVideoSample;
  if (prev?.id === inbound.id && inbound.timestamp - prev.timestamp >= 1000) {
    addMetric(stats, "fps", 1000 * (inbound.framesDecoded - prev.framesDecoded) /
      (inbound.timestamp - prev.timestamp), 240);
    const emitted = inbound.jitterBufferEmittedCount - prev.jitterBufferEmittedCount;
    if (emitted > 0) addMetric(stats, "jitterMs", 1000 * (inbound.jitterBufferDelay - prev.jitterBufferDelay) / emitted, 60000);
  }
  if (!prev || prev.id !== inbound.id || inbound.timestamp - prev.timestamp >= 1000) lastVideoSample = inbound;
  return stats;
}

async function sendPlaybackStatus(version) {
  if (version !== connectionVersion || !streamId || document.hidden || measuringVersion === version) return;
  const width = canvas.hidden ? video.videoWidth : canvas.width;
  const height = canvas.hidden ? video.videoHeight : canvas.height;
  if (!width || !height) return;
  measuringVersion = version;
  try {
    let stats = {};
    try { stats = await measurePlayback(peer, version); } catch { /* Some Safari versions omit stats. */ }
    if (version !== connectionVersion || document.hidden) return;
    await timedFetch("/api/playing", {
      method: "POST", cache: "no-store",
      headers: { "Authorization": "Bearer " + session, "Content-Type": "application/json" },
      body: JSON.stringify({
        stream: streamId, width, height, platform: isIPad ? "iPad" : "browser",
        fullscreen: fullscreenMode(), fit: stage.dataset.fit, viewport: viewportHint(), stats
      })
    }, 4000);
  } catch { /* Receiver status is independent of playback. */ }
  finally { if (measuringVersion === version) measuringVersion = null; }
}

function reportSize(width, height) {
  if (!streamId || !width || !height || metricTimer) return;
  const version = connectionVersion;
  sendPlaybackStatus(version);
  metricTimer = setInterval(() => sendPlaybackStatus(version), 5000);
}

function scheduleGeometryReport() {
  if (!metricTimer || document.hidden) return;
  clearTimeout(geometryTimer);
  const version = connectionVersion;
  geometryTimer = setTimeout(() => sendPlaybackStatus(version), 1250);
}

function confirmPlayback(connection) {
  if (reportedPeer === connection) return;
  const report = () => {
    if (peer !== connection || reportedPeer === connection || video.paused) return;
    const width = video.videoWidth;
    const height = video.videoHeight;
    if (!width || !height || video.readyState < 2) return;
    reportedPeer = connection;
    clearTimeout(startupTimer);
    reportSize(width, height);
  };
  if (video.requestVideoFrameCallback) video.requestVideoFrameCallback(report);
  else requestAnimationFrame(report);
}

video.addEventListener("playing", () => {
  if (!peer || !video.srcObject) return;
  el("play").hidden = true;
  el("stage-compat").hidden = true;
  el("notice").hidden = true;
  startWatching();
  confirmPlayback(peer);
});
function toggleToolbar() {
  if (el("toolbar").hidden || !el("play").hidden || !el("notice").hidden) showToolbar();
  else el("toolbar").hidden = true;
}
video.addEventListener("click", toggleToolbar);
canvas.addEventListener("click", toggleToolbar);
el("fit").addEventListener("click", () => {
  setFit(stage.dataset.fit === "contain" ? "cover" : "contain");
  store("localStorage", FIT, stage.dataset.fit);
  showToolbar();
});
setFit(readStored("localStorage", FIT) === "cover" ? "cover" : "contain");
el("play").addEventListener("click", () => { if (peer) playVideo(peer); });
document.addEventListener("keydown", () => { if (!stage.hidden) showToolbar(); });
async function enterElementFullscreen() {
  const root = document.documentElement;
  if (document.fullscreenEnabled && root.requestFullscreen) await root.requestFullscreen({ navigationUI: "hide" });
  else if (document.webkitFullscreenEnabled && root.webkitRequestFullscreen) await root.webkitRequestFullscreen();
  else throw new Error();
}

async function leaveFullscreen() {
  if (document.fullscreenElement) await document.exitFullscreen();
  else if (document.webkitFullscreenElement) await document.webkitExitFullscreen();
  else if (video.webkitDisplayingFullscreen) video.webkitExitFullscreen();
}

el("full").addEventListener("click", async () => {
  try {
    if (hasElementFullscreen() || video.webkitDisplayingFullscreen) await leaveFullscreen();
    else if (document.fullscreenEnabled || document.webkitFullscreenEnabled) await enterElementFullscreen();
    else if (!video.hidden && video.webkitEnterFullscreen) video.webkitEnterFullscreen();
    else throw new Error();
  } catch {
    el("notice").hidden = false;
    el("notice").textContent = "请从主屏幕启动副屏，并在 iPad 的窗口菜单中选择全屏。";
  }
  showToolbar();
});
function updateFullscreenLabel() {
  const active = hasElementFullscreen() || video.webkitDisplayingFullscreen;
  el("full").textContent = active ? "退出全屏" : "全屏";
  el("full").setAttribute("aria-pressed", String(Boolean(active)));
  el("full").hidden = !active && matchMedia("(display-mode: fullscreen)").matches;
  scheduleGeometryReport();
}
document.addEventListener("fullscreenchange", updateFullscreenLabel);
document.addEventListener("webkitfullscreenchange", updateFullscreenLabel);
video.addEventListener("webkitbeginfullscreen", updateFullscreenLabel);
video.addEventListener("webkitendfullscreen", updateFullscreenLabel);
window.addEventListener("resize", scheduleGeometryReport);
window.visualViewport?.addEventListener("resize", scheduleGeometryReport);
document.addEventListener("visibilitychange", () => {
  lastVideoSample = lastCanvasSample = null;
  if (!document.hidden) scheduleGeometryReport();
});
el("copy-link").addEventListener("click", async () => {
  if (!pairing) return;
  const url = new URL("/", location.href);
  url.hash = new URLSearchParams({ token: pairing }).toString();
  try {
    await navigator.clipboard.writeText(url.href);
    el("copy-link").textContent = "已复制配对链接";
  } catch {
    const field = el("manual-copy");
    field.value = url.href;
    field.hidden = false;
    field.focus();
    field.select();
  }
});
window.addEventListener("pagehide", () => {
  connectionVersion++;
  requestInFlight = false;
  resetPeer();
  setLobby("待连接", "点击连接以显示电脑画面。");
});
window.addEventListener("hashchange", () => {
  const incoming = readLinkCode();
  if (!incoming) return;
  scan = incoming;
  if ((session && !changingPair) || requestInFlight || !stage.hidden) return;
  pairing = incoming;
  store("sessionStorage", PAIR, pairing);
  setLobby("待连接", "配对信息已就绪，点“连接屏幕”即可与电脑配对。");
});

el("home-help").hidden = Boolean(navigator.standalone || matchMedia("(display-mode: standalone)").matches);
if (session) setLobby("已配对", "这台 iPad 已保存配对，点“连接屏幕”即可使用。");
else if (pairing) setLobby("待连接", "配对信息已就绪，点“连接屏幕”即可与电脑配对。");
else setLobby("需要配对", "首次使用请输入电脑上的今日配对码，也可以粘贴配对链接。");
getStatus().catch((error) => {
  if (!peer && !requestInFlight) message(error.message, true);
});
