using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Concentus.Structs;

namespace ww2ogg
{
    // Second stage for Opus WEM conversion to WAV/MP3: demuxes the embedded
    // standards-compliant Ogg Opus stream and decodes it to interleaved 16-bit
    // PCM with Concentus (a pure-managed libopus port, no native dependencies).
    //
    // Ogg Opus always runs on a 48 kHz timebase; pre-skip from OpusHead and the
    // end-trim from the final page's granule position are applied per RFC 7845.
    public static class OpusConverter
    {
        public const int OpusSampleRate = 48000;

        public sealed class PcmResult
        {
            public byte[] Pcm16;
            public int Channels;
            public int SampleRate = OpusSampleRate;
        }

        public static PcmResult DecodeOggOpus(byte[] ogg)
        {
            return DecodeOggOpus(new MemoryStream(ogg, false));
        }

        public static PcmResult DecodeOggOpus(Stream oggStream)
        {
            byte[] ogg;
            using (MemoryStream ms = new MemoryStream())
            {
                oggStream.Position = 0;
                oggStream.CopyTo(ms);
                ogg = ms.ToArray();
            }

            int channels = 0;
            int preSkip = 0;
            short outputGain = 0;
            int headerPackets = 0;
            long decodedSamples = 0;
            long finalGranule = 0;
            bool eosSeen = false;

            OpusDecoder decoder = null;
            using (MemoryStream rawPcm = new MemoryStream())
            {
                int pos = 0;
                List<byte> partial = null;

                while (pos + 27 <= ogg.Length)
                {
                    if (ogg[pos] != (byte)'O' || ogg[pos + 1] != (byte)'g'
                        || ogg[pos + 2] != (byte)'g' || ogg[pos + 3] != (byte)'S')
                    {
                        throw new ParseErrorStr("bad Ogg capture pattern");
                    }

                    byte version = ogg[pos + 4];
                    if (version != 0) throw new ParseErrorStr("unsupported Ogg version " + version);

                    byte headerType = ogg[pos + 5];
                    long granule = ReadInt64Le(ogg, pos + 6);
                    int segmentCount = ogg[pos + 26];
                    int tableStart = pos + 27;
                    int bodyStart = tableStart + segmentCount;

                    int bodyLength = 0;
                    for (int i = 0; i < segmentCount; i++)
                    {
                        bodyLength += ogg[tableStart + i];
                    }

                    if (bodyStart + bodyLength > ogg.Length)
                    {
                        throw new ParseErrorStr("Ogg page truncated");
                    }

                    int bodyPos = bodyStart;
                    for (int i = 0; i < segmentCount; i++)
                    {
                        int lacing = ogg[tableStart + i];

                        if (i == 0 && (headerType & 1) != 0)
                        {
                            // page continues a packet that began on the previous page
                            if (partial == null)
                            {
                                throw new ParseErrorStr("continued Ogg packet with no start");
                            }
                        }
                        else if (i == 0)
                        {
                            if (partial != null)
                            {
                                // previous page ended mid-packet without continuation
                                partial = null;
                            }
                            partial = new List<byte>();
                        }
                        else if (partial == null)
                        {
                            partial = new List<byte>();
                        }

                        for (int j = 0; j < lacing; j++)
                        {
                            partial.Add(ogg[bodyPos + j]);
                        }
                        bodyPos += lacing;

                        if (lacing < 255)
                        {
                            byte[] packet = partial.ToArray();
                            partial = null;

                            if (headerPackets == 0)
                            {
                                ParseOpusHead(packet, ref channels, ref preSkip, ref outputGain);
                                decoder = new OpusDecoder(OpusSampleRate, channels);
                                headerPackets++;
                            }
                            else if (headerPackets == 1)
                            {
                                CheckOpusTags(packet);
                                headerPackets++;
                            }
                            else
                            {
                                if (decoder == null)
                                {
                                    throw new ParseErrorStr("audio packet before OpusHead");
                                }

                                short[] pcm = new short[5760 * channels];
                                int samplesPerChannel;
                                try
                                {
                                    samplesPerChannel = decoder.Decode(
                                        packet, 0, packet.Length, pcm, 0, 5760, false);
                                }
                                catch (Exception ex)
                                {
                                    throw new ParseErrorStr("Opus decode failed: " + ex.Message);
                                }

                                if (samplesPerChannel > 0)
                                {
                                    byte[] interleaved = new byte[samplesPerChannel * channels * 2];
                                    Buffer.BlockCopy(pcm, 0, interleaved, 0, interleaved.Length);
                                    rawPcm.Write(interleaved, 0, interleaved.Length);
                                    decodedSamples += samplesPerChannel;
                                }
                            }
                        }
                    }

                    if (granule > 0) finalGranule = granule;
                    if ((headerType & 4) != 0) eosSeen = true;

                    pos = bodyStart + bodyLength;
                }

                if (decoder == null)
                {
                    throw new ParseErrorStr("no OpusHead found in Ogg stream");
                }
                if (!eosSeen && finalGranule == 0)
                {
                    finalGranule = decodedSamples + preSkip;
                }

                long keepSamples = finalGranule - preSkip;
                if (keepSamples < 0) keepSamples = 0;
                if (preSkip + keepSamples > decodedSamples)
                {
                    keepSamples = decodedSamples - preSkip;
                }
                if (keepSamples < 0) keepSamples = 0;

                long startByte = (long)preSkip * channels * 2;
                int keepBytes = (int)(keepSamples * channels * 2);

                byte[] raw = rawPcm.ToArray();
                byte[] trimmed = new byte[keepBytes];
                Buffer.BlockCopy(raw, (int)startByte, trimmed, 0, keepBytes);

                if (outputGain != 0)
                {
                    ApplyGain(trimmed, outputGain);
                }

                return new PcmResult
                {
                    Pcm16 = trimmed,
                    Channels = channels,
                    SampleRate = OpusSampleRate
                };
            }
        }

