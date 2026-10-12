using System;
using System.Buffers.Binary;
using System.IO;

namespace ww2ogg
{
    // Host-endian-neutral integer reading, port of the free functions in Bit_stream.h.
    // Implemented on top of the BCL BinaryPrimitives readers (endianness explicit,
    // no dependency on the host architecture).
    internal static class BinIo
    {
        public static ushort Read16Le(Stream s)
        {
            Span<byte> b = stackalloc byte[2];
            s.ReadExactly(b);
            return BinaryPrimitives.ReadUInt16LittleEndian(b);
        }

        public static uint Read32Le(Stream s)
        {
            Span<byte> b = stackalloc byte[4];
            s.ReadExactly(b);
            return BinaryPrimitives.ReadUInt32LittleEndian(b);
        }

        public static ushort Read16Be(Stream s)
        {
            Span<byte> b = stackalloc byte[2];
            s.ReadExactly(b);
            return BinaryPrimitives.ReadUInt16BigEndian(b);
        }

        public static uint Read32Be(Stream s)
        {
            Span<byte> b = stackalloc byte[4];
            s.ReadExactly(b);
            return BinaryPrimitives.ReadUInt32BigEndian(b);
        }

        public static void Write32Le(byte[] b, int offset, uint v)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(offset), v);
        }
    }

    // Pull individual bits (LSB first) off a stream. Port of Bit_stream.
    internal sealed class BitReader
    {
        public class OutOfBits : Exception { }

        private readonly Stream _s;
        private byte _bitBuffer;
        private int _bitsLeft;
        private ulong _totalBitsRead;

        public BitReader(Stream s)
        {
            _s = s;
        }

        public ulong TotalBitsRead => _totalBitsRead;

        public bool GetBit()
        {
            if (_bitsLeft == 0)
            {
                int c = _s.ReadByte();
                if (c < 0) throw new OutOfBits();
                _bitBuffer = (byte)c;
                _bitsLeft = 8;
            }
            _totalBitsRead++;
            _bitsLeft--;
            return (_bitBuffer & (0x80 >> _bitsLeft)) != 0;
        }

        public uint Read(int bitSize)
        {
            if (bitSize > 32) throw new ArgumentException("too many bits");
            uint total = 0;
            for (int i = 0; i < bitSize; i++)
            {
                if (GetBit()) total |= (1U << i);
            }
            return total;
        }
    }

    // Pack bits (LSB first) into Ogg pages. Port of Bit_oggstream.
    internal sealed class BitWriter : IDisposable
    {
        public class WeirdCharSize : Exception { }
        public class TooManyBits : Exception { }
        public class IntTooBig : Exception { }

        private const int HeaderBytes = 27;
        private const int MaxSegments = 255;
        private const int SegmentSize = 255;

        private readonly Stream _os;
        private byte _bitBuffer;
        private uint _bitsStored;

        private uint _payloadBytes;
        private bool _first = true;
        private bool _continued;
        private readonly byte[] _pageBuffer = new byte[HeaderBytes + MaxSegments + SegmentSize * MaxSegments];
        private uint _granule;
        private uint _seqno;

        public BitWriter(Stream os)
        {
            _os = os;
        }

        public void PutBit(bool bit)
        {
            if (bit)
            {
                _bitBuffer |= (byte)(1 << (int)_bitsStored);
            }

            _bitsStored++;
            if (_bitsStored == 8)
            {
                FlushBits();
            }
        }

        public void SetGranule(uint g)
        {
            _granule = g;
        }

        // Corresponds to Bit_uint/Bit_uintv output, including the original
        // range validation (n == 0 writes nothing).
        public void Write(uint v, int n)
        {
            if (n > 32) throw new TooManyBits();
            if (n > 0 && (v >> (n - 1)) > 1U) throw new IntTooBig();

            for (int i = 0; i < n; i++)
            {
                PutBit((v & (1U << i)) != 0);
            }
        }

        public void FlushBits()
        {
            if (_bitsStored != 0)
            {
                if (_payloadBytes == SegmentSize * MaxSegments)
                {
                    throw new ParseErrorStr("ran out of space in an Ogg packet");
                }

                _pageBuffer[HeaderBytes + MaxSegments + _payloadBytes] = _bitBuffer;
                _payloadBytes++;

                _bitsStored = 0;
                _bitBuffer = 0;
            }
        }

        public void FlushPage(bool nextContinued = false, bool last = false)
        {
            if (_payloadBytes != SegmentSize * MaxSegments)
            {
                FlushBits();
            }

            if (_payloadBytes != 0)
            {
                uint segments = (_payloadBytes + SegmentSize) / SegmentSize;  // intentionally round up
                if (segments == MaxSegments + 1) segments = MaxSegments; // at max eschews the final 0

                // move payload back
                for (uint i = 0; i < _payloadBytes; i++)
                {
                    _pageBuffer[HeaderBytes + segments + i] = _pageBuffer[HeaderBytes + MaxSegments + i];
                }

                _pageBuffer[0] = (byte)'O';
                _pageBuffer[1] = (byte)'g';
                _pageBuffer[2] = (byte)'g';
                _pageBuffer[3] = (byte)'S';
                _pageBuffer[4] = 0; // stream_structure_version
                _pageBuffer[5] = (byte)((_continued ? 1 : 0) | (_first ? 2 : 0) | (last ? 4 : 0)); // header_type_flag
                BinIo.Write32Le(_pageBuffer, 6, _granule);   // granule low bits
                BinIo.Write32Le(_pageBuffer, 10, 0);         // granule high bits
                if (_granule == 0xFFFFFFFFU)
                {
                    BinIo.Write32Le(_pageBuffer, 10, 0xFFFFFFFFU);
                }
                BinIo.Write32Le(_pageBuffer, 14, 1);       // stream serial number
                BinIo.Write32Le(_pageBuffer, 18, _seqno);  // page sequence number
                BinIo.Write32Le(_pageBuffer, 22, 0);       // checksum (0 for now)
                _pageBuffer[26] = (byte)segments;          // segment count

                // lacing values
                uint bytesLeft = _payloadBytes;
                for (uint i = 0; i < segments; i++)
                {
                    if (bytesLeft >= SegmentSize)
                    {
                        bytesLeft -= SegmentSize;
                        _pageBuffer[27 + i] = SegmentSize;
                    }
                    else
                    {
                        _pageBuffer[27 + i] = (byte)bytesLeft;
                    }
                }

                // checksum
                int pageLength = HeaderBytes + (int)segments + (int)_payloadBytes;
                BinIo.Write32Le(_pageBuffer, 22, Crc32.Checksum(_pageBuffer, pageLength));

                // output to stream
                _os.Write(_pageBuffer, 0, pageLength);

                _seqno++;
                _first = false;
                _continued = nextContinued;
                _payloadBytes = 0;
            }
        }

        public void Dispose()
        {
            FlushPage();
        }
    }
}
