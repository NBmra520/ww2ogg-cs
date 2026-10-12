using System;
using System.Buffers.Binary;
using System.IO;

namespace ww2ogg
{
    // Port of codebook.h / codebook.cpp (stuff from Tremor lowmem).
    // Public because it appears in WwiseRiffVorbis' public constructor signature
    // (a preloaded library can be shared across conversions/threads).
    public sealed class CodebookLibrary
    {
        internal static int ILog(uint v)
        {
            int ret = 0;
            while (v != 0)
            {
                ret++;
                v >>= 1;
            }
            return ret;
        }

        private static uint BookMaptype1Quantvals(uint entries, uint dimensions)
        {
            // get us a starting hint, we'll polish it below
            int bits = ILog(entries);
            uint vals = entries >> ((bits - 1) * (int)(dimensions - 1) / (int)dimensions);

            while (true)
            {
                ulong acc = 1;
                ulong acc1 = 1;
                for (uint i = 0; i < dimensions; i++)
                {
                    acc *= vals;
                    acc1 *= vals + 1;
                }
                if (acc <= entries && acc1 > entries)
                {
                    return vals;
                }
                else
                {
                    if (acc > entries)
                    {
                        vals--;
                    }
                    else
                    {
                        vals++;
                    }
                }
            }
        }

        private byte[] _codebookData;
        private long[] _codebookOffsets;
        private long _codebookCount;

        public CodebookLibrary()
        {
            _codebookData = null;
            _codebookOffsets = null;
            _codebookCount = 0;
        }

