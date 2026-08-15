using System;
using System.Threading;
using UnityEngine;
using UnityEngine.Scripting;

namespace Cowart.XunfeiSpeechRecognition
{
    public enum XunfeiRecognitionState
    {
        Idle,
        WaitingForAudio,
        PreparingAudio,
        Connecting,
        SendingAudio,
        AwaitingFinalResult
    }

    public enum XunfeiRecognitionInputKind
    {
        Pcm16Wav,
        Pcm16Bytes,
        AudioClip,
        Pcm16Stream
    }

    public sealed class XunfeiRecognitionResult
    {
        internal XunfeiRecognitionResult(
            string text,
            string serviceSessionId,
            XunfeiRecognitionInputKind inputKind,
            int pcmByteCount)
        {
            Text = text ?? string.Empty;
            ServiceSessionId = serviceSessionId ?? string.Empty;
            InputKind = inputKind;
            PcmByteCount = pcmByteCount;
        }

        public string Text { get; }
        public string ServiceSessionId { get; }
        public XunfeiRecognitionInputKind InputKind { get; }
        public int PcmByteCount { get; }
        public double AudioDurationSeconds =>
            PcmByteCount / (double)XunfeiSpeechRecognizer.PcmBytesPerSecond;
    }

    public sealed class XunfeiRecognitionError
    {
        internal XunfeiRecognitionError(int serviceCode, string message, string serviceSessionId)
        {
            ServiceCode = serviceCode;
            Message = message ?? string.Empty;
            ServiceSessionId = serviceSessionId ?? string.Empty;
        }

        /// <summary>-1 表示本地校验、转换、超时或传输错误；其他值为服务端错误码。</summary>
        public int ServiceCode { get; }
        public string Message { get; }
        public string ServiceSessionId { get; }
    }

    /// <summary>
    /// 科大讯飞语音听写唯一静态入口。包只消费调用方提供的音频数据，不启动麦克风，
    /// 也不依赖任何录音包。
    /// </summary>
    [Preserve]
    public static class XunfeiSpeechRecognizer
    {
        public const int RequiredSampleRate = 16000;
        public const int RequiredChannels = 1;
        public const int RequiredBitsPerSample = 16;
        public const int MaximumAudioSeconds = 60;
        public const int PcmBytesPerSecond =
            RequiredSampleRate * RequiredChannels * RequiredBitsPerSample / 8;
        public const int MaximumPcmBytes = MaximumAudioSeconds * PcmBytesPerSecond;

        private static int mainThreadId = Thread.CurrentThread.ManagedThreadId;
        private static XunfeiSpeechRecognitionDriver driver;
        private static string appId = string.Empty;
        private static string apiKey = string.Empty;
        private static string apiSecret = string.Empty;
        private static string language = "zh_cn";
        private static string accent = "mandarin";
        private static XunfeiRecognitionResult lastResult;

        public static event Action RecognitionStarted;
        public static event Action<string> StatusChanged;
        public static event Action<string> PartialTextChanged;
        public static event Action<XunfeiRecognitionResult> RecognitionCompleted;
        public static event Action RecognitionCanceled;
        public static event Action<XunfeiRecognitionError> RecognitionFailed;

        public static bool IsAvailable
        {
            get
            {
#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_ANDROID || UNITY_WEBGL
                return true;
#else
                return false;
#endif
            }
        }

        public static bool HasRuntimeCredentials =>
            !string.IsNullOrWhiteSpace(appId) &&
            !string.IsNullOrWhiteSpace(apiKey) &&
            !string.IsNullOrWhiteSpace(apiSecret);

        public static XunfeiRecognitionState State =>
            driver != null ? driver.State : XunfeiRecognitionState.Idle;

