// The WebGL transport: the browser's own WebSocket, reached from managed code.
//
// This file exists because Unity's supported targets do not agree that sockets
// exist. A desktop or mobile IL2CPP build can use System.Net.WebSockets; a WebGL
// build cannot — it has no threads and no socket stack — so on that one target
// the transport has to live here and be called across the interop boundary.
//
// The shape is forced rather than chosen, in three places:
//
//   1. Sockets are held in a table keyed by an integer handle, because a
//      JavaScript object cannot be returned to managed code. The handle is what
//      crosses; the socket never does.
//   2. Frames are delivered by calling a function POINTER that managed code
//      passed in, not by name. `SendMessage` addresses a GameObject by string
//      name, which makes the transport depend on scene contents — the SDK has no
//      GameObject and should not require one.
//   3. Strings are copied into the heap with `_malloc` and freed by the caller.
//      A pointer into the emscripten stack would be invalid by the time managed
//      code read it, and this callback runs from a browser event rather than
//      from the frame the caller was on.

mergeInto(LibraryManager.library, {
	$haivenState: {
		sockets: {},
		next: 1,
	},

	HaivenSocketOpen: function (urlPtr, handle, onMessage, onClosed) {
		var url = UTF8ToString(urlPtr)
		var socket

		try {
			socket = new WebSocket(url)
		} catch (error) {
			// A constructor throw is a malformed URL or a blocked mixed-content
			// upgrade, and both are close conditions rather than exceptions as
			// far as managed code is concerned. Reported as 1006 (abnormal
			// closure), which is what a browser reports for a connection that
			// never opened.
			{{{ makeDynCall('viii', 'onClosed') }}}(handle, 1006, 0)
			return 0
		}

		socket.onmessage = function (event) {
			// Binary frames are not part of this protocol. Dropping them here
			// rather than passing them on keeps the managed side from having to
			// have an opinion about a case the server never produces.
			if (typeof event.data !== "string") return

			var size = lengthBytesUTF8(event.data) + 1
			var buffer = _malloc(size)
			stringToUTF8(event.data, buffer, size);
			{{{ makeDynCall('vii', 'onMessage') }}}(handle, buffer);
			_free(buffer)
		}

		socket.onclose = function (event) {
			var reason = event.reason || ""
			var size = lengthBytesUTF8(reason) + 1
			var buffer = _malloc(size)
			stringToUTF8(reason, buffer, size);
			{{{ makeDynCall('viii', 'onClosed') }}}(handle, event.code, buffer);
			_free(buffer)
			delete haivenState.sockets[handle]
		}

		socket.onerror = function () {
			// Deliberately silent. A browser fires error immediately before
			// close and gives no detail worth forwarding, so reporting it as
			// well would announce every ordinary disconnect twice.
		}

		haivenState.sockets[handle] = socket
		return 1
	},

	HaivenSocketOpen__deps: ["$haivenState", "malloc", "free"],

	HaivenSocketSend: function (handle, payloadPtr) {
		var socket = haivenState.sockets[handle]

		// Sending before the socket is open is a real state, not a caller error:
		// the session queues frames until the handshake completes and flushes
		// them, so this is reported rather than thrown and the managed queue
		// keeps the frame.
		if (!socket || socket.readyState !== 1) return 0

		socket.send(UTF8ToString(payloadPtr))
		return 1
	},

	HaivenSocketSend__deps: ["$haivenState"],

	HaivenSocketClose: function (handle, code, reasonPtr) {
		var socket = haivenState.sockets[handle]
		if (!socket) return

		// A browser rejects any close code outside 1000 and 3000-4999, and
		// throws rather than closing. Teardown must not be able to throw, so an
		// out-of-range code becomes a normal closure.
		var accepted = code === 1000 || (code >= 3000 && code <= 4999)
		socket.close(accepted ? code : 1000, UTF8ToString(reasonPtr))
	},

	HaivenSocketClose__deps: ["$haivenState"],

	HaivenSocketNextHandle: function () {
		return haivenState.next++
	},

	HaivenSocketNextHandle__deps: ["$haivenState"],
})
