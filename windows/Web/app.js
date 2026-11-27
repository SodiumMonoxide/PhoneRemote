"use strict";

const pairPanel = document.querySelector("#pair-panel");
const pairForm = document.querySelector("#pair-form");
const pairButton = document.querySelector("#pair-button");
const pairCode = document.querySelector("#pair-code");
const pairMessage = document.querySelector("#pair-message");
const rememberOption = document.querySelector("#remember-option");
const rememberDevice = document.querySelector("#remember-device");
const resumeButton = document.querySelector("#resume-button");
const remotePanel = document.querySelector("#remote-panel");
const disconnectButton = document.querySelector("#disconnect");
const status = document.querySelector("#status");
const viewport = document.querySelector("#viewport");
const screenImage = document.querySelector("#screen");
const frameMessage = document.querySelector("#frame-message");
const mouseControls = document.querySelector("#mouse-controls");
const textControls = document.querySelector("#text-controls");
const keyControls = document.querySelector("#key-controls");
const textForm = document.querySelector("#text-form");
const textInput = document.querySelector("#text-input");
const textSubmit = textForm.querySelector('button[type="submit"]');
const inputMessage = document.querySelector("#input-message");
const modeButtons = [...document.querySelectorAll("[data-mode]")];
const modeHelp = document.querySelector("#mode-help");

const modeInstructions = {
  direct: "点击画面后可直接使用鼠标、键盘输入和滚动。Esc 返回操作面板。中文输入可用「输入文字」。",
  click: "轻点画面中的位置。",
  doubleClick: "轻点画面，电脑会在对应位置双击。",
  rightClick: "轻点画面，电脑会在对应位置按右键。",
  move: "在画面上滑动手指，移动指针。",
  drag: "在画面上按住并拖动，松开手指结束拖动。"
};

const androidBridge = window.AndroidBridge ?? null;
const desktopControl = !androidBridge && (window.PhoneRemoteMac || matchMedia("(any-pointer: fine)").matches);
const frameInterval = androidBridge ? 450 : desktopControl ? 150 : 240;
const directButton = document.querySelector("#direct-mode");
directButton.hidden = !desktopControl;
const pendingAndroidRequests = new Map();
let androidRequestId = 0;
let current = null;
let mode = "click";

if (androidBridge) {
  rememberOption.hidden = false;
  resumeButton.hidden = false;
  window.finishAndroidRequest = (id, status, content) => {
    pendingAndroidRequests.get(id)?.(status, content);
  };
}

function showStatus(state, label) {
  status.dataset.state = state;
  if (status.textContent !== label) status.textContent = label;
}

async function request(path, { token, body, keepalive = false, timeout = 3500 } = {}) {
  if (window.PhoneRemoteMac) return window.PhoneRemoteMac.request(path, { token, body, timeout, keepalive });
  if (androidBridge) return requestAndroid(path, token, body, timeout);
  const headers = new Headers();
  if (token) headers.set("Authorization", `Bearer ${token}`);
  if (body !== undefined) headers.set("Content-Type", "application/json");

  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeout);
  const response = await fetch(path, {
    method: body === undefined ? "GET" : "POST",
    body: body === undefined ? undefined : JSON.stringify(body),
    headers,
    cache: "no-store",
    credentials: "omit",
    redirect: "error",
    keepalive,
    signal: controller.signal
  }).catch(error => {
    clearTimeout(timer);
    throw error;
  });

  // Consume the response body before clearing the transfer deadline.
  try {
    if (path === "/api/frame" && response.ok) {
      return { status: response.status, image: await response.blob() };
    }
    if (path === "/api/pair" && response.ok) {
      return { status: response.status, session: await response.json() };
    }
    return { status: response.status };
  } finally {
    clearTimeout(timer);
  }
}