        public static bool IsRecognizing => State != XunfeiRecognitionState.Idle;
        public static bool IsStreaming =>
            driver != null && driver.InputKind == XunfeiRecognitionInputKind.Pcm16Stream;
        public static string CurrentText => driver != null ? driver.CurrentText : string.Empty;
        public static XunfeiRecognitionResult LastResult => lastResult;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            driver = null;
            appId = string.Empty;
            apiKey = string.Empty;
            apiSecret = string.Empty;
            language = "zh_cn";
            accent = "mandarin";
            lastResult = null;
            RecognitionStarted = null;
            StatusChanged = null;
            PartialTextChanged = null;
            RecognitionCompleted = null;
            RecognitionCanceled = null;
            RecognitionFailed = null;
        }

        /// <summary>
        /// 设置仅保存在当前进程内存中的运行时凭据。不要把 APISecret 序列化进场景或资源。
        /// </summary>
        public static void SetRuntimeCredentials(
            string newAppId,
            string newApiKey,
            string newApiSecret,
            string newLanguage = "zh_cn",
            string newAccent = "mandarin")
        {
            EnsureMainThread();
            string validatedAppId = RequireText(newAppId, nameof(newAppId));
            string validatedApiKey = RequireText(newApiKey, nameof(newApiKey));
            string validatedApiSecret = RequireText(newApiSecret, nameof(newApiSecret));
            string validatedLanguage = RequireText(newLanguage, nameof(newLanguage));
            string validatedAccent = RequireText(newAccent, nameof(newAccent));

            appId = validatedAppId;
            apiKey = validatedApiKey;
            apiSecret = validatedApiSecret;
            language = validatedLanguage;
            accent = validatedAccent;
            NotifyStatus("运行时凭据已设置，仅保存在当前进程内存。");
        }

        public static void ClearRuntimeCredentials()
        {
            EnsureMainThread();
            appId = string.Empty;
            apiKey = string.Empty;
            apiSecret = string.Empty;
            NotifyStatus("运行时凭据已清除。");
        }

        /// <summary>识别完整的 16 kHz、16-bit、单声道 PCM WAV。</summary>
        public static bool RecognizeWav(byte[] wavBytes)
        {
            EnsureMainThread();
            if (wavBytes == null)
            {
                throw new ArgumentNullException(nameof(wavBytes));
            }

            if (!CanAcceptNewRequest())
            {
                return false;
            }

            byte[] pcmBytes;
            try
            {
                pcmBytes = XunfeiIatProtocol.ExtractPcm16FromWav(wavBytes);
            }
            catch (Exception exception)
            {
                LogRejected(exception.Message);
                return false;
            }

            if (!ValidatePcmBytes(pcmBytes.Length))
            {
                return false;
            }

            return EnsureDriver().StartCompletePcm(
                pcmBytes,
                XunfeiRecognitionInputKind.Pcm16Wav,
                GetCredentialsSnapshot());
        }

        /// <summary>识别一整段无文件头的 PCM16 Little Endian 数据。</summary>
        public static bool RecognizePcm16(
            byte[] pcmBytes,
            int sampleRate = RequiredSampleRate,
            int channels = RequiredChannels,
            int bitsPerSample = RequiredBitsPerSample)
        {
            EnsureMainThread();
            if (pcmBytes == null)
            {
                throw new ArgumentNullException(nameof(pcmBytes));
            }

            if (!CanAcceptNewRequest() ||
                !ValidatePcmFormat(sampleRate, channels, bitsPerSample))
            {
                return false;
            }

            if (!ValidatePcmBytes(pcmBytes.Length))
            {
                return false;
            }

            byte[] ownedPcmBytes = new byte[pcmBytes.Length];
            Buffer.BlockCopy(pcmBytes, 0, ownedPcmBytes, 0, pcmBytes.Length);

            return EnsureDriver().StartCompletePcm(
                ownedPcmBytes,
                XunfeiRecognitionInputKind.Pcm16Bytes,
                GetCredentialsSnapshot());
        }

