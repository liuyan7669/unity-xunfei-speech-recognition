using System;
using UnityEngine;

namespace Cowart.XunfeiSpeechRecognition
{
    /// <summary>
    /// 将按源帧顺序提供的 float 音频块降混、线性重采样并编码为 16 kHz mono PCM16。
    /// 相邻块必须保留一个源帧重叠，确保跨块插值连续。
    /// </summary>
    internal sealed class XunfeiPcm16AudioEncoder
    {
        private readonly int sourceFrameCount;
        private readonly int sourceSampleRate;
        private readonly int channels;
        private readonly double sourceFramesPerOutputFrame;
        private readonly byte[] pcmBytes;
        private int outputFrame;

        internal XunfeiPcm16AudioEncoder(
            int sourceFrameCount,
            int sourceSampleRate,
            int channels)
        {
            if (sourceFrameCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceFrameCount));
            }

            if (sourceSampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceSampleRate));
            }

            if (channels <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(channels));
            }

            this.sourceFrameCount = sourceFrameCount;
            this.sourceSampleRate = sourceSampleRate;
            this.channels = channels;
            OutputFrameCount = Math.Max(
                1,
                (int)Math.Round(
                    sourceFrameCount * (double)XunfeiSpeechRecognizer.RequiredSampleRate /
                    sourceSampleRate));
            pcmBytes = new byte[checked(OutputFrameCount * sizeof(short))];
            sourceFramesPerOutputFrame =
                sourceSampleRate / (double)XunfeiSpeechRecognizer.RequiredSampleRate;
        }

        internal int OutputFrameCount { get; }
        internal int OutputByteCount => pcmBytes.Length;
        internal int WrittenOutputFrames => outputFrame;
        internal bool IsComplete => outputFrame == OutputFrameCount;

        internal bool TryWriteChunk(
            float[] interleaved,
            int chunkStartFrame,
            int chunkFrameCount,
            out string error)
        {
            return TryWriteChunk(
                interleaved,
                chunkStartFrame,
                chunkFrameCount,
                int.MaxValue,
                out _,
                out error);
        }

        internal bool TryWriteChunk(
            float[] interleaved,
            int chunkStartFrame,
            int chunkFrameCount,
            int maximumOutputFrames,
            out bool chunkNeedsMoreProcessing,
            out string error)
        {
            chunkNeedsMoreProcessing = false;
            error = string.Empty;
            if (interleaved == null)
            {
                error = "AudioClip 样本块不能为空。";
                return false;
            }

            if (chunkStartFrame < 0 || chunkFrameCount <= 0 ||
                chunkStartFrame > sourceFrameCount - chunkFrameCount)
            {
                error = "AudioClip 样本块范围无效。";
                return false;
            }

            if ((long)chunkFrameCount * channels > interleaved.Length)
            {
                error = "AudioClip 样本块短于声明的帧数。";
                return false;
            }

            if (maximumOutputFrames <= 0)
            {
                error = "每帧 PCM16 编码预算必须大于 0。";
                return false;
            }

            int chunkEndFrame = chunkStartFrame + chunkFrameCount - 1;
            int outputFramesWrittenThisCall = 0;
            while (outputFrame < OutputFrameCount &&
                   outputFramesWrittenThisCall < maximumOutputFrames)
            {
                double sourcePosition = outputFrame * sourceFramesPerOutputFrame;
                int firstFrame = Math.Min((int)sourcePosition, sourceFrameCount - 1);
                int secondFrame = Math.Min(firstFrame + 1, sourceFrameCount - 1);
                if (firstFrame < chunkStartFrame)
                {
                    error = "AudioClip 分帧数据存在缺口或块顺序错误。";
                    return false;
                }

                if (secondFrame > chunkEndFrame)
                {
                    break;
                }

                float firstSample = ReadMonoFrame(
                    interleaved,
                    firstFrame - chunkStartFrame,
                    channels);
                float secondSample = ReadMonoFrame(
                    interleaved,
                    secondFrame - chunkStartFrame,
                    channels);
                float interpolation = (float)(sourcePosition - firstFrame);
                float sample = Mathf.Clamp(
                    firstSample + ((secondSample - firstSample) * interpolation),
                    -1f,
                    1f);
                short pcmValue = sample < 0f
                    ? (short)Math.Round(sample * 32768f)
                    : (short)Math.Round(sample * 32767f);
                int destination = outputFrame * sizeof(short);
                pcmBytes[destination] = (byte)pcmValue;
                pcmBytes[destination + 1] = (byte)(pcmValue >> 8);
                outputFrame++;
                outputFramesWrittenThisCall++;
            }

            if (outputFrame < OutputFrameCount)
            {
                double nextSourcePosition = outputFrame * sourceFramesPerOutputFrame;
                int nextFirstFrame = Math.Min((int)nextSourcePosition, sourceFrameCount - 1);
                int nextSecondFrame = Math.Min(nextFirstFrame + 1, sourceFrameCount - 1);
                chunkNeedsMoreProcessing = nextSecondFrame <= chunkEndFrame;
            }

            return true;
        }

        internal byte[] GetCompletedPcm()
        {
            if (!IsComplete)
            {
                throw new InvalidOperationException("AudioClip PCM16 编码尚未完成。");
            }

            return pcmBytes;
        }

        private static float ReadMonoFrame(float[] interleaved, int frame, int channels)
        {
            int offset = frame * channels;
            double total = 0d;
            for (int channel = 0; channel < channels; channel++)
            {
                total += interleaved[offset + channel];
            }

            return (float)(total / channels);
        }
    }
}