function requestAndroid(path, token, body, timeout) {
  androidRequestId = androidRequestId % 2147483647 + 1;
  const id = androidRequestId;
  return new Promise((resolve, reject) => {
    const deadline = setTimeout(() => {
      pendingAndroidRequests.delete(id);
      reject(new Error("Android request timed out"));
    }, Math.max(timeout, 4500));
    pendingAndroidRequests.set(id, (status, content) => {
      pendingAndroidRequests.delete(id);
      clearTimeout(deadline);
      try {
        if (!status) throw new Error("Android connection failed");
        if (status === 200 && path === "/api/frame") {
          const bytes = Uint8Array.from(atob(content), value => value.charCodeAt(0));
          resolve({ status, image: new Blob([bytes], { type: "image/jpeg" }) });
        } else if (status === 200 && (path === "/api/pair" || path === "/api/resume")) {
          resolve({ status, session: JSON.parse(content) });
        } else {
          resolve({ status });
        }
      } catch (error) {
        reject(error);
      }
    });
    try {
      androidBridge.request(id, path, body === undefined ? "GET" : "POST",
        body === undefined ? "" : JSON.stringify(body), token ?? "");
    } catch (error) {
      clearTimeout(deadline);
      pendingAndroidRequests.delete(id);
      reject(error);
    }
  });
}

function setConnecting(busy) {
  pairButton.disabled = busy;
  pairCode.disabled = busy;
  rememberDevice.disabled = busy;
  resumeButton.disabled = busy;
}

function selectMode(next) {
  if (current?.gesture) finishGesture(current, null, true);
  mode = next;
  screenImage.tabIndex = next === "direct" ? 0 : -1;
  modeHelp.textContent = modeInstructions[next];
  for (const button of modeButtons) {
    button.setAttribute("aria-pressed", String(button.dataset.mode === mode));
  }
}

function discardPending(session, preserveRelease = false) {
  const pending = session.queue.splice(0);
  for (const operation of pending) {
    if (preserveRelease && operation.data.action === "up") {
      session.queue.push(operation);
    } else {
      operation.complete?.(false);
    }
  }
}

function setFrameReady(session, ready, message = "") {
  if (ready && session.ready) return;
  session.ready = ready;
  viewport.dataset.ready = String(ready);
  frameMessage.hidden = ready;
  frameMessage.textContent = message;
  showStatus(ready ? "online" : "waiting", ready ? "已连接" : "等待画面");
  mouseControls.disabled = !ready;
  textControls.disabled = !ready;
  keyControls.disabled = !ready;
  if (!ready) {
    discardPending(session, true);
    finishGesture(session, null, true);
  }
}

function clearFrame(session) {
  if (session.frameUrl) URL.revokeObjectURL(session.frameUrl);
  screenImage.hidden = true;
  screenImage.removeAttribute("src");
}

function closeSession(session, message = "") {
  if (current !== session) return;
  current = null;
  clearTimeout(session.frameTimer);
  finishGesture(session, null, true);
  discardPending(session);
  clearFrame(session);
  remotePanel.hidden = true;
  disconnectButton.hidden = true;
  pairPanel.hidden = false;
  pairMessage.textContent = message;
  showStatus("offline", "未连接");
}

function openSession(token) {
  const session = {
    token,
    ready: false,
    queue: [],
    sending: false,
    gesture: null,
    frameUrl: null,
    frameTimer: null,
    scrollRemainder: 0,
    readingFrame: false,
    disconnecting: false
  };
  current = session;
  pairPanel.hidden = true;
  remotePanel.hidden = false;
  disconnectButton.hidden = false;
  disconnectButton.disabled = false;
  inputMessage.textContent = "";
  textInput.value = "";
  textSubmit.disabled = false;
  selectMode(desktopControl ? "direct" : "click");
  setFrameReady(session, false, "正在获取画面…");
  void readFrame(session);
}

pairForm.addEventListener("submit", async event => {
  event.preventDefault();
  if (pairButton.disabled) return;
  const code = pairCode.value.trim();
  if (!/^[0-9]{8}$/.test(code)) {
    pairMessage.textContent = "请输入 8 位数字。";
    return;
  }

  setConnecting(true);
  pairMessage.textContent = "";
  showStatus("waiting", "正在连接");

  try {
    const body = {
      code,
      remember: androidBridge !== null && rememberDevice.checked,
      switchMode: androidBridge !== null
    };
    const result = await request("/api/pair", { body, timeout: androidBridge ? 15000 : 5000 });
    switch (result.status) {
      case 200:
        if (typeof result.session?.token !== "string" || !result.session.token) {
          throw new Error("Missing session token");
        }
        pairCode.value = "";
        openSession(result.session.token);
        break;
      case 401:
        pairMessage.textContent = "配对码错误或已过期，请查看电脑端窗口。";
        break;
      case 409:
        pairMessage.textContent = "请在被控端检查当前会话和已配对设备，再生成新码。";
        break;
      case 429:
        pairMessage.textContent = "尝试次数过多，请稍后重试。";
        break;
      case 503:
        pairMessage.textContent = "电脑暂时无法保存配对，请检查电脑端后重试。";
        break;
      default:
        pairMessage.textContent = "连接失败，请检查电脑端窗口后重试。";
    }
  } catch {
    pairMessage.textContent = androidBridge
      ? "无法连接。请核对连接设置，并查看「使用指南」中的连接排查。"
      : "无法连接，请确认被控端已启动，并核对地址和证书指纹。";
  } finally {
    setConnecting(false);
    if (!current) showStatus("offline", "未连接");
  }
});

