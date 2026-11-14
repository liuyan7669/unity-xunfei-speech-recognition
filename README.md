# Xunfei Speech Recognition

科大讯飞语音听写（IAT）Unity UPM 包。唯一业务入口是：

```csharp
Cowart.XunfeiSpeechRecognition.XunfeiSpeechRecognizer
```

本包只负责“接收音频并输出文字”，不会打开麦克风，也不依赖 Audio Recorder、
Device Files 或项目中的 WebGLBridge。它可以单独安装，也可以由项目业务层与任意录音包组合。

## 功能一览

| 能力 | 入口 | 输入 |
|---|---|---|
| 完整 WAV 转文字 | `RecognizeWav(...)` | PCM16 WAV `byte[]` |
| 完整裸 PCM 转文字 | `RecognizePcm16(...)` | 16 kHz / 16-bit / mono PCM `byte[]` |
| 固定 AudioClip 转文字 | `RecognizeAudioClip(...)` | 任意常见采样率、声道的可读 `AudioClip` |
| 外部实时音频转文字 | `StartStreaming` → `WritePcm16` → `CompleteStreaming` | 连续 PCM16 `byte[]` |
| 动态文字 | `PartialTextChanged` | 每次回调当前完整识别文本 |
| 最终结果 | `RecognitionCompleted` | `XunfeiRecognitionResult` |
| 取消和失败 | `RecognitionCanceled`、`RecognitionFailed` | 单一终态事件 |

本包不提供：

- 麦克风录制、录音权限申请或录音 UI。
- WAV 文件选择、下载或保存。
- 超过 60 秒的录音文件转写服务。
- 通用的多厂商语音识别抽象层。

## 支持范围与依赖

- Unity：`2021.3` 或更高的兼容版本。
- 当前编译后端：Unity Editor、Windows、Android、WebGL。
- 包依赖：`com.unity.nuget.newtonsoft-json`、`com.unity.modules.audio`、`com.unity.ugui`。
  UGUI 只用于可导入 Sample；核心 Runtime 不引用 UI 类型。
- 不依赖 Audio Recorder、Device Files、WebGLBridge 或任何其他 Cowart 包。

`IsAvailable == true` 只表示当前目标平台有已编译的传输后端，不表示凭据正确、网络可达、
设备时间准确或讯飞服务一定可用。

## 安装

### 通过 OpenUPM 安装

本包首次 Git 发布完成后，需要先在 OpenUPM 完成登记。登记并同步成功后，在 Unity 的
`Edit > Project Settings > Package Manager` 中添加：

```text
Name: package.openupm.com
URL: https://package.openupm.com
Scope: com.cowart.xunfei-speech-recognition
```

然后通过 `Add package by name` 安装：

```text
com.cowart.xunfei-speech-recognition
```

### 通过 Git URL 安装

```text
https://github.com/liuyan7669/unity-xunfei-speech-recognition.git#3.1.0
```

### 从本地磁盘安装

在 Unity Package Manager 中选择 **Add package from disk...**，然后选择：

```text
Packages\com.cowart.xunfei-speech-recognition\package.json
```

也可以把包目录作为 Embedded Package 放进目标项目的 `Packages` 目录。只安装本包即可使用
WAV、PCM 和 AudioClip 识别；只有需要麦克风录音时，目标项目才另外安装 Audio Recorder。

### 导入独立 UGUI Sample

在 Package Manager 中选中 `Xunfei Speech Recognition`，展开 Samples，点击
`UGUI Usage` 右侧的 **Import**。默认导入目录是：

```text
Assets\Samples\Xunfei Speech Recognition\3.1.0\UGUI Usage
```

打开 `Xunfei Speech Recognition UGUI.unity` 即可看到完整 WAV、裸 PCM16、AudioClip、
实时 PCM16、取消和统一终态事件的独立示例。Sample 只使用 legacy UGUI 和 Unity 内置字体，
不依赖 TextMeshPro、Audio Recorder、Device Files 或项目 Assets 中的资源。

