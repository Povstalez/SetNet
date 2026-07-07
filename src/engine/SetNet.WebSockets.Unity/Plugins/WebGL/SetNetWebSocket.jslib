// SetNet WebGL WebSocket bridge.
// A minimal, polling-based bridge to the browser WebSocket API (no C#→JS callbacks / dynCall, so it is robust across
// Emscripten versions). C# opens a socket, sends bytes, and polls an incoming queue each frame. One binary WebSocket
// message == one SetNet frame ([2-byte type LE][payload]); message boundaries are preserved by the browser.
var SetNetWebSocketLib = {

  $snws: {
    instances: {},
    next: 1,
  },

  // Opens a socket to `url`; returns a handle id (>0) or 0 on immediate failure.
  SetNetWs_Connect: function (urlPtr) {
    var url = UTF8ToString(urlPtr);
    var id = snws.next++;
    var state = { ws: null, queue: [], readyState: 0 /*CONNECTING*/, error: 0 };
    try {
      var ws = new WebSocket(url);
      ws.binaryType = 'arraybuffer';
      ws.onopen = function () { state.readyState = 1 /*OPEN*/; };
      ws.onmessage = function (e) { if (e.data instanceof ArrayBuffer) state.queue.push(new Uint8Array(e.data)); };
      ws.onerror = function () { state.error = 1; };
      ws.onclose = function () { state.readyState = 3 /*CLOSED*/; };
      state.ws = ws;
    } catch (e) {
      state.readyState = 3; state.error = 1;
    }
    snws.instances[id] = state;
    return id;
  },

  // Browser readyState: 0 connecting, 1 open, 2 closing, 3 closed.
  SetNetWs_State: function (id) {
    var s = snws.instances[id];
    return s ? s.readyState : 3;
  },

  // 1 if an error was observed on the socket, else 0.
  SetNetWs_Error: function (id) {
    var s = snws.instances[id];
    return s ? s.error : 1;
  },

  // Sends `len` bytes at `ptr`. Returns 1 on success, 0 if not open.
  SetNetWs_Send: function (id, ptr, len) {
    var s = snws.instances[id];
    if (!s || !s.ws || s.ws.readyState !== 1) return 0;
    // Copy the WASM heap slice so the browser owns the bytes.
    s.ws.send(HEAPU8.slice(ptr, ptr + len));
    return 1;
  },

  // Length of the next queued message, or -1 if the queue is empty.
  SetNetWs_PeekLength: function (id) {
    var s = snws.instances[id];
    return (s && s.queue.length > 0) ? s.queue[0].length : -1;
  },

  // Copies the next queued message into `ptr` (capacity `max`) and dequeues it. Returns the length,
  // -1 if empty, or (length) without dequeuing if it doesn't fit (caller grows the buffer and retries).
  SetNetWs_Receive: function (id, ptr, max) {
    var s = snws.instances[id];
    if (!s || s.queue.length === 0) return -1;
    var msg = s.queue[0];
    if (msg.length > max) return msg.length;
    HEAPU8.set(msg, ptr);
    s.queue.shift();
    return msg.length;
  },

  // Closes and forgets the socket.
  SetNetWs_Close: function (id) {
    var s = snws.instances[id];
    if (s) {
      try { if (s.ws) s.ws.close(); } catch (e) { }
      s.readyState = 3;
      delete snws.instances[id];
    }
  },
};

autoAddDeps(SetNetWebSocketLib, '$snws');
mergeInto(LibraryManager.library, SetNetWebSocketLib);
