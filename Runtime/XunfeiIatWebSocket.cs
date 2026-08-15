using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AOT;
using UnityEngine;
using UnityEngine.Scripting;

namespace Cowart.XunfeiSpeechRecognition
{
    [Preserve]
    [AddComponentMenu("")]
    internal sealed class XunfeiIatWebSocket : MonoBehaviour
    {
        private readonly ConcurrentQueue<Action> mainThreadActions =
            new ConcurrentQueue<Action>();
        private readonly ConcurrentQueue<OutboundMessage> outboundMessages =
            new ConcurrentQueue<OutboundMessage>();

        private bool isOpen;
#if !UNITY_WEBGL || UNITY_EDITOR
        private bool sendPumpActive;
#endif
        private int connectionGeneration;

#if !UNITY_WEBGL || UNITY_EDITOR
        private ClientWebSocket socket;
        private CancellationTokenSource cancellation;
#endif

#if UNITY_WEBGL && !UNITY_EDITOR
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void StateCallback(int state, IntPtr bytes, int length);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void MessageCallback(IntPtr bytes, int length);

        private static readonly StateCallback BrowserStateCallback = HandleBrowserState;
        private static readonly MessageCallback BrowserMessageCallback = HandleBrowserMessage;
        private static XunfeiIatWebSocket activeBrowserSocket;
        private static int activeBrowserGeneration;

        [DllImport("__Internal")]
        private static extern void CowartXunfeiIat_RegisterCallbacks(
            StateCallback stateCallback,
            MessageCallback messageCallback);

        [DllImport("__Internal")]
        private static extern int CowartXunfeiIat_Open(IntPtr urlBytes, int urlLength);

        [DllImport("__Internal")]
        private static extern int CowartXunfeiIat_Send(IntPtr messageBytes, int messageLength);

        [DllImport("__Internal")]
        private static extern void CowartXunfeiIat_Close();
#endif

        internal bool IsOpen => isOpen;

        internal event Action<int> Opened;
        internal event Action<int, string> MessageReceived;
        internal event Action<int, string> Failed;
        internal event Action<int, string> Closed;

        private void Update()
        {
            while (mainThreadActions.TryDequeue(out Action action))
            {
                action?.Invoke();
            }

#if UNITY_WEBGL && !UNITY_EDITOR
            if (!isOpen)
            {
                return;
            }

            while (outboundMessages.TryDequeue(out OutboundMessage outbound))
            {
                if (outbound.Generation != connectionGeneration)
                {
                    continue;
                }

                if (!SendBrowserUtf8(outbound.Value))
                {
                    RaiseFailure(
                        outbound.Generation,
                        "浏览器 WebSocket 发送失败。");
                    break;
                }
            }
#else
            if (isOpen && !sendPumpActive && !outboundMessages.IsEmpty)
            {
                sendPumpActive = true;
                int generation = connectionGeneration;
                ClientWebSocket connectedSocket = socket;
                CancellationToken token = cancellation != null
                    ? cancellation.Token
                    : new CancellationToken(true);
                _ = PumpDotNetMessagesAsync(connectedSocket, generation, token);
            }
#endif
        }

        private void OnDestroy()
        {
            Close();
        }

        internal void Connect(string url, int generation)
        {
            Close();
            connectionGeneration = generation;
            if (string.IsNullOrWhiteSpace(url))
            {
                RaiseFailure(generation, "WebSocket 地址为空。");
                return;
            }

#if UNITY_WEBGL && !UNITY_EDITOR
            if (activeBrowserSocket != null && activeBrowserSocket != this)
            {
                activeBrowserSocket.Close();
            }

            activeBrowserSocket = this;
            activeBrowserGeneration = generation;
            CowartXunfeiIat_RegisterCallbacks(BrowserStateCallback, BrowserMessageCallback);
            if (!InvokeBrowserUtf8(url, CowartXunfeiIat_Open))
            {
                activeBrowserSocket = null;
                activeBrowserGeneration = 0;
                RaiseFailure(generation, "浏览器 WebSocket 无法启动。");
            }
#else
            CancellationTokenSource newCancellation = new CancellationTokenSource();
            ClientWebSocket newSocket = new ClientWebSocket();
            cancellation = newCancellation;
            socket = newSocket;
            _ = ConnectDotNetAsync(
                newSocket,
                generation,
                url,
                newCancellation.Token);
#endif
        }

        internal bool Send(string message, int generation)
        {
            if (string.IsNullOrEmpty(message) || !isOpen ||
                generation == 0 || generation != connectionGeneration)
            {
                return false;
            }

            outboundMessages.Enqueue(new OutboundMessage(generation, message));
            return true;
        }

        internal void Close(int generation)
        {
            if (generation != 0 && generation != connectionGeneration)
            {
                return;
            }

            Close();
        }