Sample 不保存真实凭据。内置音频是程序生成的 2 秒合成音调，不含真人语音或第三方素材，
只用于验证格式与投递链路；要得到有意义的文字，请换成调用方有权使用的真实语音。
Sample 使用 Unity 内置 legacy 字体；若中文等目标字符显示缺字，请在导入后的场景中换成项目
已获得授权并覆盖对应字符集的字体。

## 30 秒开始使用

先在主线程订阅出口，再设置运行时凭据，最后提交一种音频输入：

```csharp
using Cowart.XunfeiSpeechRecognition;
using UnityEngine;

public sealed class SpeechRecognitionExample : MonoBehaviour
{
    private void OnEnable()
    {
        XunfeiSpeechRecognizer.PartialTextChanged += HandlePartial;
        XunfeiSpeechRecognizer.RecognitionCompleted += HandleCompleted;
        XunfeiSpeechRecognizer.RecognitionCanceled += HandleCanceled;
        XunfeiSpeechRecognizer.RecognitionFailed += HandleFailed;
    }

    private void OnDisable()
    {
        XunfeiSpeechRecognizer.PartialTextChanged -= HandlePartial;
        XunfeiSpeechRecognizer.RecognitionCompleted -= HandleCompleted;
        XunfeiSpeechRecognizer.RecognitionCanceled -= HandleCanceled;
        XunfeiSpeechRecognizer.RecognitionFailed -= HandleFailed;
    }

    public void Configure(string appId, string apiKey, string apiSecret)
    {
        XunfeiSpeechRecognizer.SetRuntimeCredentials(appId, apiKey, apiSecret);
    }

    public bool RecognizeWav(byte[] pcm16WavBytes)
    {
        return XunfeiSpeechRecognizer.RecognizeWav(pcm16WavBytes);
    }

    private static void HandlePartial(string currentFullText)
    {
        Debug.Log("动态文字：" + currentFullText);
    }

    private static void HandleCompleted(XunfeiRecognitionResult result)
    {
        Debug.Log("最终文字：" + result.Text + "，SID：" + result.ServiceSessionId);
    }

    private static void HandleCanceled()
    {
        Debug.Log("识别已取消。");
    }

    private static void HandleFailed(XunfeiRecognitionError error)
    {
        Debug.LogError($"识别失败：{error.ServiceCode} / {error.Message}");
    }
}
```

`RecognizeWav` 返回 `true` 只表示请求已被本包接受。最终一定要以
`RecognitionCompleted`、`RecognitionCanceled`、`RecognitionFailed` 三个终态出口为准。

## 凭据与安全

需要在讯飞控制台为应用启用“语音听写（流式版）”，并提供：

- `APPID`
- `APIKey`
- `APISecret`

```csharp
XunfeiSpeechRecognizer.SetRuntimeCredentials(
    appId,
    apiKey,
    apiSecret,
    newLanguage: "zh_cn",
    newAccent: "mandarin");
```

凭据只保存在当前 Unity 进程的静态内存中，进入新的 Player 会话时会清空。不要把
`APISecret` 写进源码、场景、Prefab、ScriptableObject、Resources、StreamingAssets 或 Git。

```csharp
XunfeiSpeechRecognizer.ClearRuntimeCredentials();
```

WebGL 客户端中的任何长期密钥都能被浏览器使用者观察到。当前版本仍由客户端凭据生成鉴权
URL，因此不能把长期 `APISecret` 随生产 WebGL 构建发布。生产环境应由业务后端保存密钥并
提供短时鉴权能力；在增加后端鉴权 URL 接入并通过真实部署验收前，只应把当前 WebGL 凭据
方式用于本地或受控调试。

鉴权依赖 UTC 时间。设备时间偏差过大时，连接可能被服务端拒绝。

## 唯一入口与公共状态

新代码只调用 `XunfeiSpeechRecognizer`：

