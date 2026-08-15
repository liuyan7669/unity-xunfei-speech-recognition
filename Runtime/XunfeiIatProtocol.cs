using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Cowart.XunfeiSpeechRecognition
{
    /// <summary>
    /// 讯飞语音听写（流式版）协议工具。
    /// 官方接口限制：PCM 16 kHz、16-bit、单声道，单次会话最长 60 秒。
    /// </summary>
    internal static class XunfeiIatProtocol
    {
        internal const string Host = "iat-api.xfyun.cn";
        internal const string Path = "/v2/iat";
        internal const int SampleRate = 16000;
        internal const int BytesPerChunk = 1280;
        internal const int ChunkMilliseconds = 40;
        internal const int MaximumAudioSeconds = 60;

        internal static string CreateAuthenticatedUrl(
            string apiKey,
            string apiSecret,
            DateTime utcNow)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ArgumentException("APIKey 不能为空。", nameof(apiKey));
            }

            if (string.IsNullOrWhiteSpace(apiSecret))
            {
                throw new ArgumentException("APISecret 不能为空。", nameof(apiSecret));
            }

            string date = utcNow.ToUniversalTime().ToString("r", CultureInfo.InvariantCulture);
            string signatureOrigin =
                "host: " + Host + "\n" +
                "date: " + date + "\n" +
                "GET " + Path + " HTTP/1.1";

            byte[] secretBytes = Encoding.UTF8.GetBytes(apiSecret);
            byte[] originBytes = Encoding.UTF8.GetBytes(signatureOrigin);
            string signature;
            using (HMACSHA256 hmac = new HMACSHA256(secretBytes))
            {
                signature = Convert.ToBase64String(hmac.ComputeHash(originBytes));
            }

            string authorizationOrigin =
                "api_key=\"" + apiKey + "\", algorithm=\"hmac-sha256\", " +
                "headers=\"host date request-line\", signature=\"" + signature + "\"";
            string authorization = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(authorizationOrigin));

            return "wss://" + Host + Path +
                "?authorization=" + Uri.EscapeDataString(authorization) +
                "&date=" + Uri.EscapeDataString(date) +
                "&host=" + Uri.EscapeDataString(Host);
        }

        internal static string CreateAudioFrame(
            string appId,
            byte[] pcmBytes,
            int status,
            string language = "zh_cn",
            string accent = "mandarin")
        {
            return CreateAudioFrame(
                appId,
                pcmBytes,
                0,
                pcmBytes != null ? pcmBytes.Length : 0,
                status,
                language,
                accent);
        }

        internal static string CreateAudioFrame(
            string appId,
            byte[] pcmBytes,
            int offset,
            int count,
            int status,
            string language = "zh_cn",
            string accent = "mandarin")
        {
            if (string.IsNullOrWhiteSpace(appId))
            {
                throw new ArgumentException("APPID 不能为空。", nameof(appId));
            }

            if (status < 0 || status > 2)
            {
                throw new ArgumentOutOfRangeException(nameof(status));
            }

            if (pcmBytes == null)
            {
                if (offset != 0 || count != 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(offset));
                }
            }
            else if (offset < 0 || count < 0 || offset > pcmBytes.Length - count)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            JObject root = new JObject();
            if (status == 0)
            {
                root["common"] = new JObject
                {
                    ["app_id"] = appId
                };
                root["business"] = new JObject
                {
                    ["language"] = language,
                    ["domain"] = "iat",
                    ["accent"] = accent,
                    ["dwa"] = "wpgs",
                    ["eos"] = 5000
                };
            }

            root["data"] = new JObject
            {
                ["status"] = status,
                ["format"] = "audio/L16;rate=16000",
                ["encoding"] = "raw",
                ["audio"] = pcmBytes == null || count == 0
                    ? string.Empty
                    : Convert.ToBase64String(pcmBytes, offset, count)
            };

            return root.ToString(Formatting.None);
        }

        internal static byte[] ExtractPcm16FromWav(byte[] wavBytes)
        {
            if (wavBytes == null || wavBytes.Length < 44 ||
                ReadAscii(wavBytes, 0, 4) != "RIFF" ||
                ReadAscii(wavBytes, 8, 4) != "WAVE")
            {
                throw new ArgumentException("录音不是有效的 RIFF/WAVE 文件。", nameof(wavBytes));
            }

            int offset = 12;
            int formatCode = 0;
            int channels = 0;
            int sampleRate = 0;
            int bitsPerSample = 0;
            int dataOffset = -1;
            int dataLength = 0;

            while (offset + 8 <= wavBytes.Length)
            {
                string chunkId = ReadAscii(wavBytes, offset, 4);
                int chunkLength = ReadInt32LittleEndian(wavBytes, offset + 4);
                int chunkDataOffset = offset + 8;
                if (chunkLength < 0 || chunkLength > wavBytes.Length - chunkDataOffset)
                {
                    throw new ArgumentException("WAV 数据块长度无效。", nameof(wavBytes));
                }

                if (chunkId == "fmt " && chunkLength >= 16)
                {
                    formatCode = ReadInt16LittleEndian(wavBytes, chunkDataOffset);
                    channels = ReadInt16LittleEndian(wavBytes, chunkDataOffset + 2);
                    sampleRate = ReadInt32LittleEndian(wavBytes, chunkDataOffset + 4);
                    bitsPerSample = ReadInt16LittleEndian(wavBytes, chunkDataOffset + 14);
                }
                else if (chunkId == "data")
                {
                    dataOffset = chunkDataOffset;
                    dataLength = chunkLength;
                    break;
                }

                offset = chunkDataOffset + chunkLength + (chunkLength & 1);
            }

            if (formatCode != 1 || channels != 1 ||
                sampleRate != SampleRate || bitsPerSample != 16)
            {
                throw new ArgumentException(
                    "讯飞听写只接受 16 kHz、16-bit、单声道的未压缩 PCM WAV。",
                    nameof(wavBytes));
            }

            if (dataOffset < 0 || dataLength <= 0)
            {
                throw new ArgumentException("WAV 中没有可识别的 PCM 数据。", nameof(wavBytes));
            }

            if ((dataLength & 1) != 0)
            {
                throw new ArgumentException("WAV 的 PCM16 数据长度必须是 2 的整数倍。", nameof(wavBytes));
            }

            if (dataLength > XunfeiSpeechRecognizer.MaximumPcmBytes)
            {
                throw new ArgumentException(
                    "单次语音听写最多支持 60 秒，WAV PCM 数据不能超过 1920000 字节。",
                    nameof(wavBytes));
            }

            byte[] pcmBytes = new byte[dataLength];
            Buffer.BlockCopy(wavBytes, dataOffset, pcmBytes, 0, dataLength);
            return pcmBytes;
        }

        private static string ReadAscii(byte[] bytes, int offset, int count)
        {
            return Encoding.ASCII.GetString(bytes, offset, count);
        }

        private static int ReadInt16LittleEndian(byte[] bytes, int offset)
        {
            return bytes[offset] | (bytes[offset + 1] << 8);
        }

        private static int ReadInt32LittleEndian(byte[] bytes, int offset)
        {
            return bytes[offset] |
                (bytes[offset + 1] << 8) |
                (bytes[offset + 2] << 16) |
                (bytes[offset + 3] << 24);
        }
    }

    /// <summary>合并讯飞动态修正结果（wpgs 的 apd/rpl）。</summary>
    internal sealed class XunfeiIatResultAssembler
    {
        private readonly SortedDictionary<int, string> segments =
            new SortedDictionary<int, string>();

        internal string CurrentText { get; private set; } = string.Empty;

        internal void Reset()
        {
            segments.Clear();
            CurrentText = string.Empty;
        }

        internal XunfeiIatResponse Apply(string json)
        {
            JObject root = JObject.Parse(json);
            XunfeiIatResponse response = new XunfeiIatResponse
            {
                Code = root.Value<int?>("code") ?? -1,
                Message = root.Value<string>("message") ?? string.Empty,
                Sid = root.Value<string>("sid") ?? string.Empty
            };

            if (response.Code != 0)
            {
                return response;
            }

            JObject data = root["data"] as JObject;
            response.Status = data != null ? data.Value<int?>("status") ?? -1 : -1;
            JObject result = data != null ? data["result"] as JObject : null;
            if (result == null)
            {
                response.Text = CurrentText;
                return response;
            }

            int sequence = result.Value<int?>("sn") ?? segments.Count;
            string mode = result.Value<string>("pgs") ?? "apd";
            JArray replacementRange = result["rg"] as JArray;
            if (mode == "rpl" && replacementRange != null && replacementRange.Count >= 2)
            {
                int start = replacementRange[0].Value<int>();
                int end = replacementRange[1].Value<int>();
                for (int index = start; index <= end; index++)
                {
                    segments.Remove(index);
                }
            }

            StringBuilder segment = new StringBuilder();
            JArray wordSegments = result["ws"] as JArray;
            if (wordSegments != null)
            {
                foreach (JToken wordSegment in wordSegments)
                {
                    JArray candidates = wordSegment["cw"] as JArray;
                    if (candidates != null && candidates.Count > 0)
                    {
                        segment.Append(candidates[0].Value<string>("w") ?? string.Empty);
                    }
                }
            }

            segments[sequence] = segment.ToString();
            StringBuilder complete = new StringBuilder();
            foreach (KeyValuePair<int, string> pair in segments)
            {
                complete.Append(pair.Value);
            }

            CurrentText = complete.ToString();
            response.Text = CurrentText;
            return response;
        }
    }

    internal sealed class XunfeiIatResponse
    {
        internal int Code { get; set; }
        internal int Status { get; set; } = -1;
        internal string Message { get; set; } = string.Empty;
        internal string Sid { get; set; } = string.Empty;
        internal string Text { get; set; } = string.Empty;
        internal bool IsFinal => Code == 0 && Status == 2;
    }
}
