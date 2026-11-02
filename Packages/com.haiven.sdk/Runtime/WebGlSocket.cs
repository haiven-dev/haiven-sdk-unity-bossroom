#if UNITY_WEBGL && !UNITY_EDITOR
#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using AOT;

namespace Haiven.Sdk
{
    /// <summary>
    /// <see cref="IHaivenSocket"/> over the browser's WebSocket, for WebGL.
    ///
    /// Compiled only for WebGL players. The editor is excluded deliberately: it
    /// runs Mono against the desktop socket stack even when the build target is
    /// WebGL, so a build that included this in the editor would test the one
    /// path that is never shipped.
    ///
    /// The static callbacks below are the interesting part of this file and they
    /// are not a style choice. IL2CPP cannot marshal an instance delegate to a
    /// native function pointer — there is no per-instance thunk to point at
    /// under AOT — so anything JavaScript calls back into must be a static
    /// method carrying <c>[MonoPInvokeCallback]</c>. That is what forces the
    /// handle table: the callback arrives with no receiver, so the only way back
    /// to the socket it belongs to is to look it up by a key that crossed the
    /// boundary as an integer.
    ///
    /// The consequence worth stating is that this class cannot be made
    /// per-instance later without redesigning the interop, and that a socket
    /// which is not <see cref="Dispose">disposed</see> stays in the table. That
    /// is a leak with a static root, which is the kind IL2CPP stripping and
    /// domain reloads will not clean up for you.
    /// </summary>
    public sealed class WebGlSocket : IHaivenSocket, IDisposable
    {
        [DllImport("__Internal")]
        private static extern int HaivenSocketOpen(string url, int handle, Action<int, IntPtr> onMessage, Action<int, int, IntPtr> onClosed);

        [DllImport("__Internal")]
        private static extern int HaivenSocketSend(int handle, string payload);

        [DllImport("__Internal")]
        private static extern void HaivenSocketClose(int handle, int code, string reason);

        [DllImport("__Internal")]
        private static extern int HaivenSocketNextHandle();

        /// <summary>
        /// Live sockets by handle. Static because the callbacks are.
        /// </summary>
        private static readonly Dictionary<int, WebGlSocket> Live = new Dictionary<int, WebGlSocket>();

        private readonly int handle;
        private bool disposed;

        public event Action<string>? Received;

        public event Action<int, string>? Closed;

        public WebGlSocket(string url)
        {
            handle = HaivenSocketNextHandle();
            Live[handle] = this;

            if (HaivenSocketOpen(url, handle, OnMessage, OnClosed) == 0)
            {
                // The constructor threw on the JavaScript side, which has
                // already reported a close through OnClosed. Nothing is thrown
                // here: a socket that never opened and a socket that opened and
                // closed are the same state to a caller, and only one of them
                // would be an exception if this threw.
                Live.Remove(handle);
            }
        }

        public void Send(string payload)
        {
            if (disposed) return;

            // A false return is "not open yet", which the session already models
            // by queueing until the handshake completes. Reporting it as a
            // failure here would make an ordinary startup race look like an
            // error.
            HaivenSocketSend(handle, payload);
        }

        public void Close()
        {
            Close(1000, string.Empty);
        }

        public void Close(int code, string reason)
        {
            if (disposed) return;

            HaivenSocketClose(handle, code, reason);
        }

        /// <summary>
        /// Removes this socket from the static table.
        ///
        /// Required rather than tidy. The table is a static root, so a socket
        /// left in it survives every scene load and is never collected.
        /// </summary>
        public void Dispose()
        {
            if (disposed) return;

            Close();
            disposed = true;
            Live.Remove(handle);
        }

        [MonoPInvokeCallback(typeof(Action<int, IntPtr>))]
        private static void OnMessage(int handle, IntPtr payload)
        {
            // An exception crossing back into JavaScript is undefined behaviour
            // under IL2CPP, so a handler that throws must not escape this frame.
            // It is swallowed rather than logged through UnityEngine.Debug
            // because that would make the transport depend on the engine.
            try
            {
                if (Live.TryGetValue(handle, out var socket))
                {
                    socket.Received?.Invoke(Marshal.PtrToStringUTF8(payload) ?? string.Empty);
                }
            }
            catch
            {
                // Intentionally empty: see above.
            }
        }

        [MonoPInvokeCallback(typeof(Action<int, int, IntPtr>))]
        private static void OnClosed(int handle, int code, IntPtr reason)
        {
            try
            {
                if (Live.TryGetValue(handle, out var socket))
                {
                    Live.Remove(handle);
                    socket.Closed?.Invoke(code, reason == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(reason) ?? string.Empty);
                }
            }
            catch
            {
                // Intentionally empty: see above.
            }
        }
    }
}
#endif
