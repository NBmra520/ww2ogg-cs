using System;
using System.IO;
using System.Text;
using NVorbis;

namespace ww2ogg
{
    // Second stage for WAV output: an in-memory Ogg Vorbis stream is decoded to
    // PCM with NVorbis and written as a standard 16-bit PCM RIFF/WAV file.
    // No native dependencies are required.
    // Public so external hosts (e.g. Python via pythonnet) can embed the pipeline.
    public static class WavConverter
    {
        public static void ConvertOggToWav(Stream oggStream, Stream wavOut)
        {
            oggStream.Position = 0;

            using (VorbisReader reader = new VorbisReader(oggStream, false))
            {
                short channels = (short)reader.Channels;
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
                        FloatToPcm16(floatBuf, pcmBytes, read);
                        pcm.Write(pcmBytes, 0, read * 2);
                    }

                    WritePcmWav(wavOut, pcm.ToArray(), channels, sampleRate);
                }
            }
        }

        // Writes interleaved 16-bit PCM as a canonical 44-byte-header WAV file.
        public static void WritePcmWav(Stream wavOut, byte[] pcm16, int channels, int sampleRate)
        {
            const short bitsPerSample = 16;
            short blockAlign = (short)(channels * (bitsPerSample / 8));
            int byteRate = sampleRate * blockAlign;

            byte[] header = new byte[44];
            Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
            WriteUInt32(header, 4, (uint)(36 + pcm16.Length));
            Encoding.ASCII.GetBytes("WAVE").CopyTo(header, 8);
            Encoding.ASCII.GetBytes("fmt ").CopyTo(header, 12);
            WriteUInt32(header, 16, 16);                  // fmt chunk size (PCM)
            WriteUInt16(header, 20, 1);                   // audio format: PCM
            WriteUInt16(header, 22, channels);
            WriteUInt32(header, 24, (uint)sampleRate);
            WriteUInt32(header, 28, (uint)byteRate);
            WriteUInt16(header, 32, blockAlign);
            WriteUInt16(header, 34, bitsPerSample);
            Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
            WriteUInt32(header, 40, (uint)pcm16.Length);

            wavOut.Write(header, 0, header.Length);
            wavOut.Write(pcm16, 0, pcm16.Length);
        }

        internal static void FloatToPcm16(float[] floatBuf, byte[] pcmBytes, int sampleCount)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                int s = (int)(floatBuf[i] * 32767f);
                if (s > short.MaxValue) s = short.MaxValue;
                else if (s < short.MinValue) s = short.MinValue;

                pcmBytes[i * 2] = (byte)(s & 0xFF);
                pcmBytes[i * 2 + 1] = (byte)(s >> 8);
            }
        }

        private static void WriteUInt16(byte[] buf, int offset, int value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteUInt32(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)(value >> 8);
            buf[offset + 2] = (byte)(value >> 16);
            buf[offset + 3] = (byte)(value >> 24);
        }
    }
}
