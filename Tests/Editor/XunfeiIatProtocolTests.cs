using System;
using System.Text;
using Cowart.XunfeiSpeechRecognition;
using NUnit.Framework;

namespace Cowart.XunfeiSpeechRecognition.Tests
{
    public sealed class XunfeiIatProtocolTests
    {
        [Test]
        public void CreateAuthenticatedUrl_ContainsOfficialEndpointAndEncodedFields()
        {
            string url = XunfeiIatProtocol.CreateAuthenticatedUrl(
                "test-key",
                "test-secret",
                new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc));

            StringAssert.StartsWith("wss://iat-api.xfyun.cn/v2/iat?", url);
            StringAssert.Contains("authorization=", url);
            StringAssert.Contains("date=", url);
            StringAssert.Contains("host=iat-api.xfyun.cn", url);
        }

        [Test]
        public void ResultAssembler_AppliesDynamicReplacementRange()
        {
            XunfeiIatResultAssembler assembler = new XunfeiIatResultAssembler();
            assembler.Apply(CreateResponse(0, "apd", null, "今天天气"));
            assembler.Apply(CreateResponse(1, "apd", null, "真好"));

            XunfeiIatResponse response = assembler.Apply(
                CreateResponse(2, "rpl", new[] { 0, 1 }, "今天天气很好"));

            Assert.AreEqual("今天天气很好", response.Text);
        }

        [Test]
        public void ResultAssembler_HandlesActualIncrementalReplacementPattern()
        {
            const string expected = "这是科大讯飞语音转文字测试";
            XunfeiIatResultAssembler assembler = new XunfeiIatResultAssembler();
            for (int sequence = 1; sequence <= expected.Length; sequence++)
            {
                string current = expected.Substring(0, sequence);
                assembler.Apply(CreateResponse(
                    sequence,
                    sequence == 1 ? "apd" : "rpl",
                    sequence == 1 ? null : new[] { 1, sequence - 1 },
                    current));
            }

            XunfeiIatResponse final = assembler.Apply(
                CreateResponse(expected.Length + 1, "apd", null, "。", true));

            Assert.AreEqual(expected + "。", final.Text);
            Assert.IsTrue(final.IsFinal);
        }

        [Test]
        public void ExtractPcm16FromWav_ReturnsOnlyDataChunk()
        {
            byte[] pcm = { 1, 2, 3, 4 };
            byte[] wav = CreatePcm16Wav(pcm);

            CollectionAssert.AreEqual(
                pcm,
                XunfeiIatProtocol.ExtractPcm16FromWav(wav));
        }

        [Test]
        public void FirstAudioFrame_UsesFiveSecondSilenceEndpointing()
        {
            string frame = XunfeiIatProtocol.CreateAudioFrame(
                "test-app",
                new byte[] { 0, 0 },
                0);

            StringAssert.Contains("\"domain\":\"iat\"", frame);
            StringAssert.Contains("\"eos\":5000", frame);
        }

        private static string CreateResponse(
            int sequence,
            string pgs,
            int[] range,
            string text,
            bool isFinal = false)
        {
            string rangeJson = range == null
                ? string.Empty
                : ",\"rg\":[" + range[0] + "," + range[1] + "]";
            return "{\"code\":0,\"sid\":\"test\",\"data\":{" +
                "\"status\":" + (isFinal ? 2 : 1) + "," +
                "\"result\":{\"sn\":" + sequence + ",\"pgs\":\"" + pgs + "\"" +
                rangeJson + ",\"ws\":[{\"cw\":[{\"w\":\"" + text + "\"}]}]}}}";
        }

        private static byte[] CreatePcm16Wav(byte[] pcm)
        {
            byte[] wav = new byte[44 + pcm.Length];
            WriteAscii(wav, 0, "RIFF");
            WriteInt32(wav, 4, 36 + pcm.Length);
            WriteAscii(wav, 8, "WAVE");
            WriteAscii(wav, 12, "fmt ");
            WriteInt32(wav, 16, 16);
            WriteInt16(wav, 20, 1);
            WriteInt16(wav, 22, 1);
            WriteInt32(wav, 24, 16000);
            WriteInt32(wav, 28, 32000);
            WriteInt16(wav, 32, 2);
            WriteInt16(wav, 34, 16);
            WriteAscii(wav, 36, "data");
            WriteInt32(wav, 40, pcm.Length);
            Buffer.BlockCopy(pcm, 0, wav, 44, pcm.Length);
            return wav;
        }

        private static void WriteAscii(byte[] destination, int offset, string value)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(value);
            Buffer.BlockCopy(bytes, 0, destination, offset, bytes.Length);
        }

        private static void WriteInt16(byte[] destination, int offset, int value)
        {
            destination[offset] = (byte)value;
            destination[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteInt32(byte[] destination, int offset, int value)
        {
            destination[offset] = (byte)value;
            destination[offset + 1] = (byte)(value >> 8);
            destination[offset + 2] = (byte)(value >> 16);
            destination[offset + 3] = (byte)(value >> 24);
        }
    }
}
