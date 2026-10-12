using System;
using System.IO;
using GroovyCodecs.Mp3;
using GroovyCodecs.Types;
using NVorbis;

namespace ww2ogg
{
    // Second stage for MP3 output: the in-memory Ogg Vorbis stream produced by
    // WwiseRiffVorbis is decoded to PCM with NVorbis and encoded with the pure
    // managed GroovyMp3 encoder (a C# port of LAME/Jump3r). There are no native
    // dependencies, so the same build runs on Windows, Linux and macOS.
    // CBR is used so every player can derive the duration from the frame headers.
    // Public so external hosts (e.g. Python via pythonnet) can embed the pipeline.
    public static class Mp3Converter
    {
        public const int DefaultBitRate = 128;

        public static void ConvertOggToMp3(Stream oggStream, Stream mp3Out, int bitRate = DefaultBitRate)
        {
            oggStream.Position = 0;

            using (VorbisReader reader = new VorbisReader(oggStream, false))
            {
                int channels = reader.Channels;
                int sampleRate = reader.SampleRate;

                using (MemoryStream pcm = new MemoryStream())
                {
                    // Must stay a multiple of channels * 2 (16-bit interleaved PCM).
                    const int pcmChunkBytes = 32768;
                    int sampleCount = pcmChunkBytes / 2;
                    float[] floatBuf = new float[sampleCount];
                    byte[] pcmBytes = new byte[pcmChunkBytes];

                    int read;
                    while ((read = reader.ReadSamples(floatBuf, 0, sampleCount)) > 0)
                    {
                        WavConverter.FloatToPcm16(floatBuf, pcmBytes, read);
                        pcm.Write(pcmBytes, 0, read * 2);
                    }

                    EncodePcmToMp3(pcm.ToArray(), channels, sampleRate, mp3Out, bitRate);
                }
            }
        }

        // Encodes interleaved signed 16-bit little-endian PCM as CBR MP3.
        public static void EncodePcmToMp3(byte[] pcm16, int channels, int sampleRate,
            Stream mp3Out, int bitRate = DefaultBitRate)
        {
            AudioFormat sourceFormat = new AudioFormat
            {
                SampleRate = sampleRate,
                BitsPerSample = 16,
                Channels = (short)channels,
                BigEndian = false,
                IsFloatingPoint = false
            };

            Mp3Encoder encoder = new Mp3Encoder(
                sourceFormat,
                bitRate,
                Mp3Encoder.CHANNEL_MODE_AUTO,
                Mp3Encoder.QUALITY_HIGH,
                false); // CBR

            try
            {
                const int chunkBytes = 32768;
                byte[] mp3Buf = new byte[chunkBytes + chunkBytes / 4 + 7200 + 8192];

                int offset = 0;
                while (offset < pcm16.Length)
                {
                    int count = Math.Min(chunkBytes, pcm16.Length - offset);
                    int written = encoder.EncodeBuffer(pcm16, offset, count, mp3Buf);
                    if (written > 0) mp3Out.Write(mp3Buf, 0, written);
                    offset += count;
                }

                int flushed = encoder.EncodeFinish(mp3Buf);
                if (flushed > 0) mp3Out.Write(mp3Buf, 0, flushed);
            }
            finally
            {
                encoder.Close();
            }
        }
    }
}
