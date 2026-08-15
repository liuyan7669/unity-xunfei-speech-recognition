using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Cowart.XunfeiSpeechRecognition.Tests
{
    public sealed class XunfeiSpeechRecognizerTests
    {
        [Test]
        public void RuntimeAssembly_DoesNotReferenceOtherCowartPackages()
        {
            string[] otherCowartReferences = typeof(XunfeiSpeechRecognizer)
                .Assembly
                .GetReferencedAssemblies()
                .Select(name => name.Name)
                .Where(name => name.StartsWith("Cowart.", StringComparison.Ordinal))
                .ToArray();

            CollectionAssert.IsEmpty(otherCowartReferences);
        }

        [Test]
        public void RuntimeAssembly_HasSinglePublicStaticControlFacade()
        {
            Type[] publicStaticTypes = typeof(XunfeiSpeechRecognizer)
                .Assembly
                .GetExportedTypes()
                .Where(type => type.IsAbstract && type.IsSealed)
                .ToArray();

            CollectionAssert.AreEquivalent(
                new[] { typeof(XunfeiSpeechRecognizer) },
                publicStaticTypes);
        }

        [Test]
        public void RuntimeAssembly_ExportsOnlyDocumentedApiTypes()
        {
            Type[] exportedTypes = typeof(XunfeiSpeechRecognizer)
                .Assembly
                .GetExportedTypes();

            CollectionAssert.AreEquivalent(
                new[]
                {
                    typeof(XunfeiRecognitionState),
                    typeof(XunfeiRecognitionInputKind),
                    typeof(XunfeiRecognitionResult),
                    typeof(XunfeiRecognitionError),
                    typeof(XunfeiSpeechRecognizer)
                },
                exportedTypes);
            Assert.IsFalse(exportedTypes.Any(type => typeof(MonoBehaviour).IsAssignableFrom(type)));
        }

        [Test]
        public void ResultDto_ExposesStableReadOnlyMetadata()
        {
            XunfeiRecognitionResult result = new XunfeiRecognitionResult(
                "测试",
                "sid-1",
                XunfeiRecognitionInputKind.Pcm16Wav,
                32000);

            Assert.AreEqual("测试", result.Text);
            Assert.AreEqual("sid-1", result.ServiceSessionId);
            Assert.AreEqual(XunfeiRecognitionInputKind.Pcm16Wav, result.InputKind);
            Assert.AreEqual(32000, result.PcmByteCount);
            Assert.AreEqual(1d, result.AudioDurationSeconds, 0.000001d);
            Assert.IsFalse(typeof(XunfeiRecognitionResult).GetProperties()
                .Any(property => property.SetMethod != null && property.SetMethod.IsPublic));
        }

        [Test]
        public void FrameQueue_ReframesArbitraryWritesAndMarksOnlyFinalTail()
        {
            Pcm16FrameQueue queue = new Pcm16FrameQueue(4, 12);
            byte[] first = { 1, 2 };
            byte[] second = { 3, 4, 5, 6 };

            Assert.IsTrue(queue.TryAppend(first, 0, first.Length, false, out _));
            Assert.IsTrue(queue.TryAppend(second, 0, second.Length, true, out _));
            Array.Clear(first, 0, first.Length);
            Array.Clear(second, 0, second.Length);

            Assert.IsTrue(queue.TryDequeue(out Pcm16Frame full));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, CopyFrame(full));
            Assert.IsFalse(full.IsLast);

            Assert.IsTrue(queue.TryDequeue(out Pcm16Frame tail));
            CollectionAssert.AreEqual(new byte[] { 5, 6 }, CopyFrame(tail));
            Assert.IsTrue(tail.IsLast);
            Assert.IsFalse(queue.TryDequeue(out _));
        }

        [Test]
        public void FrameQueue_RejectsEmptyComplete()
        {
            Pcm16FrameQueue queue = new Pcm16FrameQueue(1280, 1920000);

            Assert.IsFalse(queue.TryComplete(out string error));
            StringAssert.Contains("不能为空", error);
            Assert.IsFalse(queue.TryDequeue(out _));
        }

        [Test]
        public void FrameQueue_ExactLimitCompletesAutomatically()
        {
            Pcm16FrameQueue queue = new Pcm16FrameQueue(4, 8);
            byte[] bytes = { 1, 2, 3, 4, 5, 6, 7, 8 };

            Assert.IsTrue(queue.TryAppend(bytes, 0, bytes.Length, false, out _));
            Assert.AreEqual(8, queue.TotalPcmBytes);
            Assert.IsTrue(queue.IsInputCompleted);
            Assert.IsTrue(queue.TryDequeue(out Pcm16Frame first));
            Assert.IsFalse(first.IsLast);
            Assert.IsTrue(queue.TryDequeue(out Pcm16Frame second));
            Assert.IsTrue(second.IsLast);
        }

        [Test]
        public void FrameQueue_CompleteBufferReturnsZeroCopySlices()
        {
            Pcm16FrameQueue queue = new Pcm16FrameQueue(4, 12);
            byte[] ownedBuffer = { 1, 2, 3, 4, 5, 6 };

            Assert.IsTrue(queue.TrySetCompleteBuffer(ownedBuffer, out _));
            Assert.IsTrue(queue.TryDequeue(out Pcm16Frame first));
            Assert.AreSame(ownedBuffer, first.Data);
            Assert.AreEqual(0, first.Offset);
            Assert.AreEqual(4, first.Count);
            Assert.IsFalse(first.IsLast);

            Assert.IsTrue(queue.TryDequeue(out Pcm16Frame second));
            Assert.AreSame(ownedBuffer, second.Data);
            Assert.AreEqual(4, second.Offset);
            Assert.AreEqual(2, second.Count);
            Assert.IsTrue(second.IsLast);
            Assert.IsFalse(queue.TryDequeue(out _));
        }

        [Test]
        public void AudioFrameSequencer_SinglePcmFrameDefersEmptyFinalMarker()
        {
            Pcm16FrameQueue queue = new Pcm16FrameQueue(1280, 1920000);
            byte[] pcm = new byte[1280];
            Assert.IsTrue(queue.TrySetCompleteBuffer(pcm, out _));

            XunfeiAudioFrameSequencer sequencer = new XunfeiAudioFrameSequencer();
            Assert.IsTrue(sequencer.TryGetNext(queue, out Pcm16Frame first, out int firstStatus));
            Assert.AreEqual(0, firstStatus);
            Assert.AreSame(pcm, first.Data);
            Assert.AreEqual(1280, first.Count);
            Assert.IsFalse(sequencer.FinalFrameSent);

            Assert.IsTrue(sequencer.TryGetNext(queue, out Pcm16Frame final, out int finalStatus));
            Assert.AreEqual(2, finalStatus);
            Assert.AreEqual(0, final.Count);
            Assert.IsTrue(final.IsLast);
            Assert.IsTrue(sequencer.FinalFrameSent);
            Assert.IsFalse(sequencer.TryGetNext(queue, out _, out _));
        }

        [Test]
        public void FrameQueue_RejectsOverflowWithoutMutatingAcceptedCount()
        {
            Pcm16FrameQueue queue = new Pcm16FrameQueue(4, 12);
            byte[] tenBytes = new byte[10];
            byte[] fourBytes = new byte[4];

            Assert.IsTrue(queue.TryAppend(tenBytes, 0, tenBytes.Length, false, out _));
            Assert.IsFalse(queue.TryAppend(fourBytes, 0, fourBytes.Length, false, out string error));
            Assert.AreEqual(10, queue.TotalPcmBytes);
            Assert.IsFalse(queue.IsInputCompleted);
            StringAssert.Contains("60 秒", error);
        }

        [Test]
        public void FrameQueue_RejectsOddPcm16Write()
        {
            Pcm16FrameQueue queue = new Pcm16FrameQueue(1280, 1920000);

            Assert.IsFalse(queue.TryAppend(new byte[3], 0, 3, false, out string error));
            Assert.AreEqual(0, queue.TotalPcmBytes);
            StringAssert.Contains("2 的整数倍", error);
        }

        [Test]
        public void AudioEncoder_ChunkedStereoResampleMatchesSingleBlock()
        {
            const int sourceFrames = 1001;
            const int sourceRate = 44100;
            const int channels = 2;
            float[] samples = new float[sourceFrames * channels];
            for (int frame = 0; frame < sourceFrames; frame++)
            {
                samples[frame * channels] = (float)Math.Sin(frame * 0.02d);
                samples[frame * channels + 1] = (float)Math.Cos(frame * 0.015d) * 0.5f;
            }

            XunfeiPcm16AudioEncoder single = new XunfeiPcm16AudioEncoder(
                sourceFrames,
                sourceRate,
                channels);
            Assert.IsTrue(single.TryWriteChunk(samples, 0, sourceFrames, out _));
            Assert.IsTrue(single.IsComplete);

            XunfeiPcm16AudioEncoder chunked = new XunfeiPcm16AudioEncoder(
                sourceFrames,
                sourceRate,
                channels);
            int start = 0;
            const int newFramesPerChunk = 113;
            while (start < sourceFrames && !chunked.IsComplete)
            {
                int readFrames = Math.Min(newFramesPerChunk + 1, sourceFrames - start);
                float[] chunk = new float[readFrames * channels];
                Buffer.BlockCopy(
                    samples,
                    start * channels * sizeof(float),
                    chunk,
                    0,
                    chunk.Length * sizeof(float));
                Assert.IsTrue(chunked.TryWriteChunk(chunk, start, readFrames, out string error), error);
                if (start + readFrames >= sourceFrames)
                {
                    break;
                }

                start += newFramesPerChunk;
            }

            Assert.IsTrue(chunked.IsComplete);
            CollectionAssert.AreEqual(single.GetCompletedPcm(), chunked.GetCompletedPcm());
        }

        [Test]
        public void AudioEncoder_DownmixesStereoBeforePcmEncoding()
        {
            XunfeiPcm16AudioEncoder encoder = new XunfeiPcm16AudioEncoder(1, 16000, 2);

            Assert.IsTrue(encoder.TryWriteChunk(new[] { 1f, -1f }, 0, 1, out _));
            CollectionAssert.AreEqual(new byte[] { 0, 0 }, encoder.GetCompletedPcm());
        }

        [Test]
        public void AudioEncoder_RejectsChunkGap()
        {
            XunfeiPcm16AudioEncoder encoder = new XunfeiPcm16AudioEncoder(10, 16000, 1);

            Assert.IsFalse(encoder.TryWriteChunk(new float[5], 5, 5, out string error));
            StringAssert.Contains("缺口", error);
        }

        [Test]
        public void PublicConstants_MatchIatPcmContract()
        {
            Assert.AreEqual(16000, XunfeiSpeechRecognizer.RequiredSampleRate);
            Assert.AreEqual(1, XunfeiSpeechRecognizer.RequiredChannels);
            Assert.AreEqual(16, XunfeiSpeechRecognizer.RequiredBitsPerSample);
            Assert.AreEqual(32000, XunfeiSpeechRecognizer.PcmBytesPerSecond);
            Assert.AreEqual(1920000, XunfeiSpeechRecognizer.MaximumPcmBytes);
        }

        [Test]
        public void StartStreaming_WaitsForFirstAudioBeforeConnecting()
        {
            XunfeiSpeechRecognizer.SetRuntimeCredentials("app", "key", "secret");
            try
            {
                Assert.IsTrue(XunfeiSpeechRecognizer.StartStreaming());
                Assert.AreEqual(
                    XunfeiRecognitionState.WaitingForAudio,
                    XunfeiSpeechRecognizer.State);
                Assert.IsTrue(XunfeiSpeechRecognizer.Cancel());
                Assert.AreEqual(XunfeiRecognitionState.Idle, XunfeiSpeechRecognizer.State);
            }
            finally
            {
                if (XunfeiSpeechRecognizer.IsRecognizing)
                {
                    XunfeiSpeechRecognizer.Cancel();
                }

                XunfeiSpeechRecognizer.ClearRuntimeCredentials();
            }
        }

        [Test]
        public void RejectedRequest_DoesNotPublishSessionFailure()
        {
            XunfeiSpeechRecognizer.ClearRuntimeCredentials();
            int failureCount = 0;
            Action<XunfeiRecognitionError> handler = _ => failureCount++;
            XunfeiSpeechRecognizer.RecognitionFailed += handler;
            LogAssert.Expect(
                LogType.Error,
                "[XunfeiSpeechRecognizer] 请求未接受：请先设置 APPID、APIKey 和 APISecret 运行时凭据。");
            try
            {
                Assert.IsFalse(XunfeiSpeechRecognizer.StartStreaming());
                Assert.AreEqual(0, failureCount);
                Assert.AreEqual(XunfeiRecognitionState.Idle, XunfeiSpeechRecognizer.State);
            }
            finally
            {
                XunfeiSpeechRecognizer.RecognitionFailed -= handler;
            }
        }

        [Test]
        public void StartedSubscriberCancellation_StopsRemainingOldStartedSubscribers()
        {
            XunfeiSpeechRecognizer.SetRuntimeCredentials("app", "key", "secret");
            int laterSubscriberCount = 0;
            Action cancelFirst = () => XunfeiSpeechRecognizer.Cancel();
            Action laterSubscriber = () => laterSubscriberCount++;
            XunfeiSpeechRecognizer.RecognitionStarted += cancelFirst;
            XunfeiSpeechRecognizer.RecognitionStarted += laterSubscriber;
            try
            {
                Assert.IsTrue(XunfeiSpeechRecognizer.StartStreaming());
                Assert.AreEqual(0, laterSubscriberCount);
                Assert.AreEqual(XunfeiRecognitionState.Idle, XunfeiSpeechRecognizer.State);
            }
            finally
            {
                XunfeiSpeechRecognizer.RecognitionStarted -= cancelFirst;
                XunfeiSpeechRecognizer.RecognitionStarted -= laterSubscriber;
                if (XunfeiSpeechRecognizer.IsRecognizing)
                {
                    XunfeiSpeechRecognizer.Cancel();
                }

                XunfeiSpeechRecognizer.ClearRuntimeCredentials();
            }
        }

        [Test]
        public void SetCredentials_InvalidReplacementDoesNotPartiallyMutateExistingValues()
        {
            XunfeiSpeechRecognizer.SetRuntimeCredentials(
                "old-app",
                "old-key",
                "old-secret");
            try
            {
                Assert.Throws<ArgumentException>(() =>
                    XunfeiSpeechRecognizer.SetRuntimeCredentials(
                        "new-app",
                        string.Empty,
                        "new-secret"));

                FieldInfo appIdField = typeof(XunfeiSpeechRecognizer).GetField(
                    "appId",
                    BindingFlags.Static | BindingFlags.NonPublic);
                FieldInfo apiKeyField = typeof(XunfeiSpeechRecognizer).GetField(
                    "apiKey",
                    BindingFlags.Static | BindingFlags.NonPublic);
                Assert.NotNull(appIdField);
                Assert.NotNull(apiKeyField);
                Assert.AreEqual("old-app", appIdField.GetValue(null));
                Assert.AreEqual("old-key", apiKeyField.GetValue(null));
            }
            finally
            {
                XunfeiSpeechRecognizer.ClearRuntimeCredentials();
            }
        }

        [Test]
        public void AudioFrameSchedule_AtSixtyFpsKeepsSixtySecondsNearRealtime()
        {
            const int protocolFrameCount = 1500;
            const float updateDelta = 1f / 60f;
            float now = 0f;
            float next = 0f;
            int sent = 0;

            while (sent < protocolFrameCount && now < 65f)
            {
                if (now + 0.000001f >= next)
                {
                    next = XunfeiSpeechRecognitionDriver.CalculateNextAudioFrameAt(
                        next,
                        now,
                        true);
                    sent++;
                }

                now += updateDelta;
            }

            Assert.AreEqual(protocolFrameCount, sent);
            Assert.Less(now, 61f);
        }

        [Test]
        public void AudioFrameSchedule_LongHitchReanchorsWithoutImmediateBurst()
        {
            float next = XunfeiSpeechRecognitionDriver.CalculateNextAudioFrameAt(
                0.04f,
                2f,
                true);

            Assert.AreEqual(2.04f, next, 0.00001f);
        }

        private static byte[] CopyFrame(Pcm16Frame frame)
        {
            byte[] copy = new byte[frame.Count];
            Buffer.BlockCopy(frame.Data, frame.Offset, copy, 0, frame.Count);
            return copy;
        }
    }
}