| 成员 | 含义 |
|---|---|
| `IsAvailable` | 当前编译目标是否有后端 |
| `HasRuntimeCredentials` | 当前进程内是否已设置三项凭据 |
| `State` | 当前全局识别状态 |
| `IsRecognizing` | 是否存在进行中的识别会话 |
| `IsStreaming` | 当前会话是否使用外部 PCM 流输入 |
| `CurrentText` | 当前会话已经组装出的完整动态文字 |
| `LastResult` | 最近一次成功结果；失败或取消不会自动清掉旧结果 |

状态枚举：

```text
Idle
WaitingForAudio       // 实时会话已保留，尚未收到第一块非空 PCM
PreparingAudio       // 正在分帧读取和转换 AudioClip
Connecting           // 正在连接 WebSocket
SendingAudio         // 正在按协议节奏发送音频
AwaitingFinalResult  // 音频已结束，等待服务端最终文字
```

全局同一时间只允许一场识别。已有会话时，新入口返回 `false`，不会创建第二个会话。
隐藏 Driver 使用 `HideAndDontSave + DontDestroyOnLoad`，因此识别可跨场景继续。

## 出口事件

```csharp
public static event Action RecognitionStarted;
public static event Action<string> StatusChanged;
public static event Action<string> PartialTextChanged;
public static event Action<XunfeiRecognitionResult> RecognitionCompleted;
public static event Action RecognitionCanceled;
public static event Action<XunfeiRecognitionError> RecognitionFailed;
```

- `RecognitionStarted`：请求已进入本包状态机，不表示 WebSocket 已连接。
- `StatusChanged`：只用于展示进度，不是成功或失败依据。
- `PartialTextChanged`：参数是当前完整文本，不是只包含新增字符的 token。
- `RecognitionCompleted`：服务端最终结果已经收到，会话已先回到 `Idle`。
- `RecognitionCanceled`：调用 `Cancel()` 后的取消终态。
- `RecognitionFailed`：本地校验、AudioClip 转换、超时、传输或服务端错误。

一个已接受的会话最多发布一个终态。终态事件触发前内部会话已经清理，因此可以在终态回调中
立即开始下一场识别。各订阅者异常会被隔离，不会阻止其他订阅者收到事件。

所有公共方法必须在 Unity 主线程调用；否则抛出 `InvalidOperationException`。所有公开事件也在
Unity 主线程交付。应在 `OnEnable` / `OnDisable` 中对称订阅和退订。

### 返回值与同步失败

- `true`：请求或本次写入已接受，不表示最终识别成功。
- `false`：当前平台不可用、缺少凭据、已有会话、格式错误、流已结束或请求被同步拒绝。
- `null`、越界范围、非法 `sourceFramesPerUpdate` 等编程错误会抛参数异常。
- 新识别请求返回 `false` 时没有建立会话，因此不会发布
  `RecognitionCompleted / RecognitionCanceled / RecognitionFailed` 终态；详细原因写入 Unity Console。
- 请求返回 `true` 后，转换、超时、传输或服务端错误才通过 `RecognitionFailed` 结束该会话。
- 已经开始的流式会话中，非法写入可能同步失败并在方法返回 `false` 前发布 `RecognitionFailed`。

不要在入口返回 `false` 后无条件覆盖事件回调刚写入的详细错误 UI。

## 输入一：完整 PCM16 WAV byte[]

```csharp
bool accepted = XunfeiSpeechRecognizer.RecognizeWav(wavBytes);
```

要求：

- RIFF/WAVE。
- PCM 编码，不支持 MP3、AAC、Ogg 或浮点 WAV。
- 16 kHz、16-bit、单声道、Little Endian。
- 音频数据大于 0 且不超过 60 秒。

本包会解析 WAV 的 `fmt ` 与 `data` 块，提取一份独立 PCM 数据。入口返回后，调用方可以复用
或释放原始 WAV 数组。Audio Recorder 的 `RecordedAudio.Data` 正好是该格式，可直接传入。

## 输入二：完整裸 PCM16 byte[]

```csharp
bool accepted = XunfeiSpeechRecognizer.RecognizePcm16(
    pcmBytes,
    sampleRate: 16000,
    channels: 1,
    bitsPerSample: 16);
```