        public CodebookLibrary(string filename)
        {
            _codebookData = null;
            _codebookOffsets = null;
            _codebookCount = 0;

            byte[] data;
            try
            {
                data = File.ReadAllBytes(filename);
            }
            catch (Exception)
            {
                throw new FileOpenError(filename);
            }

            long fileSize = data.Length;

            long offsetOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)fileSize - 4));
            _codebookCount = (fileSize - offsetOffset) / 4;

            _codebookData = new byte[offsetOffset];
            Array.Copy(data, 0, _codebookData, 0, (int)offsetOffset);

            _codebookOffsets = new long[_codebookCount];
            ReadOnlySpan<byte> table = data.AsSpan((int)offsetOffset);
            for (long i = 0; i < _codebookCount; i++)
            {
                _codebookOffsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(table.Slice((int)i * 4, 4));
            }
        }

        private byte[] GetCodebook(int i, out long size)
        {
            if (_codebookData == null || _codebookOffsets == null)
            {
                throw new ParseErrorStr("codebook library not loaded");
            }
            if (i >= _codebookCount - 1 || i < 0)
            {
                size = -1;
                return null;
            }
            size = _codebookOffsets[i + 1] - _codebookOffsets[i];
            return _codebookData.AsSpan((int)_codebookOffsets[i], (int)size).ToArray();
        }

        internal void Rebuild(int i, BitWriter bos)
        {
            long signedCbSize;
            byte[] cb = GetCodebook(i, out signedCbSize);

            if (cb == null || -1 == signedCbSize) throw new InvalidId(i);

            ulong cbSize = (ulong)signedCbSize;

            using (MemoryStream ms = new MemoryStream(cb, false))
            {
                BitReader bis = new BitReader(ms);
                Rebuild(bis, cbSize, bos);
            }
        }

        /* cbSize == 0 to not check size (for an inline bitstream) */
        internal void Copy(BitReader bis, BitWriter bos)
        {
            /* IN: 24 bit identifier, 16 bit dimensions, 24 bit entry count */
            uint id = bis.Read(24);
            uint dimensions = bis.Read(16);
            uint entries = bis.Read(24);

            if (0x564342 != id)
            {
                throw new ParseErrorStr("invalid codebook identifier");
            }

            /* OUT: 24 bit identifier, 16 bit dimensions, 24 bit entry count */
            bos.Write(id, 24);
            bos.Write(dimensions, 16);
            bos.Write(entries, 24);

            // gather codeword lengths

            /* IN/OUT: 1 bit ordered flag */
            uint ordered = bis.Read(1);
            bos.Write(ordered, 1);
            if (ordered != 0)
            {
                /* IN/OUT: 5 bit initial length */
                uint initialLength = bis.Read(5);
                bos.Write(initialLength, 5);

                uint currentEntry = 0;
                while (currentEntry < entries)
                {
                    /* IN/OUT: ilog(entries-current_entry) bit count w/ given length */
                    int numberBits = ILog(entries - currentEntry);
                    uint number = bis.Read(numberBits);
                    bos.Write(number, numberBits);
                    currentEntry += number;
                }
                if (currentEntry > entries) throw new ParseErrorStr("current_entry out of range");
            }
            else
            {
                /* IN/OUT: 1 bit sparse flag */
                uint sparse = bis.Read(1);
                bos.Write(sparse, 1);

                for (uint i = 0; i < entries; i++)
                {
                    bool presentBool = true;

                    if (sparse != 0)
                    {
                        /* IN/OUT 1 bit sparse presence flag */
                        uint present = bis.Read(1);
                        bos.Write(present, 1);

                        presentBool = (0 != present);
                    }

                    if (presentBool)
                    {
                        /* IN/OUT: 5 bit codeword length-1 */
                        uint codewordLength = bis.Read(5);
                        bos.Write(codewordLength, 5);
                    }
                }
            } // done with lengths


            // lookup table

            /* IN/OUT: 4 bit lookup type */
            uint lookupType = bis.Read(4);
            bos.Write(lookupType, 4);

            if (0 == lookupType)
            {
                // no lookup table
            }
            else if (1 == lookupType)
            {
                /* IN/OUT: 32 bit minimum length, 32 bit maximum length, 4 bit value length-1, 1 bit sequence flag */
                uint min = bis.Read(32);
                uint max = bis.Read(32);
                uint valueLength = bis.Read(4);
                uint sequenceFlag = bis.Read(1);
                bos.Write(min, 32);
                bos.Write(max, 32);
                bos.Write(valueLength, 4);
                bos.Write(sequenceFlag, 1);

                uint quantvals = BookMaptype1Quantvals(entries, dimensions);
                for (uint i = 0; i < quantvals; i++)
                {
                    /* IN/OUT: n bit value */
                    uint val = bis.Read((int)valueLength + 1);
                    bos.Write(val, (int)valueLength + 1);
                }
            }
            else if (2 == lookupType)
            {
                throw new ParseErrorStr("didn't expect lookup type 2");
            }
            else
            {
                throw new ParseErrorStr("invalid lookup type");
            }
        }

        /* cbSize == 0 to not check size (for an inline bitstream) */
        internal void Rebuild(BitReader bis, ulong cbSize, BitWriter bos)
        {
            /* IN: 4 bit dimensions, 14 bit entry count */
            uint dimensions = bis.Read(4);
            uint entries = bis.Read(14);

            /* OUT: 24 bit identifier, 16 bit dimensions, 24 bit entry count */
            bos.Write(0x564342, 24);
            bos.Write(dimensions, 16);
            bos.Write(entries, 24);

            // gather codeword lengths

            /* IN/OUT: 1 bit ordered flag */
            uint ordered = bis.Read(1);
            bos.Write(ordered, 1);
            if (ordered != 0)
            {
                /* IN/OUT: 5 bit initial length */
                uint initialLength = bis.Read(5);
                bos.Write(initialLength, 5);

                uint currentEntry = 0;
                while (currentEntry < entries)
                {
                    /* IN/OUT: ilog(entries-current_entry) bit count w/ given length */
                    int numberBits = ILog(entries - currentEntry);
                    uint number = bis.Read(numberBits);
                    bos.Write(number, numberBits);
                    currentEntry += number;
                }
                if (currentEntry > entries) throw new ParseErrorStr("current_entry out of range");
            }
            else
            {
                /* IN: 3 bit codeword length length, 1 bit sparse flag */
                uint codewordLengthLength = bis.Read(3);
                uint sparse = bis.Read(1);

                if (0 == codewordLengthLength || 5 < codewordLengthLength)
                {
                    throw new ParseErrorStr("nonsense codeword length");
                }

                /* OUT: 1 bit sparse flag */
                bos.Write(sparse, 1);

                for (uint i = 0; i < entries; i++)
                {
                    bool presentBool = true;

                    if (sparse != 0)
                    {
                        /* IN/OUT 1 bit sparse presence flag */
                        uint present = bis.Read(1);
                        bos.Write(present, 1);

                        presentBool = (0 != present);
                    }

                    if (presentBool)
                    {
                        /* IN: n bit codeword length-1 */
                        uint codewordLength = bis.Read((int)codewordLengthLength);

                        /* OUT: 5 bit codeword length-1 */
                        bos.Write(codewordLength, 5);
                    }
                }
            } // done with lengths


            // lookup table

            /* IN: 1 bit lookup type */
            uint lookupType = bis.Read(1);
            /* OUT: 4 bit lookup type */
            bos.Write(lookupType, 4);

            if (0 == lookupType)
            {
                // no lookup table
            }
            else if (1 == lookupType)
            {
                /* IN/OUT: 32 bit minimum length, 32 bit maximum length, 4 bit value length-1, 1 bit sequence flag */
                uint min = bis.Read(32);
                uint max = bis.Read(32);
                uint valueLength = bis.Read(4);
                uint sequenceFlag = bis.Read(1);
                bos.Write(min, 32);
                bos.Write(max, 32);
                bos.Write(valueLength, 4);
                bos.Write(sequenceFlag, 1);

                uint quantvals = BookMaptype1Quantvals(entries, dimensions);
                for (uint i = 0; i < quantvals; i++)
                {
                    /* IN/OUT: n bit value */
                    uint val = bis.Read((int)valueLength + 1);
                    bos.Write(val, (int)valueLength + 1);
                }
            }
            else if (2 == lookupType)
            {
                throw new ParseErrorStr("didn't expect lookup type 2");
            }
            else
            {
                throw new ParseErrorStr("invalid lookup type");
            }

            /* check that we used exactly all bytes */
            /* note: if all bits are used in the last byte there will be one extra 0 byte */
            if (0 != cbSize && bis.TotalBitsRead / 8 + 1 != cbSize)
            {
                throw new SizeMismatch(cbSize, bis.TotalBitsRead / 8 + 1);
            }
        }
    }
}
