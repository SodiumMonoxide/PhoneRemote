"use strict";

window.PhoneRemoteMac = {
  async request(path, { token, body, timeout }) {
    const message = { path, timeout };
    if (token !== undefined) message.token = token;
    if (body !== undefined) message.body = body;
    const response = await window.webkit.messageHandlers.remote.postMessage(message);
    if (typeof response.jpeg === "string") {
      let bytes;
      if (typeof Uint8Array.fromBase64 === "function") {
        bytes = Uint8Array.fromBase64(response.jpeg);
      } else {
        const raw = atob(response.jpeg);
        bytes = new Uint8Array(raw.length);
        for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
      }
      response.image = new Blob([bytes], { type: "image/jpeg" });
      delete response.jpeg;
    }
    return response;
  }
};