裸 PCM 没有 WAV 文件头。本入口只接受讯飞要求的 16 kHz、16-bit、单声道 Little Endian；
不会自动转换其他裸 PCM 格式。方法会在返回前复制最多 1,920,000 字节，调用方随后可以修改
或复用原数组。

## 输入三：固定 AudioClip

```csharp
[SerializeField] private AudioClip clip;

public void RecognizeClip()
{
    bool accepted = XunfeiSpeechRecognizer.RecognizeAudioClip(
        clip,
        sourceFramesPerUpdate: 8192);
}
```

`AudioClip` 可以来自项目资源、AssetBundle 或调用方自己的代码，不要求由 Audio Recorder 生成。
本包会：

1. 在入口后的下一 Unity 帧才开始转换。
2. 分帧调用 `AudioClip.GetData`。
3. 多声道平均降混为 mono。
4. 线性重采样为 16 kHz。
5. 编码为 PCM16，再走同一识别状态机。

`sourceFramesPerUpdate` 可设为 `256..16384`，默认 `8192`。内部还会按声道数和输出帧数执行
更严格的单帧预算，并复用工作缓冲，避免在入口当帧一次性读取整段 Clip。最终 PCM 使用一块最多
约 1.92 MB 的连续内存，发送时只切片引用，不预先复制成上千个协议帧。

注意：

- Clip 最长 60 秒。
- Clip 必须已加载且允许 `GetData`；导入资源建议使用 `Decompress On Load`。
- 转换在 Unity 主线程协程中分帧进行，不等于后台线程解码。真实设备仍需用目标 Clip、声道数和
  机型做 Profiler 验收。
- 当前使用线性重采样，没有抗混叠低通；高频素材的质量应真实试听和识别验证。
- 转换完成、网络完成或失败仍由静态终态事件报告。

## 输入四：外部实时 PCM16 流

### 调用顺序

```csharp
if (!XunfeiSpeechRecognizer.StartStreaming(16000, 1, 16))
{
    return;
}

// 可重复调用。每块大小不必正好是 1280 字节，但必须包含完整 PCM16 样本。
XunfeiSpeechRecognizer.WritePcm16(chunk1);
XunfeiSpeechRecognizer.WritePcm16(chunk2, offset, count);

// 两种结束方式二选一：
XunfeiSpeechRecognizer.WritePcm16(lastChunk, isLast: true);
// 或最后一块已写完后：
XunfeiSpeechRecognizer.CompleteStreaming();
```

`StartStreaming` 只保留一场输入会话，第一块非空 PCM 到达时才连接讯飞。这样调用方可以先建立
组合状态，再等待浏览器/Android 麦克风授权，不会因为提前建立 WebSocket 后长期无音频而被服务
端主动断开。

流输入契约：

- 格式固定为 16 kHz、16-bit、单声道 Little Endian。
- 每次 `count` 必须是 2 的整数倍，不能把一个 PCM16 样本的两个字节拆到两次调用。
- 包会在 `WritePcm16` 返回前复制本次范围，调用方可立即复用源数组。
- 任意偶数字节块会在内部重组为讯飞协议需要的 1280 字节 / 40 ms 帧。
- 空流不能结束；没有写入音频就调用 `CompleteStreaming` 会失败。
- 累计达到 1,920,000 字节时，讯飞输入自动结束，不会停止外部录音器。
- 超过上限的整次写入会被拒绝并进入失败终态，不会静默截断。

`CompleteStreaming()` 只表示“不再有新的音频输入”，不会立刻调用 `RecognitionCompleted`。
本包仍要发送剩余帧并等待讯飞返回最终文字；成功时才触发 `RecognitionCompleted`。

如果要放弃当前会话：

```csharp
bool canceled = XunfeiSpeechRecognizer.Cancel();
```

`Cancel()` 不会停止调用方的录音器，也不会销毁调用方的音频数据。

## 与 Audio Recorder 组合

组合层属于业务项目，不放进 Audio Recorder 或本包 Runtime。依赖方向应是：

