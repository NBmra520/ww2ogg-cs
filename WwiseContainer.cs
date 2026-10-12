using System;
using System.Collections.Generic;
using System.Buffers.Binary;

namespace ww2ogg
{
    // Kind of uploaded / input container, detected from its magic bytes.
    internal enum WwiseContainerKind
    {
        Wem,
        Pck,
        Bnk
    }

    // One embedded Wwise WEM located inside a container. Offset is absolute
    // relative to the top-level container byte array.
    internal sealed class WemEntry
    {
        public string Name;
        public long Offset;
        public long Size;
    }

    // Parser for Wwise AKPK file packages (.pck) and BKHD sound banks (.bnk).
    // Port of the Python reference in py/wavescan.py + py/bnk.py.
    internal static class WwiseContainer
    {
        public static WwiseContainerKind Detect(byte[] data)
        {
            if (data == null || data.Length < 4)
            {
                throw new ParseErrorStr("file too small");
            }

            if (data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F'
                && (data[3] == (byte)'F' || data[3] == (byte)'X'))
            {
                return WwiseContainerKind.Wem;
            }

            if (HasMagic(data, 0, "AKPK")) return WwiseContainerKind.Pck;
            if (HasMagic(data, 0, "BKHD")) return WwiseContainerKind.Bnk;

            throw new ParseErrorStr("not a Wwise WEM/PCK/BNK file (bad magic)");
        }

        // Lists every WEM inside the given container. A standalone WEM yields a
        // single entry covering the whole array (Name == null).
        public static List<WemEntry> ListWems(byte[] data)
        {
            switch (Detect(data))
            {
                case WwiseContainerKind.Wem:
                    return new List<WemEntry>
                    {
                        new WemEntry { Name = null, Offset = 0, Size = data.Length }
                    };
                case WwiseContainerKind.Bnk:
                    return ParseBnk(data, null);
                default:
                    return ParsePck(data);
            }
        }

        // ---- AKPK (.pck) ----------------------------------------------------

        private static List<WemEntry> ParsePck(byte[] data)
        {
            R r = new R(data);

            // endianness / validity check: the flag sector is 1 on LE files
            r.Seek(0x08);
            uint endianCheck = r.U32();
            if (endianCheck != 1 && endianCheck != 0x01000000)
            {
                throw new ParseErrorStr("PCK: couldn't detect endianness");
            }

            r.Seek(0x04);
            uint headerSize = r.U32();
            r.U32();                    // flag, always 1
            uint languagesSize = r.U32();
            uint banksSize = r.U32();
            uint soundsSize = r.U32();
            uint externalsSize = 0;
            if ((long)languagesSize + banksSize + soundsSize + 0x10 < headerSize)
            {
                externalsSize = r.U32();
            }

            List<WemEntry> result = new List<WemEntry>();

            // Language sector only holds id/name strings, irrelevant for extraction.
            long languageSectorStart = r.Position;
            r.Seek(languageSectorStart + languagesSize);

            // banks sector (embedded .bnk), then loose wems, then externals
            int bankVersion = ParseSector(r, data, banksSize, false, false, 0, result);
            if (bankVersion == 0) bankVersion = 62;
            bankVersion = ParseSector(r, data, soundsSize, true, false, bankVersion, result);
            ParseSector(r, data, externalsSize, true, true, bankVersion, result);

            return result;
        }

        // Returns the (possibly updated) bank version.
        private static int ParseSector(
            R r, byte[] data, uint sectionSize, bool isSounds, bool isExternals,
            int bankVersion, List<WemEntry> entries)
        {
            if (sectionSize == 0) return bankVersion;

            long sectorStart = r.Position;
            uint files = r.U32();
            if (files == 0)
            {
                r.Seek(sectorStart + sectionSize);
                return bankVersion;
            }

            long entrySize = (sectionSize - 4) / (long)files;
            bool altMode = entrySize == 0x18;

            for (int i = 0; i < files; i++)
            {
                uint id, id2 = 0;
                if (altMode && isExternals)
                {
                    id2 = r.U32();   // on-disk order, little endian
                    id = r.U32();
                }
                else
                {
                    id = r.U32();
                }

                uint blockSize = r.U32();

                long size;
                if (altMode && isExternals)
                {
                    size = r.U32();
                }
                else if (altMode)
                {
                    size = r.I64();
                }
                else
                {
                    size = r.U32();
                }

                long offset = r.U32();
                r.U32();                    // language id

                if (blockSize != 0)
                {
                    offset *= blockSize;
                }

                // First bank entry reveals the bank version: third u32 inside BKHD.
                if (!isSounds && bankVersion == 0)
                {
                    long save = r.Position;
                    r.Seek(offset + 8);
                    uint v = r.U32();
                    r.Seek(save);
                    bankVersion = v > 0x1000 ? 62 : (int)v;
                }

                if (isSounds)
                {
                    // The python reference additionally classifies pre-v62 codecs
                    // (xma/ogg/pcm) at offset + 0x14. Those cannot be converted
                    // anyway and are skipped later when WEM parsing fails.
                    string name = (altMode && isExternals)
                        ? id.ToString("X8") + id2.ToString("X8") + ".wem"
                        : id + ".wem";

                    TryAdd(entries, data.Length, name, offset, size);
                }
                else
                {
                    // embedded sound bank; a broken bnk entry is skipped like the reference
                    byte[] bnkData;
                    try
                    {
                        bnkData = r.Slice(offset, size);
                    }
                    catch (ParseError)
                    {
                        continue;
                    }

                    try
                    {
                        List<WemEntry> inner = ParseBnk(bnkData, id.ToString());
                        // ParseBnk returns offsets local to the bnk slice; make them
                        // absolute inside the pck again.
                        foreach (WemEntry e in inner) e.Offset += offset;
                        entries.AddRange(inner);
                    }
                    catch (ParseError)
                    {
                        // invalid BKHD/DIDX/DATA inside this bank entry
                    }
                }
            }

            r.Seek(sectorStart + sectionSize);
            return bankVersion;
        }