        private static void ParseOpusHead(byte[] packet, ref int channels,
            ref int preSkip, ref short outputGain)
        {
            if (packet.Length < 19
                || packet[0] != (byte)'O' || packet[1] != (byte)'p'
                || packet[2] != (byte)'u' || packet[3] != (byte)'s'
                || packet[4] != (byte)'H' || packet[5] != (byte)'e'
                || packet[6] != (byte)'a' || packet[7] != (byte)'d')
            {
                throw new ParseErrorStr("bad OpusHead packet");
            }

            // byte 8: version, must currently be 1
            channels = packet[9];
            preSkip = packet[10] | (packet[11] << 8);
            // bytes 12..15: input sample rate (informational only)
            outputGain = (short)(packet[16] | (packet[17] << 8));
            // byte 18: channel mapping family (0 = mono/stereo, supported directly)

            if (channels < 1 || channels > 2)
            {
                throw new ParseErrorStr("unsupported Opus channel count " + channels);
            }
        }

        private static void CheckOpusTags(byte[] packet)
        {
            if (packet.Length < 8
                || packet[0] != (byte)'O' || packet[1] != (byte)'p'
                || packet[2] != (byte)'u' || packet[3] != (byte)'s'
                || packet[4] != (byte)'T' || packet[5] != (byte)'a'
                || packet[6] != (byte)'g' || packet[7] != (byte)'s')
            {
                throw new ParseErrorStr("bad OpusTags packet");
            }
        }

        // OpusHead output gain: s16 Q8 dB unit (256 = 1 dB)
        private static void ApplyGain(byte[] pcm16, short gainQ8)
        {
            double factor = Math.Pow(10.0, gainQ8 / 256.0 / 20.0);
            for (int i = 0; i + 1 < pcm16.Length; i += 2)
            {
                short s = (short)(pcm16[i] | (pcm16[i + 1] << 8));
                double v = s * factor;
                if (v > short.MaxValue) v = short.MaxValue;
                else if (v < short.MinValue) v = short.MinValue;
                short o = (short)v;
                pcm16[i] = (byte)(o & 0xFF);
                pcm16[i + 1] = (byte)(o >> 8);
            }
        }

        private static long ReadInt64Le(byte[] d, int offset)
        {
            long v = 0;
            for (int i = 0; i < 8; i++)
            {
                v |= (long)d[offset + i] << (8 * i);
            }
            return v;
        }
    }
}