```text
业务代码
├─ 调用 Cowart.AudioRecorder
└─ 调用 Cowart.XunfeiSpeechRecognition

Audio Recorder ─X─> Xunfei 包
Xunfei 包       ─X─> Audio Recorder
```

这样两个包都能单独安装、单独测试，也能在同一项目中组合。

### 方案 A：录完完整 WAV 后一次性识别

```csharp
using Cowart.AudioRecorder;
using Cowart.XunfeiSpeechRecognition;
using UnityEngine;

public sealed class CompleteRecordingToText : MonoBehaviour
{
    private void OnEnable()
    {
        AudioRecorder.RecordingCompleted += HandleRecordingCompleted;
        AudioRecorder.RecordingCanceled += HandleRecordingCanceled;
        AudioRecorder.RecordingFailed += HandleRecordingFailed;
    }

    private void OnDisable()
    {
        AudioRecorder.RecordingCompleted -= HandleRecordingCompleted;
        AudioRecorder.RecordingCanceled -= HandleRecordingCanceled;
        AudioRecorder.RecordingFailed -= HandleRecordingFailed;
    }

    // WebGL 中应由真实按钮点击直接调用。
    public bool StartRecordingForRecognition()
    {
        // Audio Recorder 默认 300 秒、可设置 5..3600 秒；本次 IAT 必须限制在 60 秒以内。
        return AudioRecorder.StartRecording(maxDurationSeconds: 60);
    }

    private static void HandleRecordingCompleted(RecordedAudio recording)
    {
        XunfeiSpeechRecognizer.RecognizeWav(recording.Data);
    }

    private static void HandleRecordingCanceled()
    {
        Debug.Log("录音取消，没有提交识别。");
    }

    private static void HandleRecordingFailed(string message)
    {
        Debug.LogError("录音失败：" + message);
    }
}
```

该方案简单、可保留完整 WAV，但必须等录音结束后才开始识别。

### 方案 B：录音时实时推送 PCM

```csharp
using Cowart.AudioRecorder;
using Cowart.XunfeiSpeechRecognition;
using UnityEngine;

public sealed class StreamingRecordingToText : MonoBehaviour
{
    private void OnEnable()
    {
        AudioRecorder.AudioChunkReceived += HandleAudioChunk;
        AudioRecorder.RecordingCanceled += HandleRecorderCanceled;
        AudioRecorder.RecordingFailed += HandleRecorderFailed;
    }

    private void OnDisable()
    {
        AudioRecorder.AudioChunkReceived -= HandleAudioChunk;
        AudioRecorder.RecordingCanceled -= HandleRecorderCanceled;
        AudioRecorder.RecordingFailed -= HandleRecorderFailed;
    }

    // WebGL 中应由真实按钮点击直接调用。
    public bool StartCombinedStreaming()
    {
        if (!XunfeiSpeechRecognizer.StartStreaming(16000, 1, 16))
        {
            return false;
        }

        bool recorderAccepted = AudioRecorder.StartRecording(
            maxDurationSeconds: 60,
            streamAudio: true,
            streamChunkDurationMilliseconds: 40);
        if (!recorderAccepted)
        {
            XunfeiSpeechRecognizer.Cancel();
            return false;
        }

        return true;
    }

    public void StopCombinedStreaming()
    {
        // 录音器停止后会冲刷最后一个 AudioStreamChunk；由该块结束讯飞输入。
        AudioRecorder.StopRecording();
    }

    private static void HandleAudioChunk(AudioStreamChunk chunk)
    {
        if (chunk.Data.Length > 0)
        {
            XunfeiSpeechRecognizer.WritePcm16(
                chunk.Data,
                0,
                chunk.Data.Length,
                isLast: chunk.IsLast);
        }
        else if (chunk.IsLast)
        {
            // Audio Recorder 的最终结束块允许为空。
            XunfeiSpeechRecognizer.CompleteStreaming();
        }
    }

    private static void HandleRecorderCanceled()
    {
        XunfeiSpeechRecognizer.Cancel();
    }

    private static void HandleRecorderFailed(string _)
    {
        XunfeiSpeechRecognizer.Cancel();
    }
}
```