        internal void Close()
        {
            isOpen = false;
            connectionGeneration = 0;
#if !UNITY_WEBGL || UNITY_EDITOR
            sendPumpActive = false;
#endif
            while (outboundMessages.TryDequeue(out _))
            {
            }

#if UNITY_WEBGL && !UNITY_EDITOR
            if (activeBrowserSocket == this)
            {
                CowartXunfeiIat_Close();
                activeBrowserSocket = null;
                activeBrowserGeneration = 0;
            }
#else
            CancellationTokenSource oldCancellation = cancellation;
            ClientWebSocket oldSocket = socket;
            cancellation = null;
            socket = null;
            if (oldCancellation != null)
            {
                try
                {
                    oldCancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                oldCancellation.Dispose();
            }

            if (oldSocket != null)
            {
                try
                {
                    oldSocket.Dispose();
                }
                catch (Exception)
                {
                }
            }
#endif
        }

#if !UNITY_WEBGL || UNITY_EDITOR
        private async Task ConnectDotNetAsync(
            ClientWebSocket connectedSocket,
            int generation,
            string url,
            CancellationToken token)
        {
            try
            {
                await connectedSocket.ConnectAsync(new Uri(url), token);
                EnqueueForGeneration(generation, () =>
                {
                    if (connectedSocket != socket)
                    {
                        return;
                    }

                    isOpen = true;
                    Opened?.Invoke(generation);
                });
                await ReceiveDotNetMessagesAsync(connectedSocket, generation, token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception exception)
            {
                EnqueueForGeneration(
                    generation,
                    () => RaiseFailure(generation, "WebSocket 连接失败：" + exception.Message));
            }
        }

        private async Task ReceiveDotNetMessagesAsync(
            ClientWebSocket connectedSocket,
            int generation,
            CancellationToken token)
        {
            byte[] buffer = new byte[8192];
            using (MemoryStream messageBytes = new MemoryStream())
            {
                while (!token.IsCancellationRequested &&
                       connectedSocket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result = await connectedSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        string closeMessage =
                            "code=" + (int?)result.CloseStatus +
                            ", reason=" + (result.CloseStatusDescription ?? string.Empty);
                        EnqueueForGeneration(generation, () =>
                        {
                            isOpen = false;
                            Closed?.Invoke(generation, closeMessage);
                        });
                        return;
                    }

                    messageBytes.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage)
                    {
                        continue;
                    }

                    string message = Encoding.UTF8.GetString(
                        messageBytes.GetBuffer(),
                        0,
                        (int)messageBytes.Length);
                    messageBytes.SetLength(0);
                    EnqueueForGeneration(
                        generation,
                        () => MessageReceived?.Invoke(generation, message));
                }
            }

            if (!token.IsCancellationRequested)
            {
                EnqueueForGeneration(generation, () =>
                {
                    isOpen = false;
                    Closed?.Invoke(generation, "WebSocket 接收循环已结束。");
                });
            }
        }

        private async Task PumpDotNetMessagesAsync(
            ClientWebSocket connectedSocket,
            int generation,
            CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested &&
                       generation == connectionGeneration &&
                       outboundMessages.TryDequeue(out OutboundMessage outbound))
                {
                    if (outbound.Generation != generation)
                    {
                        continue;
                    }

                    byte[] bytes = Encoding.UTF8.GetBytes(outbound.Value);
                    await connectedSocket.SendAsync(
                        new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text,
                        true,
                        token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception exception)
            {
                EnqueueForGeneration(
                    generation,
                    () => RaiseFailure(generation, "WebSocket 发送失败：" + exception.Message));
            }
            finally
            {
                EnqueueForGeneration(generation, () => sendPumpActive = false);
            }
        }

        private void EnqueueForGeneration(int generation, Action action)
        {
            mainThreadActions.Enqueue(() =>
            {
                if (generation == connectionGeneration)
                {
                    action?.Invoke();
                }
            });
        }
#endif

        private void RaiseFailure(int generation, string message)
        {
            if (generation == 0 || generation != connectionGeneration)
            {
                return;
            }

            isOpen = false;
            Failed?.Invoke(generation, message);
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        private delegate int BrowserUtf8Function(IntPtr bytes, int length);

        private static bool InvokeBrowserUtf8(string value, BrowserUtf8Function function)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                return function(handle.AddrOfPinnedObject(), bytes.Length) != 0;
            }
            finally
            {
                handle.Free();
            }
        }

        private static bool SendBrowserUtf8(string value)
        {
            return InvokeBrowserUtf8(value, CowartXunfeiIat_Send);
        }

        [MonoPInvokeCallback(typeof(StateCallback))]
        private static void HandleBrowserState(int state, IntPtr bytes, int length)
        {
            XunfeiIatWebSocket target = activeBrowserSocket;
            int generation = activeBrowserGeneration;
            if (target == null || generation == 0 ||
                target.connectionGeneration != generation)
            {
                return;
            }

            string message = DecodeUtf8(bytes, length);
            if (state == 0)
            {
                target.isOpen = true;
                target.Opened?.Invoke(generation);
            }
            else if (state == 1)
            {
                target.RaiseFailure(
                    generation,
                    string.IsNullOrEmpty(message)
                        ? "浏览器 WebSocket 发生错误。"
                        : message);
            }
            else
            {
                target.isOpen = false;
                target.Closed?.Invoke(generation, message);
            }
        }

        [MonoPInvokeCallback(typeof(MessageCallback))]
        private static void HandleBrowserMessage(IntPtr bytes, int length)
        {
            XunfeiIatWebSocket target = activeBrowserSocket;
            int generation = activeBrowserGeneration;
            if (target != null && generation != 0 &&
                target.connectionGeneration == generation)
            {
                target.MessageReceived?.Invoke(generation, DecodeUtf8(bytes, length));
            }
        }

        private static string DecodeUtf8(IntPtr bytes, int length)
        {
            if (bytes == IntPtr.Zero || length <= 0)
            {
                return string.Empty;
            }

            byte[] managedBytes = new byte[length];
            Marshal.Copy(bytes, managedBytes, 0, length);
            return Encoding.UTF8.GetString(managedBytes);
        }
#endif

        private readonly struct OutboundMessage
        {
            internal OutboundMessage(int generation, string value)
            {
                Generation = generation;
                Value = value;
            }

            internal int Generation { get; }
            internal string Value { get; }
        }
    }
}