async function resumeDevice(automatic = false) {
  if (!androidBridge || current || resumeButton.disabled) return;
  setConnecting(true);
  pairMessage.textContent = "";
  showStatus("waiting", "正在连接");
  try {
    const result = await request("/api/resume", { body: { switchMode: !automatic }, timeout: 15000 });
    if (result.status === 200 && typeof result.session?.token === "string") {
      openSession(result.session.token);
    } else if (result.status === 401) {
      if (!automatic) pairMessage.textContent = "这部手机未保存有效配对，请用电脑端配对码连接。";
    } else if (result.status === 409) {
      pairMessage.textContent = "电脑正在使用另一种连接。点「连接已配对的电脑」即可切换。";
    } else {
      pairMessage.textContent = "暂时无法恢复连接，请检查电脑端状态。";
    }
  } catch {
    pairMessage.textContent = "无法恢复连接。请确认电脑端已启动，并查看「使用指南」。";
  } finally {
    setConnecting(false);
    if (!current) showStatus("offline", "未连接");
  }
}

resumeButton.addEventListener("click", () => void resumeDevice());
if (androidBridge) void resumeDevice(true);

async function showFrame(session, blob) {
  const url = URL.createObjectURL(blob);
  let displayed = false;
  try {
    const image = new Image();
    image.src = url;
    await image.decode();
    if (current !== session || document.hidden || session.disconnecting) return;
    const previous = session.frameUrl;
    screenImage.src = url;
    screenImage.hidden = false;
    session.frameUrl = url;
    displayed = true;
    if (previous) URL.revokeObjectURL(previous);
    setFrameReady(session, true);
  } finally {
    if (!displayed) URL.revokeObjectURL(url);
  }
}

async function readFrame(session) {
  if (current !== session || document.hidden || session.readingFrame || session.disconnecting) return;
  session.readingFrame = true;
  const started = performance.now();
  let nextDelay = frameInterval;
  try {
    const result = await request("/api/frame", { token: session.token, timeout: 2400 });
    if (current !== session || session.disconnecting) return;
    if (result.status === 401) {
      closeSession(session, "连接已结束，请重新配对。");
    } else if (result.status === 200) {
      await showFrame(session, result.image);
      nextDelay = Math.max(0, frameInterval - (performance.now() - started));
    } else {
      setFrameReady(session, false, "暂时无法获取画面，正在重试…");
      nextDelay = 850;
    }
  } catch {
    if (current === session && !session.disconnecting) {
      setFrameReady(session, false, "画面连接中断，正在重试…");
      nextDelay = 1000;
    }
  } finally {
    session.readingFrame = false;
    if (current === session && !document.hidden && !session.disconnecting) {
      session.frameTimer = setTimeout(() => void readFrame(session), nextDelay);
    }
  }
}

function queueInput(session, data, complete) {
  if (current !== session || (!session.ready && data.action !== "up")) {
    complete?.(false);
    return;
  }

  const tail = session.queue[session.queue.length - 1];
  if (data.action === "move" && tail?.data.action === "move") {
    tail.data = data;
  } else if (data.action === "scroll" && tail?.data.action === "scroll" &&
             data.x === tail.data.x && data.y === tail.data.y && Math.abs(data.delta + tail.data.delta) <= 8) {
    tail.data.delta += data.delta;
    if (tail.data.delta === 0) session.queue.pop();
  } else if (!complete && !tail?.complete && data.action === "text" && tail?.data.action === "text" &&
             data.text.length + tail.data.text.length <= 256) {
    tail.data.text += data.text;
  } else if (session.queue.length < 48 || data.action === "up") {
    session.queue.push({ data, complete });
  } else {
    if (data.action !== "move") inputMessage.textContent = "网络较慢，操作尚未发送，请稍后重试。";
    complete?.(false);
    return;
  }
  if (!session.sending) void sendInput(session);
}