Audio Recorder 的流输出已经是 16 kHz、16-bit、单声道 PCM16，可以直接转发。开始顺序必须是：

```text
先订阅两个包的事件
→ StartStreaming()
→ StartRecording(streamAudio: true)
→ 持续 WritePcm16(chunk.Data)
→ 最终 chunk 或 CompleteStreaming()
→ 等待 RecognitionCompleted / Canceled / Failed
```

Audio Recorder 默认上限是 300 秒，可设置为 5..3600 秒；但当前讯飞 IAT 单场仍最多 60 秒。
组合实时识别时应把本场录音上限设为 60 秒。若业务需要继续录制 30 或 60 分钟，讯飞输入达到
60 秒后会自动结束，录音器仍可继续；业务层必须自行决定是否按不重叠的 60 秒会话切分、停止
录音，或改用讯飞长音频转写服务。不能把一小时完整 WAV 一次提交给本包。

## 结果对象

`XunfeiRecognitionResult` 是只读快照：

| 属性 | 含义 |
|---|---|
| `Text` | 最终完整文字 |
| `ServiceSessionId` | 讯飞返回的 SID，排查服务问题时应记录 |
| `InputKind` | WAV、裸 PCM、AudioClip 或 PCM 流 |
| `PcmByteCount` | 实际提交的 PCM 字节数 |
| `AudioDurationSeconds` | 按 32000 字节/秒计算的音频时长 |

`XunfeiRecognitionError`：

| 属性 | 含义 |
|---|---|
| `ServiceCode` | `-1` 为本地校验、转换、超时或传输错误；其他值为服务端错误码 |
| `Message` | 可展示和记录的错误信息 |
| `ServiceSessionId` | 服务端已返回时携带 SID |

## 时长、格式和发送节奏

- 单次语音听写最多 60 秒。
- 目标 PCM：16000 Hz × mono × 16-bit = 32000 字节/秒。
- 最大 PCM：`1,920,000` 字节。
- 协议帧：1280 字节，即 40 ms。
- 完整音频按逻辑 40 ms 时间轴发送，每个 Unity Update 最多发送一帧；不会在同一帧突发发送
  多个音频 JSON。正常 30/60 FPS 下会用帧间隔量化维持接近实时的平均节奏；长卡顿超过一个
  协议周期时重新锚定，避免用过短间隔持续追赶。
- 音频发完后等待最终结果最多 30 秒；连接等待最多 20 秒。
- 首帧协议固定使用 `eos=5000`，约 5 秒连续静音可能触发服务端提前结束本场输入并返回结果。
- `StartStreaming()` 本身只建立本地等待状态，首块非空 PCM 到来才连接；连接打开后应持续供数，
  不要让第三方音源在连接状态下长时间停顿。服务端可能在约 10 秒无数据后主动断开。

超过 60 秒的音频应使用讯飞的录音文件转写等另一类服务；那不是本包当前 IAT WebSocket
协议的一部分。

## 数据所有权与性能

- `RecognizeWav`：解析并创建独立 PCM 数组，调用方可复用原 WAV。
- `RecognizePcm16`：入口同步复制完整 PCM，最多约 1.92 MB。
- `WritePcm16`：入口同步复制本次写入范围，适合 20～100 ms 等小块持续投递。
- `RecognizeAudioClip`：主线程协程分帧转换；不会在入口调用当帧读取整段 Clip。
- 完整 PCM 保留为一个连续数组，协议发送使用 `array + offset + count` 切片，不会一次创建
  约 1500 个小数组。
- WebSocket 收发和事件最终回到 Unity 主线程。
- 包默认不把服务端原始 JSON 响应写入 Console，避免开发日志无意保存完整识别文字；业务若记录
  `PartialTextChanged`、最终文字或 SID，应按用户语音数据的隐私级别管理日志。

“分帧”用于降低单帧工作量，不等于任何设备上绝对零卡顿。60 秒、多声道、高采样率 Clip 以及
WebGL/低端 Android 必须用 Unity Profiler 和真实目标设备测量 GC Alloc、主线程峰值与网络稳定性。

