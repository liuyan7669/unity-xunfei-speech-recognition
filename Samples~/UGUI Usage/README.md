# Xunfei Speech Recognition UGUI Usage

这是一个可通过 Unity Package Manager 导入的独立 UGUI 示例。它只依赖本讯飞包、Unity
UGUI、Unity Audio 和 Newtonsoft Json，不依赖 Audio Recorder、File Bridge、WebGLBridge
或 TextMeshPro。

## 导入与运行

1. 在 Package Manager 中选中 `Xunfei Speech Recognition`。
2. 展开 Samples，导入 `UGUI Usage`。
3. 打开导入目录中的 `Xunfei Speech Recognition UGUI.unity`。
4. 进入 Play Mode，在 APPID、APIKey、APISecret 输入框中填写运行时凭据。
5. 点击 `Apply Credentials`，再选择一种输入方式。

导入后的默认目录是：

`Assets/Samples/Xunfei Speech Recognition/1.0.0/UGUI Usage`

场景和资源中没有预置真实凭据。APISecret 输入框使用 Password 模式，示例不会把凭据写入
场景、资源或 PlayerPrefs。

## 示例覆盖的接口

- `Recognize complete WAV`：调用 `XunfeiSpeechRecognizer.RecognizeWav(byte[])`。
- `Recognize complete PCM16`：从 WAV 提取裸 PCM 后调用 `RecognizePcm16(byte[])`。
- `Recognize assigned AudioClip`：调用 `RecognizeAudioClip(AudioClip)`；可在示例组件中替换
  `audioClipSample`。
- `Stream PCM16 in 40 ms chunks`：依次调用 `StartStreaming()`、`WritePcm16(...)` 和
  `CompleteStreaming()`。
- `Complete stream input`：手动结束已经写入音频的流式输入。
- `Cancel recognition`：调用 `Cancel()`。
- 界面订阅 Started、状态、动态文字、成功、取消和失败静态事件；bool 只表示请求是否被接受，
  不代表识别已经成功。

## 内置音频说明

`SyntheticTone16kMonoPcm16.bytes` 和同内容的 `.wav` 是由程序生成的 2 秒合成音调：
16 kHz、16-bit、单声道 PCM。它不含真人语音、身份信息或第三方素材，只用于演示输入格式、
请求和流式投递链路，通常不会得到有意义的识别文字。

要验证真实转写结果，请替换为你有权使用的语音：

- 完整 WAV/PCM 和流式示例要求 16 kHz、16-bit、单声道 PCM WAV；
- `AudioClip` 入口可接收可读、已加载的常见采样率/声道音频，由包分帧转换；
- 单场讯飞 IAT 输入最多 60 秒。

示例使用 Unity 内置 legacy 字体以避免携带来源不明的字体文件。如果目标文字缺少中文等字形，
请在导入后的场景中把输出 `Text.font` 替换为项目已经获得授权、且覆盖目标字符集的字体。

## 重要限制

- 全局同时只允许一个识别会话。示例的 Complete、Cancel、禁用和销毁逻辑只处理由本示例成功
  发起的会话，不会终止业务代码在外部启动的会话。
- 约 5 秒连续静音可能触发服务端提前结束当前识别。
- 流式入口建立本地会话后会等待首块 PCM；连接打开后不要长时间停止供数。
- 当前 WebGL 鉴权需要客户端持有 APISecret，只适合本地或受控调试。生产 WebGL 应由可信
  后端生成短时鉴权 URL；不要把长期 APISecret 发布到浏览器资源中。
- Editor 和本地编译通过不等于真实账号、网络、Windows、Android 或浏览器环境已经验证。

录音包组合方式见包根 README。组合代码应位于业务层：录音包输出完整 WAV 或实时 PCM，
再由业务代码传给这里的静态入口；两个 UPM 包不互相形成编译依赖。