async function sendInput(session) {
  session.sending = true;
  try {
    while (current === session && session.queue.length) {
      const { data, complete } = session.queue.shift();
      let success = false;
      try {
        const result = await request("/api/input", { token: session.token, body: data, timeout: 1400 });
        if (current !== session) return;
        if (result.status === 401) {
          closeSession(session, "连接已结束，请重新配对。");
          return;
        }
        success = result.status >= 200 && result.status < 300;
        inputMessage.textContent = success ? "" : "这次操作未完成，请重试。";
      } catch {
        if (current === session) {
          setFrameReady(session, false, "正在重新获取画面…");
          inputMessage.textContent = "操作发送超时，请先在画面上确认结果。";
        }
      } finally {
        complete?.(success);
      }
    }
  } finally {
    session.sending = false;
  }
}

function screenPoint(event) {
  const bounds = screenImage.getBoundingClientRect();
  if (!bounds.width || !bounds.height) return null;
  const clamp = value => Math.max(0, Math.min(1, value));
  return {
    x: clamp((event.clientX - bounds.left) / bounds.width),
    y: clamp((event.clientY - bounds.top) / bounds.height)
  };
}

function finishGesture(session, event, cancelled) {
  const gesture = session.gesture;
  if (!gesture || (event && gesture.id !== event.pointerId)) return;
  session.gesture = null;
  clearInterval(gesture.keepHeld);

  if (screenImage.hasPointerCapture(gesture.id)) {
    screenImage.releasePointerCapture(gesture.id);
  }
  const point = (event && !cancelled && screenPoint(event)) || gesture.point;
  if (gesture.mode === "drag" || gesture.mode === "direct") {
    queueInput(session, { action: "up", ...point });
  } else if (!cancelled && gesture.mode !== "move" && !gesture.moved) {
    const distance = Math.hypot(event.clientX - gesture.startX, event.clientY - gesture.startY);
    if (distance < 10) queueInput(session, { action: gesture.mode, ...point });
  }
}

screenImage.addEventListener("pointerdown", event => {
  const session = current;
  if (!session?.ready || session.gesture || !event.isPrimary) return;
  const point = screenPoint(event);
  if (!point) return;
  if (mode === "direct" && (event.button === 2 || (event.button === 0 && event.ctrlKey && window.PhoneRemoteMac))) {
    event.preventDefault();
    screenImage.focus({ preventScroll: true });
    queueInput(session, { action: "rightClick", ...point });
    return;
  }
  if (event.button !== 0) return;
  event.preventDefault();
  if (mode === "direct") screenImage.focus({ preventScroll: true });
  screenImage.setPointerCapture(event.pointerId);
  const gesture = {
    id: event.pointerId,
    mode,
    point,
    startX: event.clientX,
    startY: event.clientY,
    moved: false,
    keepHeld: null
  };
  session.gesture = gesture;
  if (mode === "move") queueInput(session, { action: "move", ...point });
  if (mode === "drag" || mode === "direct") {
    queueInput(session, { action: "down", ...point });
    gesture.keepHeld = setInterval(() => {
      queueInput(session, { action: "move", ...gesture.point });
    }, 650);
  }
});

screenImage.addEventListener("pointermove", event => {
  const session = current;
  const gesture = session?.gesture;
  if (session?.ready && !gesture && mode === "direct" && event.pointerType === "mouse") {
    const point = screenPoint(event);
    if (point) queueInput(session, { action: "move", ...point });
    return;
  }
  if (!session?.ready || !gesture || gesture.id !== event.pointerId) return;
  const point = screenPoint(event);
  if (!point) return;
  gesture.point = point;
  if (Math.hypot(event.clientX - gesture.startX, event.clientY - gesture.startY) >= 10) {
    gesture.moved = true;
  }
  if (gesture.mode === "move" || gesture.mode === "drag" || gesture.mode === "direct") {
    queueInput(session, { action: "move", ...point });
  }
});