## 平台说明

### Unity Editor / Windows

使用 .NET `ClientWebSocket`。Editor 测试可验证协议和状态逻辑，但不能代替真实讯飞服务、
Windows 防火墙、代理、TLS 与设备时间验证。

### Android

本包不访问麦克风，因此不请求 `RECORD_AUDIO`。若与 Audio Recorder 组合，录音权限由录音包
负责。仍需在真实 Android 设备验证网络、TLS、应用切后台和机型性能。

### WebGL

使用包内 `.jslib` 和浏览器 WebSocket。Editor Play Mode 不会执行 WebGL `.jslib`，必须在真实
浏览器构建中验证连接、页面协议、浏览器控制台和生产部署。WebGL 的长期凭据安全限制见“凭据
与安全”；未完成后端鉴权接入前，不应将客户端 `APISecret` 方案视为生产可用。

## 测试

包内 EditMode 测试位于 `Tests/Editor`，覆盖协议、WAV 解析、程序集独立性、唯一静态入口、
完整公开类型白名单、PCM 队列边界、AudioClip 分块重采样、5 秒静音端点配置和只读结果契约。

要在当前项目中运行包测试，将包名加入 `Packages\manifest.json` 的 `testables`：

```json
"testables": [
  "com.cowart.xunfei-speech-recognition"
]
```

然后在 Unity Test Runner 中运行：

```text
Cowart.XunfeiSpeechRecognition.Tests.Editor
```

### 2026-08-15 本地验证记录

- Unity `2021.3.30f1`：讯飞包 EditMode `26/26` 通过；当前工程全部 EditMode `144/144` 通过。
- 空白工程仅直接安装本包，Runtime 程序集引用只有 `netstandard`、`Newtonsoft.Json`、
  `UnityEngine.AudioModule`、`UnityEngine.CoreModule`；其他 Cowart 程序集引用为 `0`。
- Package Manager 找到 `1` 个 Sample 并实际导入；导入场景有 `3` 个凭据 InputField、`9` 个
  功能按钮、`1` 个 Canvas、`1` 个 EventSystem，Missing Script 和禁止依赖均为 `0`。
- 使用导入后的 Sample 场景完成 Development WebGL Player 构建：`Succeeded`、`0 error`；
  包内四个 WebGL 传输符号均进入 framework。唯一构建 warning 来自 `-nographics` 下无法更新
  环境/反射探针，不是包源码 warning。

这些是源码、空白安装、Sample 导入和 Player 构建证据，不代表已连接真实讯飞账号，也不代表
Windows、Android 或浏览器中的真实网络识别已经通过。

这些测试不连接真实讯飞账号。发布前仍需分别完成：

1. 只安装本包的空白项目编译。
2. 在空白项目通过 Package Manager 导入 UGUI Sample，检查 0 Missing Script 并构建 Player。
3. 只安装 Audio Recorder 的空白项目编译。
4. 同时安装两包的完整 WAV 与实时 PCM 组合验证。
5. Windows、Android、WebGL 的真实账号和真实网络验证。
6. WebGL 后端鉴权与密钥不落客户端验证。

## 版本迁移

`1.0.0` 起，正式且唯一的业务入口是
`Cowart.XunfeiSpeechRecognition.XunfeiSpeechRecognizer`。旧 WebGLBridge 测试组件和公开
WebSocket 类型不再属于公共 API，Audio Recorder 硬依赖也已移除。组合代码必须放在业务项目
中，显式把 WAV 或 PCM 数据从生产者传给本包。

## Inspector 统一依赖

本包声明依赖 `com.cowart.inspector-groups` `3.1.0`，Inspector 样式统一由该基础包提供。安装前须配置 OpenUPM scoped registry（`https://package.openupm.com`，scope `com.cowart`）；UPM 会根据 dependencies 连带解析 Inspector Groups。使用未发布源码时，请同时使用本地 Inspector Groups 3.1.0。业务包之间不互相依赖，Samples 的可选组合不受此限制。
