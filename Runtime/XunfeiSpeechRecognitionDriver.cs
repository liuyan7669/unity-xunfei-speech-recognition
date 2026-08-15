using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Cowart.XunfeiSpeechRecognition
{
    [AddComponentMenu("")]
    internal sealed class XunfeiSpeechRecognitionDriver : MonoBehaviour
    {
        private const float ConnectionTimeoutSeconds = 20f;
        private const float FinalResultTimeoutSeconds = 30f;
        private const float AudioClipLoadTimeoutSeconds = 30f;
        private const int MaximumSourceSampleValuesPerUpdate = 32768;
        private const int MaximumEncodedFramesPerUpdate = 4096;

        private readonly XunfeiIatResultAssembler resultAssembler =
            new XunfeiIatResultAssembler();

        private XunfeiIatWebSocket webSocket;
        private Pcm16FrameQueue frameQueue;
        private XunfeiAudioFrameSequencer frameSequencer;
        private XunfeiRuntimeConfiguration configuration;
        private Coroutine audioClipConversion;
        private XunfeiRecognitionState state = XunfeiRecognitionState.Idle;
        private XunfeiRecognitionInputKind inputKind;
        private int nextGeneration;
        private int activeGeneration;
        private bool terminalPublished;
        private bool initialized;
        private float phaseDeadline;
        private float nextAudioFrameAt;

        internal XunfeiRecognitionState State => state;
        internal XunfeiRecognitionInputKind InputKind => inputKind;
        internal bool IsBusy => state != XunfeiRecognitionState.Idle;
        internal string CurrentText => resultAssembler.CurrentText;

        private void Awake()
        {
            Initialize();
        }

        internal void Initialize()
        {
            if (initialized)
            {
                return;
            }

            webSocket = GetComponent<XunfeiIatWebSocket>();
            if (webSocket == null)
            {
                webSocket = gameObject.AddComponent<XunfeiIatWebSocket>();
            }

            webSocket.Opened += HandleSocketOpened;
            webSocket.MessageReceived += HandleSocketMessage;
            webSocket.Failed += HandleSocketFailed;
            webSocket.Closed += HandleSocketClosed;
            initialized = true;
        }

        private void Update()
        {
            if (!IsBusy)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (state == XunfeiRecognitionState.Connecting && now >= phaseDeadline)
            {
                Fail(activeGeneration, "连接讯飞服务超时。");
                return;
            }

            if (state == XunfeiRecognitionState.AwaitingFinalResult && now >= phaseDeadline)
            {
                Fail(activeGeneration, "等待讯飞最终识别结果超时。");
                return;
            }

            if (state != XunfeiRecognitionState.SendingAudio ||
                !webSocket.IsOpen || now < nextAudioFrameAt)
            {
                return;
            }

            bool frameSent = SendNextAudioFrame(activeGeneration);
            if (state == XunfeiRecognitionState.SendingAudio)
            {
                nextAudioFrameAt = CalculateNextAudioFrameAt(
                    nextAudioFrameAt,
                    now,
                    frameSent);
            }
        }

        private void OnDestroy()
        {
            int generation = activeGeneration;
            if (audioClipConversion != null)
            {
                StopCoroutine(audioClipConversion);
                audioClipConversion = null;
            }

            if (initialized && webSocket != null)
            {
                webSocket.Opened -= HandleSocketOpened;
                webSocket.MessageReceived -= HandleSocketMessage;
                webSocket.Failed -= HandleSocketFailed;
                webSocket.Closed -= HandleSocketClosed;
                webSocket.Close();
            }

            ResetSession(generation);
            XunfeiSpeechRecognizer.NotifyDriverDestroyed(this);
        }

        internal static float CalculateNextAudioFrameAt(
            float previousScheduledTime,
            float now,
            bool frameSent)
        {
            float interval = XunfeiIatProtocol.ChunkMilliseconds / 1000f;
            if (!frameSent)
            {
                return now + interval;
            }

            float next = previousScheduledTime + interval;
            if (next < now - interval)
            {
                // Do not burst rapidly for a long rendering hitch. Re-anchor only when
                // more than one full protocol interval has already been lost.
                return now + interval;
            }

            return next;
        }

        internal bool StartCompletePcm(
            byte[] pcmBytes,
            XunfeiRecognitionInputKind kind,
            XunfeiRuntimeConfiguration runtimeConfiguration)
        {
            Initialize();
            if (IsBusy || pcmBytes == null || pcmBytes.Length == 0 ||
                (pcmBytes.Length & 1) != 0 ||
                pcmBytes.Length > XunfeiSpeechRecognizer.MaximumPcmBytes)
            {
                return false;
            }

            int generation = BeginSession(kind, runtimeConfiguration, XunfeiRecognitionState.Connecting);
            XunfeiSpeechRecognizer.NotifyStarted(this, generation);
            if (!IsCurrent(generation))
            {
                return true;
            }

            if (!frameQueue.TrySetCompleteBuffer(pcmBytes, out string error))
            {
                Fail(generation, error);
                return true;
            }

            if (IsCurrent(generation))
            {
                Connect(generation);
            }

            return true;
        }

        internal bool StartAudioClip(
            AudioClip clip,
            int sourceFramesPerUpdate,
            XunfeiRuntimeConfiguration runtimeConfiguration)
        {
            Initialize();
            if (IsBusy || clip == null)
            {
                return false;
            }

            int generation = BeginSession(
                XunfeiRecognitionInputKind.AudioClip,
                runtimeConfiguration,
                XunfeiRecognitionState.PreparingAudio);
            XunfeiSpeechRecognizer.NotifyStarted(this, generation);
            if (IsCurrent(generation))
            {
                audioClipConversion = StartCoroutine(
                    ConvertAudioClip(generation, clip, sourceFramesPerUpdate));
            }

            return true;
        }

        internal bool StartStreaming(XunfeiRuntimeConfiguration runtimeConfiguration)
        {
            Initialize();
            if (IsBusy)
            {
                return false;
            }

            int generation = BeginSession(
                XunfeiRecognitionInputKind.Pcm16Stream,
                runtimeConfiguration,
                XunfeiRecognitionState.WaitingForAudio);
            XunfeiSpeechRecognizer.NotifyStarted(this, generation);
            if (IsCurrent(generation) && state == XunfeiRecognitionState.WaitingForAudio)
            {
                XunfeiSpeechRecognizer.NotifyStatus(
                    this,
                    generation,
                    "讯飞流式会话已建立，正在等待第一块 PCM 音频……");
            }

            return true;
        }

        internal bool WriteStreamingPcm(
            byte[] pcmBytes,
            int offset,
            int count,
            bool isLast)
        {
            if (!IsBusy || inputKind != XunfeiRecognitionInputKind.Pcm16Stream ||
                frameQueue.IsInputCompleted)
            {
                return false;
            }

            int generation = activeGeneration;
            if (count == 0 && isLast && frameQueue.TotalPcmBytes == 0)
            {
                Fail(generation, "实时 PCM 输入不能为空。");
                return false;
            }

            if (!frameQueue.TryAppend(pcmBytes, offset, count, isLast, out string error))
            {
                Fail(generation, error);
                return false;
            }

            if (frameQueue.TotalPcmBytes == XunfeiSpeechRecognizer.MaximumPcmBytes)
            {
                XunfeiSpeechRecognizer.NotifyStatus(
                    this,
                    generation,
                    "实时输入已达到 60 秒上限，讯飞输入已自动结束；外部录音不会被停止。");
            }

            if (count > 0 && state == XunfeiRecognitionState.WaitingForAudio &&
                IsCurrent(generation))
            {
                Connect(generation);
            }

            return true;
        }

        internal bool CompleteStreaming()
        {
            if (!IsBusy || inputKind != XunfeiRecognitionInputKind.Pcm16Stream ||
                frameQueue.IsInputCompleted)
            {
                return false;
            }

            int generation = activeGeneration;
            if (frameQueue.TotalPcmBytes == 0)
            {
                Fail(generation, "实时 PCM 输入不能为空。");
                return false;
            }

            if (!frameQueue.TryComplete(out string error))
            {
                Fail(generation, error);
                return false;
            }

            XunfeiSpeechRecognizer.NotifyStatus(
                this,
                generation,
                "实时音频输入已结束，正在发送剩余数据。");
            return true;
        }

        internal bool CancelRecognition()
        {
            if (!IsBusy)
            {
                return false;
            }

            int generation = activeGeneration;
            if (!TryFinishSession(generation))
            {
                return false;
            }

            XunfeiSpeechRecognizer.NotifyCanceled();
            return true;
        }

        private int BeginSession(
            XunfeiRecognitionInputKind kind,
            XunfeiRuntimeConfiguration runtimeConfiguration,
            XunfeiRecognitionState initialState)
        {
            activeGeneration = ++nextGeneration;
            inputKind = kind;
            configuration = runtimeConfiguration;
            state = initialState;
            terminalPublished = false;
            phaseDeadline = 0f;
            nextAudioFrameAt = 0f;
            resultAssembler.Reset();
            frameQueue = new Pcm16FrameQueue(
                XunfeiIatProtocol.BytesPerChunk,
                XunfeiSpeechRecognizer.MaximumPcmBytes);
            frameSequencer = new XunfeiAudioFrameSequencer();
            return activeGeneration;
        }

        private void Connect(int generation)
        {
            if (!IsCurrent(generation))
            {
                return;
            }

            string url;
            try
            {
                url = XunfeiIatProtocol.CreateAuthenticatedUrl(
                    configuration.ApiKey,
                    configuration.ApiSecret,
                    DateTime.UtcNow);
            }
            catch (Exception exception)
            {
                Fail(generation, exception.Message);
                return;
            }

            state = XunfeiRecognitionState.Connecting;
            phaseDeadline = Time.realtimeSinceStartup + ConnectionTimeoutSeconds;
            XunfeiSpeechRecognizer.NotifyStatus(
                this,
                generation,
                "正在连接讯飞语音听写服务……");
            if (IsCurrent(generation))
            {
                webSocket.Connect(url, generation);
            }
        }

        private IEnumerator ConvertAudioClip(
            int generation,
            AudioClip clip,
            int sourceFramesPerUpdate)
        {
            // StartCoroutine executes immediately until the first yield. Always defer the
            // potentially expensive AudioClip read/conversion work to a later Unity frame.
            yield return null;
            if (!IsCurrent(generation))
            {
                yield break;
            }

            if (clip == null)
            {
                Fail(generation, "AudioClip 已被销毁。");
                yield break;
            }

            int sourceFrames = clip.samples;
            int sourceRate = clip.frequency;
            int channels = clip.channels;
            if (sourceFrames <= 0 || sourceRate <= 0 || channels <= 0)
            {
                Fail(generation, "AudioClip 没有可识别的音频数据。");
                yield break;
            }

            double durationSeconds = sourceFrames / (double)sourceRate;
            if (durationSeconds > XunfeiSpeechRecognizer.MaximumAudioSeconds + 0.000001d)
            {
                Fail(generation, "AudioClip 超过讯飞单次语音听写 60 秒上限。");
                yield break;
            }

            if (clip.loadState == AudioDataLoadState.Unloaded && !clip.LoadAudioData())
            {
                Fail(generation, "AudioClip 音频数据无法开始加载。");
                yield break;
            }

            float loadDeadline = Time.realtimeSinceStartup + AudioClipLoadTimeoutSeconds;
            while (IsCurrent(generation) && clip != null &&
                   clip.loadState != AudioDataLoadState.Loaded)
            {
                if (clip.loadState == AudioDataLoadState.Failed)
                {
                    Fail(generation, "AudioClip 音频数据加载失败。");
                    yield break;
                }

                if (Time.realtimeSinceStartup >= loadDeadline)
                {
                    Fail(generation, "等待 AudioClip 音频数据加载超时。");
                    yield break;
                }

                yield return null;
            }

            if (!IsCurrent(generation))
            {
                yield break;
            }

            if (clip == null || clip.loadState == AudioDataLoadState.Failed)
            {
                Fail(generation, "AudioClip 音频数据加载失败。");
                yield break;
            }

            XunfeiPcm16AudioEncoder encoder;
            try
            {
                encoder = new XunfeiPcm16AudioEncoder(
                    sourceFrames,
                    sourceRate,
                    channels);
            }
            catch (Exception exception)
            {
                Fail(generation, "无法初始化 AudioClip 转换：" + exception.Message);
                yield break;
            }

            if (encoder.OutputByteCount > XunfeiSpeechRecognizer.MaximumPcmBytes)
            {
                Fail(generation, "AudioClip 转换后超过讯飞单次语音听写 60 秒上限。");
                yield break;
            }

            int effectiveFramesPerUpdate = Math.Min(
                sourceFramesPerUpdate,
                Math.Max(1, MaximumSourceSampleValuesPerUpdate / channels));
            int regularReadFrames = Math.Min(
                effectiveFramesPerUpdate + 1,
                sourceFrames);
            float[] regularBuffer;
            try
            {
                regularBuffer = new float[checked(regularReadFrames * channels)];
            }
            catch (Exception exception)
            {
                Fail(generation, "无法分配 AudioClip 转换缓冲区：" + exception.Message);
                yield break;
            }

            int chunkStartFrame = 0;

            while (chunkStartFrame < sourceFrames && !encoder.IsComplete)
            {
                if (!IsCurrent(generation) || clip == null)
                {
                    yield break;
                }

                int readFrames = Math.Min(
                    effectiveFramesPerUpdate + 1,
                    sourceFrames - chunkStartFrame);
                float[] interleaved = regularBuffer;
                bool readSucceeded;
                try
                {
                    if (readFrames != regularReadFrames)
                    {
                        // GetData wraps when the destination is longer than the remaining
                        // clip data, so the final block must use its exact sample count.
                        interleaved = new float[checked(readFrames * channels)];
                    }

                    readSucceeded = clip.GetData(interleaved, chunkStartFrame);
                }
                catch (Exception exception)
                {
                    Fail(generation, "读取 AudioClip 失败：" + exception.Message);
                    yield break;
                }

                if (!readSucceeded)
                {
                    Fail(generation, "AudioClip 不可读；请使用已加载且允许 GetData 的音频资源。");
                    yield break;
                }

                bool chunkNeedsMoreProcessing;
                do
                {
                    if (!IsCurrent(generation))
                    {
                        yield break;
                    }

                    if (!encoder.TryWriteChunk(
                            interleaved,
                            chunkStartFrame,
                            readFrames,
                            MaximumEncodedFramesPerUpdate,
                            out chunkNeedsMoreProcessing,
                            out string conversionError))
                    {
                        Fail(generation, conversionError);
                        yield break;
                    }

                    if (chunkNeedsMoreProcessing)
                    {
                        yield return null;
                    }
                }
                while (chunkNeedsMoreProcessing);

                int chunkEndFrame = chunkStartFrame + readFrames - 1;
                if (chunkEndFrame >= sourceFrames - 1)
                {
                    break;
                }

                chunkStartFrame += effectiveFramesPerUpdate;
                yield return null;
            }

            audioClipConversion = null;
            if (!IsCurrent(generation))
            {
                yield break;
            }

            if (!encoder.IsComplete)
            {
                Fail(generation, "AudioClip 分帧重采样未能生成完整 PCM 数据。");
                yield break;
            }

            byte[] pcmBytes = encoder.GetCompletedPcm();
            if (!frameQueue.TrySetCompleteBuffer(pcmBytes, out string error))
            {
                Fail(generation, error);
                yield break;
            }

            Connect(generation);
        }

        private void HandleSocketOpened(int generation)
        {
            if (!IsCurrent(generation))
            {
                return;
            }

            state = XunfeiRecognitionState.SendingAudio;
            nextAudioFrameAt = Time.realtimeSinceStartup;
            XunfeiSpeechRecognizer.NotifyStatus(
                this,
                generation,
                "讯飞已连接，正在发送音频并识别……");
        }

        private bool SendNextAudioFrame(int generation)
        {
            if (!IsCurrent(generation) || frameSequencer == null ||
                !frameSequencer.TryGetNext(frameQueue, out Pcm16Frame frame, out int status))
            {
                return false;
            }

            if (!SendFrame(
                    generation,
                    frame.Data,
                    frame.Offset,
                    frame.Count,
                    status))
            {
                return false;
            }

            if (frameSequencer.FinalFrameSent)
            {
                state = XunfeiRecognitionState.AwaitingFinalResult;
                phaseDeadline = Time.realtimeSinceStartup + FinalResultTimeoutSeconds;
                XunfeiSpeechRecognizer.NotifyStatus(
                    this,
                    generation,
                    "音频已发送完毕，正在等待最终识别结果……");
                return true;
            }

            return true;
        }

        private bool SendFrame(
            int generation,
            byte[] pcmBytes,
            int offset,
            int count,
            int status)
        {
            string message;
            try
            {
                message = XunfeiIatProtocol.CreateAudioFrame(
                    configuration.AppId,
                    pcmBytes,
                    offset,
                    count,
                    status,
                    configuration.Language,
                    configuration.Accent);
            }
            catch (Exception exception)
            {
                Fail(generation, exception.Message);
                return false;
            }

            if (!webSocket.Send(message, generation))
            {
                Fail(generation, "WebSocket 当前无法接受音频数据。");
                return false;
            }

            return true;
        }

        private void HandleSocketMessage(int generation, string message)
        {
            if (!IsCurrent(generation))
            {
                return;
            }

            XunfeiIatResponse response;
            try
            {
                response = resultAssembler.Apply(message);
            }
            catch (Exception exception)
            {
                Fail(generation, "讯飞响应解析失败：" + exception.Message);
                return;
            }

            if (response.Code != 0)
            {
                Fail(generation, response.Message, response.Code, response.Sid);
                return;
            }

            XunfeiSpeechRecognizer.NotifyPartialText(this, generation, response.Text);
            if (!IsCurrent(generation) || !response.IsFinal)
            {
                return;
            }

            XunfeiRecognitionResult result = new XunfeiRecognitionResult(
                response.Text,
                response.Sid,
                inputKind,
                frameQueue.TotalPcmBytes);
            if (!TryFinishSession(generation))
            {
                return;
            }

            XunfeiSpeechRecognizer.NotifyCompleted(result);
        }

        private void HandleSocketFailed(int generation, string message)
        {
            Fail(generation, message);
        }

        private void HandleSocketClosed(int generation, string message)
        {
            if (!IsCurrent(generation))
            {
                return;
            }

            string detail = string.IsNullOrWhiteSpace(message)
                ? "WebSocket 在最终识别结果返回前关闭。"
                : "WebSocket 在最终识别结果返回前关闭：" + message;
            Fail(generation, detail);
        }

        private void Fail(
            int generation,
            string message,
            int serviceCode = -1,
            string serviceSessionId = "")
        {
            if (!TryFinishSession(generation))
            {
                return;
            }

            XunfeiRecognitionError error = new XunfeiRecognitionError(
                serviceCode,
                message,
                serviceSessionId);
            XunfeiSpeechRecognizer.NotifyFailed(error);
        }

        private bool TryFinishSession(int generation)
        {
            if (!IsCurrent(generation) || terminalPublished)
            {
                return false;
            }

            terminalPublished = true;
            if (audioClipConversion != null)
            {
                StopCoroutine(audioClipConversion);
                audioClipConversion = null;
            }

            webSocket.Close(generation);
            ResetSession(generation);
            return true;
        }

        private void ResetSession(int generation)
        {
            if (generation != 0 && activeGeneration != generation)
            {
                return;
            }

            activeGeneration = 0;
            state = XunfeiRecognitionState.Idle;
            inputKind = XunfeiRecognitionInputKind.Pcm16Wav;
            configuration = null;
            phaseDeadline = 0f;
            nextAudioFrameAt = 0f;
            frameQueue?.Clear();
            frameQueue = null;
            frameSequencer = null;
            resultAssembler.Reset();
        }

        internal bool IsSessionCurrent(int generation)
        {
            return IsCurrent(generation);
        }

        private bool IsCurrent(int generation)
        {
            return generation != 0 && generation == activeGeneration && !terminalPublished;
        }
    }

    internal readonly struct Pcm16Frame
    {
        internal Pcm16Frame(byte[] data, int offset, int count, bool isLast)
        {
            Data = data;
            Offset = offset;
            Count = count;
            IsLast = isLast;
        }

        internal byte[] Data { get; }
        internal int Offset { get; }
        internal int Count { get; }
        internal bool IsLast { get; }
    }

    internal sealed class XunfeiAudioFrameSequencer
    {
        private bool firstFrameSent;
        private bool finalMarkerPending;

        internal bool FinalFrameSent { get; private set; }

        internal bool TryGetNext(
            Pcm16FrameQueue queue,
            out Pcm16Frame frame,
            out int status)
        {
            if (FinalFrameSent || queue == null)
            {
                frame = default;
                status = 0;
                return false;
            }

            if (finalMarkerPending)
            {
                finalMarkerPending = false;
                FinalFrameSent = true;
                frame = new Pcm16Frame(Array.Empty<byte>(), 0, 0, true);
                status = 2;
                return true;
            }

            if (!queue.TryDequeue(out frame))
            {
                status = 0;
                return false;
            }

            status = !firstFrameSent ? 0 : (frame.IsLast ? 2 : 1);
            firstFrameSent = true;
            if (frame.IsLast)
            {
                if (status == 2)
                {
                    FinalFrameSent = true;
                }
                else
                {
                    // A protocol session must start with status 0 and end with status 2.
                    // Return the empty final marker on the next scheduling tick.
                    finalMarkerPending = true;
                }
            }

            return true;
        }
    }

    internal sealed class Pcm16FrameQueue
    {
        private readonly Queue<byte[]> frames = new Queue<byte[]>();
        private readonly int frameByteCount;
        private readonly int maximumByteCount;
        private byte[] partialFrame;
        private int partialCount;
        private byte[] completeBuffer;
        private int completeBufferOffset;
        private bool finalMarkerDequeued;

        internal Pcm16FrameQueue(int frameByteCount, int maximumByteCount)
        {
            if (frameByteCount <= 0 || maximumByteCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frameByteCount));
            }

            this.frameByteCount = frameByteCount;
            this.maximumByteCount = maximumByteCount;
            partialFrame = new byte[frameByteCount];
        }

        internal int TotalPcmBytes { get; private set; }
        internal bool IsInputCompleted { get; private set; }

        /// <summary>
        /// Takes ownership of one complete PCM buffer and exposes frame-sized slices without
        /// allocating and copying every protocol frame up front.
        /// </summary>
        internal bool TrySetCompleteBuffer(byte[] ownedBuffer, out string error)
        {
            error = string.Empty;
            if (IsInputCompleted || TotalPcmBytes != 0 || frames.Count != 0 || partialCount != 0)
            {
                error = "PCM 输入队列已经包含数据或已经结束。";
                return false;
            }

            if (ownedBuffer == null || ownedBuffer.Length == 0)
            {
                error = "PCM 数据不能为空。";
                return false;
            }

            if ((ownedBuffer.Length & 1) != 0)
            {
                error = "PCM16 字节数必须是 2 的整数倍。";
                return false;
            }

            if (ownedBuffer.Length > maximumByteCount)
            {
                error = "PCM 输入超过讯飞单次语音听写 60 秒上限。";
                return false;
            }

            completeBuffer = ownedBuffer;
            completeBufferOffset = 0;
            TotalPcmBytes = ownedBuffer.Length;
            IsInputCompleted = true;
            return true;
        }

        internal bool TryAppend(
            byte[] source,
            int offset,
            int count,
            bool isLast,
            out string error)
        {
            error = string.Empty;
            if (IsInputCompleted)
            {
                error = "实时 PCM 输入已经结束。";
                return false;
            }

            if (source == null)
            {
                error = "PCM 数据不能为空引用。";
                return false;
            }

            if (offset < 0 || count < 0 || offset > source.Length - count)
            {
                error = "PCM 数据范围无效。";
                return false;
            }

            if ((count & 1) != 0)
            {
                error = "PCM16 每次写入的字节数必须是 2 的整数倍。";
                return false;
            }

            if (count == 0 && isLast && TotalPcmBytes == 0)
            {
                error = "实时 PCM 输入不能为空。";
                return false;
            }

            if (TotalPcmBytes > maximumByteCount - count)
            {
                error = "实时 PCM 输入超过讯飞单次语音听写 60 秒上限。";
                return false;
            }

            int remaining = count;
            int sourceOffset = offset;
            while (remaining > 0)
            {
                int copyCount = Math.Min(frameByteCount - partialCount, remaining);
                Buffer.BlockCopy(source, sourceOffset, partialFrame, partialCount, copyCount);
                partialCount += copyCount;
                sourceOffset += copyCount;
                remaining -= copyCount;

                if (partialCount == frameByteCount)
                {
                    frames.Enqueue(partialFrame);
                    partialFrame = new byte[frameByteCount];
                    partialCount = 0;
                }
            }

            TotalPcmBytes += count;
            if (isLast || TotalPcmBytes == maximumByteCount)
            {
                CompleteInput();
            }

            return true;
        }

        internal bool TryComplete(out string error)
        {
            if (IsInputCompleted)
            {
                error = "实时 PCM 输入已经结束。";
                return false;
            }

            if (TotalPcmBytes == 0)
            {
                error = "实时 PCM 输入不能为空。";
                return false;
            }

            CompleteInput();
            error = string.Empty;
            return true;
        }

        internal bool TryDequeue(out Pcm16Frame frame)
        {
            if (completeBuffer != null && completeBufferOffset < completeBuffer.Length)
            {
                int offset = completeBufferOffset;
                int count = Math.Min(frameByteCount, completeBuffer.Length - offset);
                completeBufferOffset += count;
                bool isLast = completeBufferOffset == completeBuffer.Length;
                frame = new Pcm16Frame(completeBuffer, offset, count, isLast);
                if (isLast)
                {
                    finalMarkerDequeued = true;
                    completeBuffer = null;
                }

                return true;
            }

            if (frames.Count > 0)
            {
                byte[] data = frames.Dequeue();
                bool isLast = IsInputCompleted && frames.Count == 0;
                frame = new Pcm16Frame(data, 0, data.Length, isLast);
                if (isLast)
                {
                    finalMarkerDequeued = true;
                }

                return true;
            }

            if (IsInputCompleted && !finalMarkerDequeued)
            {
                finalMarkerDequeued = true;
                frame = new Pcm16Frame(Array.Empty<byte>(), 0, 0, true);
                return true;
            }

            frame = default;
            return false;
        }

        internal void Clear()
        {
            frames.Clear();
            partialFrame = null;
            partialCount = 0;
            completeBuffer = null;
            completeBufferOffset = 0;
            TotalPcmBytes = 0;
            IsInputCompleted = true;
            finalMarkerDequeued = true;
        }

        private void CompleteInput()
        {
            if (partialCount > 0)
            {
                byte[] tail = new byte[partialCount];
                Buffer.BlockCopy(partialFrame, 0, tail, 0, partialCount);
                frames.Enqueue(tail);
                partialCount = 0;
            }

            IsInputCompleted = true;
        }
    }
}