screenImage.addEventListener("pointerup", event => {
  if (current) finishGesture(current, event, false);
});
screenImage.addEventListener("pointercancel", event => {
  if (current) finishGesture(current, event, true);
});
screenImage.addEventListener("lostpointercapture", event => {
  if (current) finishGesture(current, event, true);
});
screenImage.addEventListener("contextmenu", event => event.preventDefault());
screenImage.addEventListener("wheel", event => {
  if (!current?.ready || event.deltaY === 0) return;
  event.preventDefault();
  let delta = -Math.sign(event.deltaY);
  if (mode === "direct") {
    // Trackpads report many small pixel deltas. Keep fractions between events.
    const units = event.deltaMode === 1 ? 1 : event.deltaMode === 2 ? 8 : 1 / 40;
    current.scrollRemainder -= event.deltaY * units;
    const lines = Math.trunc(current.scrollRemainder);
    if (!lines) return;
    current.scrollRemainder -= lines;
    delta = Math.max(-8, Math.min(8, lines));
  }
  queueInput(current, { action: "scroll", delta, ...screenPoint(event) });
}, { passive: false });

const remoteKeys = new Set([...keyControls.querySelectorAll("[data-key]")].map(button => button.dataset.key));
screenImage.addEventListener("keydown", event => {
  if (!current?.ready || mode !== "direct" || event.isComposing) return;
  if (event.key === "Escape") {
    event.preventDefault();
    directButton.focus();
    return;
  }
  let key = event.key === " " ? "Space" : event.key;
  let input;
  if (event.metaKey || event.ctrlKey || event.altKey) {
    if (event.altKey && key === "Tab") key = "Alt+Tab";
    else if (!event.altKey && (event.ctrlKey || event.metaKey)) key = "Ctrl+" + key.toUpperCase();
    else return;
    if (!remoteKeys.has(key)) return;
    input = { action: "key", key };
  } else if ([...event.key].length === 1) {
    input = { action: "text", text: event.key };
  } else if (remoteKeys.has(key)) {
    input = { action: "key", key };
  } else return;
  event.preventDefault();
  queueInput(current, input);
});

window.addEventListener("blur", () => {
  if (current?.gesture) finishGesture(current, null, true);
});

mouseControls.addEventListener("click", event => {
  if (!current?.ready) return;
  const button = event.target.closest("button");
  if (button?.dataset.mode) selectMode(button.dataset.mode);
  if (button?.dataset.scroll) {
    queueInput(current, { action: "scroll", delta: Number(button.dataset.scroll) });
  }
});

keyControls.addEventListener("click", event => {
  const key = event.target.closest("button")?.dataset.key;
  if (current?.ready && key) queueInput(current, { action: "key", key });
});

textForm.addEventListener("submit", event => {
  event.preventDefault();
  const session = current;
  const entered = textInput.value;
  if (!session?.ready || !entered || textSubmit.disabled) return;
  textSubmit.disabled = true;
  queueInput(session, { action: "text", text: entered }, success => {
    if (current !== session) return;
    textSubmit.disabled = false;
    if (success && textInput.value === entered) textInput.value = "";
  });
});

disconnectButton.addEventListener("click", async () => {
  const session = current;
  if (!session) return;
  session.disconnecting = true;
  disconnectButton.disabled = true;
  clearTimeout(session.frameTimer);
  setFrameReady(session, false, "正在断开…");
  try {
    const result = await request("/api/disconnect", { token: session.token, body: {} });
    if ((result.status >= 200 && result.status < 300) || result.status === 401) {
      closeSession(session, "已断开连接。");
      return;
    }
  } catch {
    // Keep the token for a second disconnect attempt.
  }
  if (current !== session) return;
  session.disconnecting = false;
  disconnectButton.disabled = false;
  inputMessage.textContent = "断开请求未确认，请重试，或在电脑端断开。";
  void readFrame(session);
});

document.addEventListener("visibilitychange", () => {
  const session = current;
  if (!session) return;
  clearTimeout(session.frameTimer);
  if (document.hidden) {
    setFrameReady(session, false, "画面已暂停。");
  } else {
    void readFrame(session);
  }
});

window.addEventListener("pagehide", () => {
  const session = current;
  if (!session) return;
  closeSession(session);
  void request("/api/disconnect", { token: session.token, body: {}, keepalive: true }).catch(() => {});
});