        // ---- BKHD (.bnk) ----------------------------------------------------

        // namePrefix == null for a standalone bnk upload ("{wemId}.wem");
        // for a bnk embedded in a pck it is the bank id ("{bankId}_{wemId}.wem").
        private static List<WemEntry> ParseBnk(byte[] data, string namePrefix)
        {
            List<WemEntry> result = new List<WemEntry>();
            R r = new R(data);

            if (!HasMagic(data, 0, "BKHD"))
            {
                throw new ParseErrorStr("BNK: invalid BKHD signature");
            }

            r.Seek(4);
            uint bkhdSize = r.U32();
            r.Seek(8 + bkhdSize);

            if (r.Position >= data.Length)
            {
                return result;          // empty bank
            }

            if (!HasMagic(data, r.Position, "DIDX"))
            {
                throw new ParseErrorStr("BNK: invalid DIDX signature");
            }

            r.Seek(r.Position + 4);
            uint didxSize = r.U32();
            int nWems = (int)(didxSize / 12);

            uint[] wemId = new uint[nWems];
            uint[] wemOffset = new uint[nWems];
            uint[] wemSize = new uint[nWems];
            for (int i = 0; i < nWems; i++)
            {
                wemId[i] = r.U32();
                wemOffset[i] = r.U32();
                wemSize[i] = r.U32();
            }

            if (!HasMagic(data, r.Position, "DATA"))
            {
                throw new ParseErrorStr("BNK: invalid DATA signature");
            }

            r.Seek(r.Position + 4);
            r.U32();                    // DATA section size
            long dataOffset = r.Position;

            for (int i = 0; i < nWems; i++)
            {
                string name = namePrefix == null
                    ? wemId[i] + ".wem"
                    : namePrefix + "_" + wemId[i] + ".wem";

                // offsets are local to this byte array; the pck caller rebases them
                long localOffset = dataOffset + wemOffset[i];
                TryAdd(result, data.Length, name, localOffset, wemSize[i]);
            }

            return result;
        }

        // ---- helpers --------------------------------------------------------

        private static bool TryAdd(List<WemEntry> entries, long containerLength,
            string name, long offset, long size)
        {
            if (offset < 0 || size < 0 || offset + size > containerLength)
            {
                throw new ParseErrorStr("entry '" + name + "' is out of container bounds");
            }

            entries.Add(new WemEntry { Name = name, Offset = offset, Size = size });
            return true;
        }

        private static bool HasMagic(byte[] data, long offset, string magic)
        {
            if (offset < 0 || offset + magic.Length > data.Length) return false;
            for (int i = 0; i < magic.Length; i++)
            {
                if (data[offset + i] != (byte)magic[i]) return false;
            }
            return true;
        }

        // Little-endian bounded reader over an in-memory byte array.
        private sealed class R
        {
            private readonly byte[] _d;
            private long _p;

            public R(byte[] data) { _d = data; }

            public long Position
            {
                get => _p;
            }

            public void Seek(long pos)
            {
                if (pos < 0 || pos > _d.Length)
                {
                    throw new ParseErrorStr("read past end of container");
                }
                _p = pos;
            }

            public uint U32()
            {
                Require(4);
                uint v = BinaryPrimitives.ReadUInt32LittleEndian(
                    new ReadOnlySpan<byte>(_d, (int)_p, 4));
                _p += 4;
                return v;
            }

            public long I64()
            {
                Require(8);
                long v = BinaryPrimitives.ReadInt64LittleEndian(
                    new ReadOnlySpan<byte>(_d, (int)_p, 8));
                _p += 8;
                return v;
            }

            public byte[] Slice(long offset, long size)
            {
                if (offset < 0 || size < 0 || offset + size > _d.Length)
                {
                    throw new ParseErrorStr("entry is out of container bounds");
                }

                byte[] copy = new byte[size];
                Buffer.BlockCopy(_d, (int)offset, copy, 0, (int)size);
                return copy;
            }

            private void Require(int n)
            {
                if (_p + n > _d.Length)
                {
                    throw new ParseErrorStr("read past end of container");
                }
            }
        }
    }
}
