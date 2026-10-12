using System;
using System.IO;
using System.Text;

namespace ww2ogg
{
    // A Wwise WEM encoded with Opus. Unlike the Wwise-modified Vorbis packets,
    // the RIFF 'data' chunk of these files already contains a complete,
    // standards-compliant Ogg Opus bitstream (BOS page with "OpusHead",
    // "OpusTags", then audio pages). Conversion to .opus is therefore a pure
    // extraction; WAV/MP3 output decodes the embedded stream via OpusConverter.
    public sealed class WwiseRiffOpus
    {
        private readonly string _name;
        private readonly bool _littleEndian;
        private readonly byte[] _oggData;

        public int Channels { get; private set; }
        public int SampleRate { get; private set; }

        public WwiseRiffOpus(string filename)
            : this(ReadFileOrThrow(filename), filename)
        {
        }

        public WwiseRiffOpus(byte[] inputData, string name)
        {
            _name = name;

            if (inputData == null || inputData.Length < 12)
            {
                throw new ParseErrorStr("missing RIFF");
            }

            if (inputData[0] == (byte)'R' && inputData[1] == (byte)'I'
                && inputData[2] == (byte)'F' && inputData[3] == (byte)'F')
            {
                _littleEndian = true;
            }
            else if (inputData[0] == (byte)'R' && inputData[1] == (byte)'I'
                && inputData[2] == (byte)'F' && inputData[3] == (byte)'X')
            {
                _littleEndian = false;
            }
            else
            {
                throw new ParseErrorStr("missing RIFF");
            }

            long riffSize = Read32(inputData, 4) + 8;
            if (riffSize > inputData.Length) throw new ParseErrorStr("RIFF truncated");

            long fmtOffset = -1, dataOffset = -1;
            long fmtSize = 0, dataSize = 0;

            long chunkOffset = 12;
            while (chunkOffset + 8 <= riffSize)
            {
                string fourCc = Encoding.ASCII.GetString(inputData, (int)chunkOffset, 4);
                uint chunkSize = Read32(inputData, chunkOffset + 4);

                if (fourCc == "fmt ")
                {
                    fmtOffset = chunkOffset + 8;
                    fmtSize = chunkSize;
                }
                else if (fourCc == "data")
                {
                    dataOffset = chunkOffset + 8;
                    dataSize = chunkSize;
                }

                chunkOffset += 8 + chunkSize + (chunkSize & 1);
            }

            if (fmtOffset < 0 || dataOffset < 0 || fmtSize < 16)
            {
                throw new ParseErrorStr("expected fmt, data chunks");
            }

            Channels = Read16(inputData, fmtOffset + 2);
            SampleRate = (int)Read32(inputData, fmtOffset + 4);

            if (dataOffset + dataSize > inputData.Length)
            {
                throw new ParseErrorStr("data chunk truncated");
            }
            if (dataSize < 27 + 8)
            {
                throw new ParseErrorStr("data chunk is not an Ogg Opus stream");
            }

            if (inputData[dataOffset] != (byte)'O' || inputData[dataOffset + 1] != (byte)'g'
                || inputData[dataOffset + 2] != (byte)'g' || inputData[dataOffset + 3] != (byte)'S')
            {
                throw new ParseErrorStr("data chunk is not an Ogg Opus stream");
            }

            _oggData = new byte[dataSize];
            Buffer.BlockCopy(inputData, (int)dataOffset, _oggData, 0, (int)dataSize);

            if (!ContainsOpusHead(_oggData))
            {
                throw new ParseErrorStr("Ogg stream is missing OpusHead");
            }
        }

