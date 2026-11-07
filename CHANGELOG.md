# Changelog

## [1.0.2] - 2026-11-08

- 统一 Cowart 包图标：重新设计为按功能类别配色的圆角图标（语音气泡里的声纹转文字），同步更新 `Icon.png` 与 `Icon@2x.png`。

## [1.0.1] - 2026-08-23

- 新增包专用图标，并将包内组件脚本的默认图标统一替换为该图标。

## [1.0.0] - 2026-08-15

- 增加唯一静态入口 `XunfeiSpeechRecognizer` 和统一成功、取消、失败出口。
- 增加完整 PCM16 WAV、完整裸 PCM16、固定 `AudioClip` 与外部实时 PCM16 四种输入。
- 移除 Runtime、程序集和 `package.json` 对 Audio Recorder 的硬依赖；两个包可单独安装，
  由项目业务层组合。
- `AudioClip` 在主线程协程中分帧读取、降混、重采样和编码，并限制单帧工作量。
- 完整 PCM 使用连续缓冲区和零额外复制的协议切片，避免集中创建大量小数组。
- 增加会话 generation、单终态、提前断线失败、连接/最终结果超时和有限发送追赶。
- 增加程序集独立性、唯一入口、PCM 队列和 AudioClip 编码 EditMode 测试。
- 增加可从 Package Manager 导入的独立 UGUI Sample，覆盖 WAV、裸 PCM16、AudioClip、
  实时 PCM16 和统一终态事件；Sample 不依赖其他 Cowart 包或 TextMeshPro。
- Sample 使用程序生成的合成音调，不再分发来源未记录的真人测试语音。
- 删除旧 `XunfeiSpeechRecognitionTest` 场景兼容层，并把底层 WebSocket 类型改为 internal；
  Runtime 导出的唯一控制入口是 `XunfeiSpeechRecognizer`。
- 默认不再把包含识别文字的服务端原始 JSON 写入 Console。

### Breaking changes

- 新代码命名空间改为 `Cowart.XunfeiSpeechRecognition`。
- 删除旧 `Cowart.WebGLBridge.Tests.XunfeiSpeech.XunfeiSpeechRecognitionTest` 公共类型。
- 讯飞包不再自动启动、停止或读取 Audio Recorder。
- 原先的“最近录音”和内嵌录音耦合入口不再属于正式 API；调用方应传入 WAV、PCM 或
  `AudioClip`，实时组合使用 `StartStreaming` / `WritePcm16` / `CompleteStreaming`。

## [0.1.0] - 2026-08-12

- 将科大讯飞 IAT 协议、WebSocket 适配器和录音转写控制器拆为独立本地包。
- 增加独立 EditMode 测试程序集和包内测试音频数据。
- 初始拆包版本曾保留原公开类型、命名空间和 Unity `.meta` GUID；这些临时兼容内容已在
  `1.0.0` 的正式公共契约中移除。