        /// <summary>
        /// 异步分帧读取 AudioClip，降混并重采样为讯飞要求的 PCM16 后识别。
        /// bool 只表示请求已接受；转换或网络结果通过静态终态事件返回。
        /// </summary>
        public static bool RecognizeAudioClip(AudioClip clip, int sourceFramesPerUpdate = 8192)
        {
            EnsureMainThread();
            if (clip == null)
            {
                throw new ArgumentNullException(nameof(clip));
            }

            if (sourceFramesPerUpdate < 256 || sourceFramesPerUpdate > 16384)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sourceFramesPerUpdate),
                    "每帧读取量必须在 256 到 16384 个源音频帧之间。");
            }

            if (!CanAcceptNewRequest())
            {
                return false;
            }

            return EnsureDriver().StartAudioClip(
                clip,
                sourceFramesPerUpdate,
                GetCredentialsSnapshot());
        }

        /// <summary>开始接收外部实时 PCM16 数据。</summary>
        public static bool StartStreaming(
            int sampleRate = RequiredSampleRate,
            int channels = RequiredChannels,
            int bitsPerSample = RequiredBitsPerSample)
        {
            EnsureMainThread();
            if (!CanAcceptNewRequest() ||
                !ValidatePcmFormat(sampleRate, channels, bitsPerSample))
            {
                return false;
            }

            return EnsureDriver().StartStreaming(GetCredentialsSnapshot());
        }

        public static bool WritePcm16(byte[] pcmBytes, bool isLast = false)
        {
            if (pcmBytes == null)
            {
                throw new ArgumentNullException(nameof(pcmBytes));
            }

            return WritePcm16(pcmBytes, 0, pcmBytes.Length, isLast);
        }

        /// <summary>
        /// 向当前流式会话写入 PCM16。方法会在返回前复制指定数据，调用方随后可复用原数组。
        /// </summary>
        public static bool WritePcm16(
            byte[] pcmBytes,
            int offset,
            int count,
            bool isLast = false)
        {
            EnsureMainThread();
            if (pcmBytes == null)
            {
                throw new ArgumentNullException(nameof(pcmBytes));
            }

            if (offset < 0 || count < 0 || offset > pcmBytes.Length - count)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            return driver != null && driver.WriteStreamingPcm(pcmBytes, offset, count, isLast);
        }

        public static bool CompleteStreaming()
        {
            EnsureMainThread();
            return driver != null && driver.CompleteStreaming();
        }

        public static bool Cancel()
        {
            EnsureMainThread();
            return driver != null && driver.CancelRecognition();
        }

        private static bool CanAcceptNewRequest()
        {
            if (!IsAvailable)
            {
                LogRejected("当前平台没有可用的讯飞语音识别后端。");
                return false;
            }

            if (driver != null && driver.IsBusy)
            {
                Debug.LogWarning("[XunfeiSpeechRecognizer] 已有识别会话正在运行。");
                return false;
            }

            if (!HasRuntimeCredentials)
            {
                LogRejected("请先设置 APPID、APIKey 和 APISecret 运行时凭据。");
                return false;
            }

            return true;
        }

        private static bool ValidatePcmFormat(int sampleRate, int channels, int bitsPerSample)
        {
            if (sampleRate == RequiredSampleRate &&
                channels == RequiredChannels &&
                bitsPerSample == RequiredBitsPerSample)
            {
                return true;
            }

            LogRejected("PCM 输入必须是 16 kHz、16-bit、单声道 Little Endian。");
            return false;
        }

        private static bool ValidatePcmBytes(int byteCount)
        {
            if (byteCount <= 0)
            {
                LogRejected("PCM 输入不能为空。");
                return false;
            }

            if ((byteCount & 1) != 0)
            {
                LogRejected("PCM16 字节数必须是 2 的整数倍。");
                return false;
            }

            if (byteCount > MaximumPcmBytes)
            {
                LogRejected("单次语音听写最多支持 60 秒，输入不能超过 1920000 字节。");
                return false;
            }

            return true;
        }

        private static XunfeiRuntimeConfiguration GetCredentialsSnapshot()
        {
            return new XunfeiRuntimeConfiguration(
                appId,
                apiKey,
                apiSecret,
                language,
                accent);
        }

        private static XunfeiSpeechRecognitionDriver EnsureDriver()
        {
            if (driver != null)
            {
                return driver;
            }

            GameObject host = new GameObject("[Cowart Xunfei Speech Recognizer]")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            if (Application.isPlaying)
            {
                UnityEngine.Object.DontDestroyOnLoad(host);
            }

            driver = host.AddComponent<XunfeiSpeechRecognitionDriver>();
            driver.Initialize();
            return driver;
        }

        private static string RequireText(string value, string parameterName)
        {
            string trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                throw new ArgumentException("值不能为空。", parameterName);
            }

            return trimmed;
        }

        private static void EnsureMainThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != mainThreadId)
            {
                throw new InvalidOperationException("讯飞语音识别公开 API 只能在 Unity 主线程调用。");
            }
        }

        private static void LogRejected(string message)
        {
            Debug.LogError("[XunfeiSpeechRecognizer] 请求未接受：" + message);
        }

        internal static void NotifyDriverDestroyed(XunfeiSpeechRecognitionDriver target)
        {
            if (ReferenceEquals(driver, target))
            {
                driver = null;
            }
        }

        internal static void NotifyStarted(
            XunfeiSpeechRecognitionDriver source,
            int generation)
        {
            InvokeSafelyForSession(RecognitionStarted, source, generation);
        }

        internal static void NotifyStatus(string message)
        {
            InvokeSafely(StatusChanged, message ?? string.Empty);
        }

        internal static void NotifyStatus(
            XunfeiSpeechRecognitionDriver source,
            int generation,
            string message)
        {
            InvokeSafelyForSession(
                StatusChanged,
                message ?? string.Empty,
                source,
                generation);
        }

        internal static void NotifyPartialText(
            XunfeiSpeechRecognitionDriver source,
            int generation,
            string text)
        {
            InvokeSafelyForSession(
                PartialTextChanged,
                text ?? string.Empty,
                source,
                generation);
        }

        internal static void NotifyCompleted(XunfeiRecognitionResult result)
        {
            lastResult = result;
            InvokeSafely(RecognitionCompleted, result);
        }

        internal static void NotifyCanceled()
        {
            InvokeSafely(RecognitionCanceled);
        }

        internal static void NotifyFailed(XunfeiRecognitionError error)
        {
            InvokeSafely(RecognitionFailed, error);
            Debug.LogError("[XunfeiSpeechRecognizer] " + error.Message);
        }

        private static void InvokeSafely(Action callback)
        {
            if (callback == null)
            {
                return;
            }

            foreach (Action subscriber in callback.GetInvocationList())
            {
                try
                {
                    subscriber();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

        private static void InvokeSafely<T>(Action<T> callback, T value)
        {
            if (callback == null)
            {
                return;
            }

            foreach (Action<T> subscriber in callback.GetInvocationList())
            {
                try
                {
                    subscriber(value);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

        private static void InvokeSafelyForSession(
            Action callback,
            XunfeiSpeechRecognitionDriver source,
            int generation)
        {
            if (callback == null)
            {
                return;
            }

            foreach (Action subscriber in callback.GetInvocationList())
            {
                if (source == null || !source.IsSessionCurrent(generation))
                {
                    break;
                }

                try
                {
                    subscriber();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

        private static void InvokeSafelyForSession<T>(
            Action<T> callback,
            T value,
            XunfeiSpeechRecognitionDriver source,
            int generation)
        {
            if (callback == null)
            {
                return;
            }

            foreach (Action<T> subscriber in callback.GetInvocationList())
            {
                if (source == null || !source.IsSessionCurrent(generation))
                {
                    break;
                }

                try
                {
                    subscriber(value);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }
    }

    internal sealed class XunfeiRuntimeConfiguration
    {
        internal XunfeiRuntimeConfiguration(
            string appId,
            string apiKey,
            string apiSecret,
            string language,
            string accent)
        {
            AppId = appId;
            ApiKey = apiKey;
            ApiSecret = apiSecret;
            Language = language;
            Accent = accent;
        }

        internal string AppId { get; }
        internal string ApiKey { get; }
        internal string ApiSecret { get; }
        internal string Language { get; }
        internal string Accent { get; }
    }
}