        // True when the RIFF 'data' chunk carries an Ogg Opus bitstream.
        public static bool IsOpusWem(byte[] data)
        {
            try
            {
                if (data == null || data.Length < 48) return false;

                bool le;
                if (data[0] == (byte)'R' && data[1] == (byte)'I'
                    && data[2] == (byte)'F' && data[3] == (byte)'F')
                {
                    le = true;
                }
                else if (data[0] == (byte)'R' && data[1] == (byte)'I'
                    && data[2] == (byte)'F' && data[3] == (byte)'X')
                {
                    le = false;
                }
                else
                {
                    return false;
                }

                long riffSize = (le
                        ? (uint)(data[4] | (data[5] << 8) | (data[6] << 16) | (data[7] << 24))
                        : (uint)((data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7]))
                    + 8;
                if (riffSize > data.Length) return false;

                long chunkOffset = 12;
                while (chunkOffset + 8 <= riffSize)
                {
                    uint chunkSize = le
                        ? (uint)(data[chunkOffset + 4]
                            | (data[chunkOffset + 5] << 8)
                            | (data[chunkOffset + 6] << 16)
                            | (data[chunkOffset + 7] << 24))
                        : (uint)((data[chunkOffset + 4] << 24)
                            | (data[chunkOffset + 5] << 16)
                            | (data[chunkOffset + 6] << 8)
                            | data[chunkOffset + 7]);

                    string fourCc = Encoding.ASCII.GetString(data, (int)chunkOffset, 4);
                    if (fourCc == "data")
                    {
                        long payload = chunkOffset + 8;
                        if (payload + 8 > data.Length) return false;
                        return data[payload] == (byte)'O' && data[payload + 1] == (byte)'g'
                            && data[payload + 2] == (byte)'g' && data[payload + 3] == (byte)'S'
                            && ContainsOpusHead(data, payload, Math.Min((long)4096, riffSize - payload));
                    }

                    chunkOffset += 8 + chunkSize + (chunkSize & 1);
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        // The embedded Ogg Opus bitstream, ready to be written as an .opus file.
        public byte[] GetOggData()
        {
            return _oggData;
        }

        public void GenerateOgg(Stream outStream)
        {
            outStream.Write(_oggData, 0, _oggData.Length);
        }

        public void PrintInfo()
        {
            Console.WriteLine("RIFF WAVE (Wwise Opus) " + Channels + " channel"
                + (Channels != 1 ? "s" : "") + " " + SampleRate + " Hz");
            Console.WriteLine("- embedded standard Ogg Opus bitstream ("
                + _oggData.Length + " bytes)");
        }

        private static bool ContainsOpusHead(byte[] ogg)
        {
            return ContainsOpusHead(ogg, 0, ogg.Length);
        }

        private static bool ContainsOpusHead(byte[] buf, long offset, long count)
        {
            // "OpusHead" inside the first BOS page
            long end = Math.Min(buf.Length, offset + count);
            for (long i = offset + 27; i + 8 <= end; i++)
            {
                if (buf[i] == (byte)'O' && buf[i + 1] == (byte)'p'
                    && buf[i + 2] == (byte)'u' && buf[i + 3] == (byte)'s'
                    && buf[i + 4] == (byte)'H' && buf[i + 5] == (byte)'e'
                    && buf[i + 6] == (byte)'a' && buf[i + 7] == (byte)'d')
                {
                    return true;
                }
            }
            return false;
        }

        private ushort Read16(byte[] d, long offset)
        {
            if (_littleEndian)
            {
                return (ushort)(d[offset] | (d[offset + 1] << 8));
            }
            return (ushort)((d[offset] << 8) | d[offset + 1]);
        }

        private uint Read32(byte[] d, long offset)
        {
            if (_littleEndian)
            {
                return (uint)(d[offset]
                    | (d[offset + 1] << 8)
                    | (d[offset + 2] << 16)
                    | (d[offset + 3] << 24));
            }
            return (uint)((d[offset] << 24)
                | (d[offset + 1] << 16)
                | (d[offset + 2] << 8)
                | d[offset + 3]);
        }

        private static byte[] ReadFileOrThrow(string name)
        {
            try
            {
                return File.ReadAllBytes(name);
            }
            catch (Exception)
            {
                throw new FileOpenError(name);
            }
        }
    }
}
