using System;
using System.IO;
using System.Text;

namespace ww2ogg
{
    // Public so external hosts (e.g. Python via pythonnet) can embed the converter.
    public enum ForcePacketFormat
    {
        NoForcePacketFormat,
        ForceModPackets,
        ForceNoModPackets
    }

    public sealed class WwiseRiffVorbis
    {
        private struct PacketHeader
        {
            public long HeaderSize;
            public long PayloadOffset;
            public long NextOffset;
            public uint Size;
            public uint Granule;
        }

        private readonly string _fileName;
        private readonly string _codebooksName;
        private readonly Stream _infile;
        private long _fileSize;

        private bool _littleEndian;

        private long _riffSize;
        private long _fmtOffset, _cueOffset, _listOffset, _smplOffset, _vorbOffset, _dataOffset;
        private long _fmtSize, _cueSize, _listSize, _smplSize, _vorbSize, _dataSize;

        // RIFF fmt
        private ushort _channels;
        private uint _sampleRate;
        private uint _avgBytesPerSecond;

        // RIFF extended fmt
        private ushort _extUnk;
        private uint _subtype;

        // cue info
        private uint _cueCount;

        // smpl info
        private uint _loopCount, _loopStart, _loopEnd;

        // vorbis info
        private uint _sampleCount;
        private uint _setupPacketOffset;
        private uint _firstAudioPacketOffset;
        private uint _uid;
        private byte _blocksize0Pow;
        private byte _blocksize1Pow;

        private readonly bool _inlineCodebooks, _fullSetup;
        private bool _headerTriadPresent, _oldPacketHeaders;
        private bool _noGranule, _modPackets;

        private ushort Read16()
        {
            return _littleEndian ? BinIo.Read16Le(_infile) : BinIo.Read16Be(_infile);
        }

        private uint Read32()
        {
            return _littleEndian ? BinIo.Read32Le(_infile) : BinIo.Read32Be(_infile);
        }

        /* Modern 2 or 6 byte header */
        private PacketHeader ReadPacket(long offset, bool noGranule)
        {
            _infile.Position = offset;

            ushort size = Read16();
            uint granule = 0;
            long headerSize = noGranule ? 2 : 6;
            if (!noGranule)
            {
                granule = Read32();
            }

            return new PacketHeader
            {
                HeaderSize = headerSize,
                Size = size,
                Granule = granule,
                PayloadOffset = offset + headerSize,
                NextOffset = offset + headerSize + size
            };
        }

        /* Old 8 byte header */
        private PacketHeader ReadPacket8(long offset)
        {
            _infile.Position = offset;

            uint size = Read32();
            uint granule = Read32();

            return new PacketHeader
            {
                HeaderSize = 8,
                Size = size,
                Granule = granule,
                PayloadOffset = offset + 8,
                NextOffset = offset + 8 + size
            };
        }

        private static bool FourCcEquals(byte[] b, string s)
        {
            return b[0] == (byte)s[0] && b[1] == (byte)s[1] && b[2] == (byte)s[2] && b[3] == (byte)s[3];
        }

        private static void WriteVorbisPacketHeader(BitWriter os, byte type)
        {
            os.Write(type, 8);
            byte[] vorbis = { (byte)'v', (byte)'o', (byte)'r', (byte)'b', (byte)'i', (byte)'s' };
            for (int i = 0; i < 6; i++)
            {
                os.Write(vorbis[i], 8);
            }
        }

        public WwiseRiffVorbis(
            string name,
            string codebooksName,
            bool inlineCodebooks,
            bool fullSetup,
            ForcePacketFormat forcePacketFormat)
            : this(ReadFileOrThrow(name), name, codebooksName, inlineCodebooks, fullSetup, forcePacketFormat)
        {
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

        // Optional preloaded codebook library (thread-safe read-only reuse, e.g. in a web server)
        private readonly CodebookLibrary _sharedCodebooks;

        public WwiseRiffVorbis(
            byte[] inputData,
            string name,
            string codebooksName,
            bool inlineCodebooks,
            bool fullSetup,
            ForcePacketFormat forcePacketFormat,
            CodebookLibrary sharedCodebooks = null)
        {
            _sharedCodebooks = sharedCodebooks;
            _fileName = name;
            _codebooksName = codebooksName;
            _fileSize = -1;
            _littleEndian = true;
            _riffSize = -1;
            _fmtOffset = -1;
            _cueOffset = -1;
            _listOffset = -1;
            _smplOffset = -1;
            _vorbOffset = -1;
            _dataOffset = -1;
            _fmtSize = -1;
            _cueSize = -1;
            _listSize = -1;
            _smplSize = -1;
            _vorbSize = -1;
            _dataSize = -1;

            _infile = new MemoryStream(inputData, false);

            _inlineCodebooks = inlineCodebooks;
            _fullSetup = fullSetup;

            _fileSize = _infile.Length;

            // check RIFF header
            {
                byte[] riffHead = new byte[4];
                byte[] waveHead = new byte[4];
                _infile.Position = 0;
                _infile.ReadExactly(riffHead);

                if (FourCcEquals(riffHead, "RIFX"))
                {
                    _littleEndian = false;
                }
                else if (FourCcEquals(riffHead, "RIFF"))
                {
                    _littleEndian = true;
                }
                else
                {
                    throw new ParseErrorStr("missing RIFF");
                }

                _riffSize = (long)Read32() + 8;

                if (_riffSize > _fileSize) throw new ParseErrorStr("RIFF truncated");

                _infile.ReadExactly(waveHead);
                if (!FourCcEquals(waveHead, "WAVE")) throw new ParseErrorStr("missing WAVE");
            }

            // read chunks
            long chunkOffset = 12;
            while (chunkOffset < _riffSize)
            {
                _infile.Position = chunkOffset;

                if (chunkOffset + 8 > _riffSize) throw new ParseErrorStr("chunk header truncated");

                byte[] chunkType = new byte[4];
                _infile.ReadExactly(chunkType);
                uint chunkSize = Read32();

                if (FourCcEquals(chunkType, "fmt "))
                {
                    _fmtOffset = chunkOffset + 8;
                    _fmtSize = chunkSize;
                }
                else if (FourCcEquals(chunkType, "cue "))
                {
                    _cueOffset = chunkOffset + 8;
                    _cueSize = chunkSize;
                }
                else if (FourCcEquals(chunkType, "LIST"))
                {
                    _listOffset = chunkOffset + 8;
                    _listSize = chunkSize;
                }
                else if (FourCcEquals(chunkType, "smpl"))
                {
                    _smplOffset = chunkOffset + 8;
                    _smplSize = chunkSize;
                }
                else if (FourCcEquals(chunkType, "vorb"))
                {
                    _vorbOffset = chunkOffset + 8;
                    _vorbSize = chunkSize;
                }
                else if (FourCcEquals(chunkType, "data"))
                {
                    _dataOffset = chunkOffset + 8;
                    _dataSize = chunkSize;
                }

                chunkOffset = chunkOffset + 8 + chunkSize;
            }

            if (chunkOffset > _riffSize) throw new ParseErrorStr("chunk truncated");

            // check that we have the chunks we're expecting
            if (-1 == _fmtOffset && -1 == _dataOffset) throw new ParseErrorStr("expected fmt, data chunks");

            // read fmt
            if (-1 == _vorbOffset && 0x42 != _fmtSize) throw new ParseErrorStr("expected 0x42 fmt if vorb missing");

            if (-1 != _vorbOffset && 0x28 != _fmtSize && 0x18 != _fmtSize && 0x12 != _fmtSize) throw new ParseErrorStr("bad fmt size");

            if (-1 == _vorbOffset && 0x42 == _fmtSize)
            {
                // fake it out
                _vorbOffset = _fmtOffset + 0x18;
            }

            _infile.Position = _fmtOffset;
            if ((ushort)0xFFFF != Read16()) throw new ParseErrorStr("bad codec id");
            _channels = Read16();
            _sampleRate = Read32();
            _avgBytesPerSecond = Read32();
            if (0U != Read16()) throw new ParseErrorStr("bad block align");
            if (0U != Read16()) throw new ParseErrorStr("expected 0 bps");
            if (_fmtSize - 0x12 != Read16()) throw new ParseErrorStr("bad extra fmt length");

            if (_fmtSize - 0x12 >= 2)
            {
                // read extra fmt
                _extUnk = Read16();
                if (_fmtSize - 0x12 >= 6)
                {
                    _subtype = Read32();
                }
            }

            if (_fmtSize == 0x28)
            {
                byte[] whoknowsbuf = new byte[16];
                byte[] whoknowsbufCheck = { 1, 0, 0, 0, 0, 0, 0x10, 0, 0x80, 0, 0, 0xAA, 0, 0x38, 0x9b, 0x71 };
                _infile.ReadExactly(whoknowsbuf);
                for (int i = 0; i < 16; i++)
                {
                    if (whoknowsbuf[i] != whoknowsbufCheck[i]) throw new ParseErrorStr("expected signature in extra fmt?");
                }
            }

            // read cue
            if (-1 != _cueOffset)
            {
                _infile.Position = _cueOffset;
                _cueCount = Read32();
            }

            // read smpl
            if (-1 != _smplOffset)
            {
                _infile.Position = _smplOffset + 0x1C;
                _loopCount = Read32();

                if (1 != _loopCount) throw new ParseErrorStr("expected one loop");

                _infile.Position = _smplOffset + 0x2c;
                _loopStart = Read32();
                _loopEnd = Read32();
            }

            // read vorb
            switch (_vorbSize)
            {
                case -1:
                case 0x28:
                case 0x2A:
                case 0x32:
                case 0x34:
                    _infile.Position = _vorbOffset + 0x00;
                    break;

                default:
                    throw new ParseErrorStr("bad vorb size");
            }

            _sampleCount = Read32();

            switch (_vorbSize)
            {
                case -1:
                case 0x2A:
                {
                    _noGranule = true;

                    _infile.Position = _vorbOffset + 0x4;
                    uint modSignal = Read32();

                    // seems to be 0xD9 when _mod_packets should be set
                    // also seen 0xCB, 0xBC, 0xB2
                    if (0x4A != modSignal && 0x4B != modSignal && 0x69 != modSignal && 0x70 != modSignal)
                    {
                        _modPackets = true;
                    }
                    _infile.Position = _vorbOffset + 0x10;
                    break;
                }

                default:
                    _infile.Position = _vorbOffset + 0x18;
                    break;
            }

            if (forcePacketFormat == ForcePacketFormat.ForceNoModPackets)
            {
                _modPackets = false;
            }
            else if (forcePacketFormat == ForcePacketFormat.ForceModPackets)
            {
                _modPackets = true;
            }

            _setupPacketOffset = Read32();
            _firstAudioPacketOffset = Read32();

            switch (_vorbSize)
            {
                case -1:
                case 0x2A:
                    _infile.Position = _vorbOffset + 0x24;
                    break;

                case 0x32:
                case 0x34:
                    _infile.Position = _vorbOffset + 0x2C;
                    break;
            }

            switch (_vorbSize)
            {
                case 0x28:
                case 0x2C:
                    // ok to leave _uid, _blocksize0Pow and _blocksize1Pow unset
                    _headerTriadPresent = true;
                    _oldPacketHeaders = true;
                    break;

                case -1:
                case 0x2A:
                case 0x32:
                case 0x34:
                    _uid = Read32();
                    int b0 = _infile.ReadByte();
                    int b1 = _infile.ReadByte();
                    if (b0 < 0 || b1 < 0) throw new EndOfStreamException();
                    _blocksize0Pow = (byte)b0;
                    _blocksize1Pow = (byte)b1;
                    break;
            }

            // check/set loops now that we know total sample count
            if (0 != _loopCount)
            {
                if (_loopEnd == 0)
                {
                    _loopEnd = _sampleCount;
                }
                else
                {
                    _loopEnd = _loopEnd + 1;
                }

                if (_loopStart >= _sampleCount || _loopEnd > _sampleCount || _loopStart > _loopEnd)
                    throw new ParseErrorStr("loops out of range");
            }
        }

        public void PrintInfo()
        {
            Console.Write(_littleEndian ? "RIFF WAVE" : "RIFX WAVE");
            Console.Write(" " + _channels + " channel");
            if (_channels != 1) Console.Write("s");
            Console.WriteLine(" " + _sampleRate + " Hz " + (_avgBytesPerSecond * 8) + " bps");
            Console.WriteLine(_sampleCount + " samples");

            if (0 != _loopCount)
            {
                Console.WriteLine("loop from " + _loopStart + " to " + _loopEnd);
            }

            if (_oldPacketHeaders)
            {
                Console.WriteLine("- 8 byte (old) packet headers");
            }
            else if (_noGranule)
            {
                Console.WriteLine("- 2 byte packet headers, no granule");
            }
            else
            {
                Console.WriteLine("- 6 byte packet headers");
            }

            if (_headerTriadPresent)
            {
                Console.WriteLine("- Vorbis header triad present");
            }

            if (_fullSetup || _headerTriadPresent)
            {
                Console.WriteLine("- full setup header");
            }
            else
            {
                Console.WriteLine("- stripped setup header");
            }

            if (_inlineCodebooks || _headerTriadPresent)
            {
                Console.WriteLine("- inline codebooks");
            }
            else
            {
                Console.WriteLine("- external codebooks (" + _codebooksName + ")");
            }

            if (_modPackets)
            {
                Console.WriteLine("- modified Vorbis packets");
            }
            else
            {
                Console.WriteLine("- standard Vorbis packets");
            }
        }

        public void GenerateOgg(Stream of)
        {
            using (BitWriter os = new BitWriter(of))
            {
                bool[] modeBlockflag = null;
                int modeBits = 0;
                bool prevBlockflag = false;

                if (_headerTriadPresent)
                {
                    GenerateOggHeaderWithTriad(os);
                }
                else
                {
                    GenerateOggHeader(os, ref modeBlockflag, ref modeBits);
                }

                // Audio pages
                {
                    long offset = _dataOffset + _firstAudioPacketOffset;
                    long endOffset = _dataOffset + _dataSize;

                    // Files with 2-byte packet headers ("no granule") carry no packet
                    // positions, which would leave every Ogg page's granule position at 0
                    // and players could not determine the duration. Derive the positions
                    // from the Vorbis block sizes (the same algorithm revorb uses); the
                    // final page is clamped to the sample count from the RIFF header.
                    uint computedGranule = 0;
                    int prevBlocksize = 0;

                    while (offset < endOffset)
                    {
                        uint size, granule;
                        long packetHeaderSize, payloadOffset, nextOffset;
                        int curBlocksize = 0;

                        PacketHeader audioPacket;
                        if (_oldPacketHeaders)
                        {
                            audioPacket = ReadPacket8(offset);
                        }
                        else
                        {
                            audioPacket = ReadPacket(offset, _noGranule);
                        }
                        packetHeaderSize = audioPacket.HeaderSize;
                        size = audioPacket.Size;
                        payloadOffset = audioPacket.PayloadOffset;
                        granule = audioPacket.Granule;
                        nextOffset = audioPacket.NextOffset;

                        if (offset + packetHeaderSize > endOffset)
                        {
                            throw new ParseErrorStr("page header truncated");
                        }

                        offset = payloadOffset;

                        _infile.Position = offset;
                        // HACK: don't know what to do here
                        if (granule == 0xFFFFFFFFU)
                        {
                            os.SetGranule(1);
                        }
                        else
                        {
                            os.SetGranule(granule);
                        }

                        // first byte
                        if (_modPackets)
                        {
                            // need to rebuild packet type and window info

                            if (modeBlockflag == null)
                            {
                                throw new ParseErrorStr("didn't load mode_blockflag");
                            }

                            // OUT: 1 bit packet type (0 == audio)
                            os.Write(0, 1);

                            uint modeNumber;
                            uint remainder;

                            {
                                // collect mode number from first byte

                                BitReader ss = new BitReader(_infile);

                                // IN/OUT: N bit mode number (max 6 bits)
                                modeNumber = ss.Read(modeBits);
                                os.Write(modeNumber, modeBits);

                                // IN: remaining bits of first (input) byte
                                remainder = ss.Read(8 - modeBits);
                            }

                            if (modeBlockflag[modeNumber])
                            {
                                // long window, peek at next frame

                                _infile.Position = nextOffset;
                                bool nextBlockflag = false;
                                if (nextOffset + packetHeaderSize <= endOffset)
                                {
                                    // mod_packets always goes with 6-byte headers
                                    PacketHeader nextAudioPacket = ReadPacket(nextOffset, _noGranule);
                                    uint nextPacketSize = nextAudioPacket.Size;
                                    if (nextPacketSize > 0)
                                    {
                                        _infile.Position = nextAudioPacket.PayloadOffset;

                                        BitReader ss = new BitReader(_infile);
                                        uint nextModeNumber = ss.Read(modeBits);

                                        nextBlockflag = modeBlockflag[nextModeNumber];
                                    }
                                }

                                // OUT: previous window type bit
                                os.Write(prevBlockflag ? 1U : 0U, 1);

                                // OUT: next window type bit
                                os.Write(nextBlockflag ? 1U : 0U, 1);

                                // fix seek for rest of stream
                                _infile.Position = offset + 1;
                            }

                            prevBlockflag = modeBlockflag[modeNumber];
                            curBlocksize = 1 << (modeBlockflag[modeNumber] ? _blocksize1Pow : _blocksize0Pow);

                            // OUT: remaining bits of first (input) byte
                            os.Write(remainder, 8 - modeBits);
                        }
                        else
                        {
                            // nothing unusual for first byte
                            int v = _infile.ReadByte();
                            if (v < 0)
                            {
                                throw new ParseErrorStr("file truncated");
                            }

                            // for granule position computation on "no granule" streams
                            if (modeBlockflag != null && modeBits > 0)
                            {
                                uint modeNumber = ((uint)v >> 1) & ((1U << modeBits) - 1U);
                                curBlocksize = 1 << (modeBlockflag[modeNumber] ? _blocksize1Pow : _blocksize0Pow);
                            }

                            os.Write((uint)v, 8);
                        }

                        // remainder of packet
                        for (uint i = 1; i < size; i++)
                        {
                            int v = _infile.ReadByte();
                            if (v < 0)
                            {
                                throw new ParseErrorStr("file truncated");
                            }
                            os.Write((uint)v, 8);
                        }

                        offset = nextOffset;

                        if (_noGranule && !_oldPacketHeaders && curBlocksize > 0)
                        {
                            // running granule before this block was added (equals the
                            // granule position of the previously flushed page)
                            uint prevGranule = computedGranule;

                            if (prevBlocksize == 0)
                                computedGranule += (uint)curBlocksize / 4U;
                            else
                                computedGranule += (uint)(prevBlocksize + curBlocksize) / 4U;
                            prevBlocksize = curBlocksize;

                            // The RIFF sample count is the precise stream length and
                            // normally matches the accumulated block sizes. Some files
                            // (e.g. trimmed loops) report a value that falls *before*
                            // the previous page's position; adopting it would make the
                            // final granule regress, which Ogg decoders reject. Only
                            // refine the final position when it stays non-regressing and
                            // does not run past the last block's end.
                            if (offset == endOffset && _sampleCount != 0)
                            {
                                uint reported = (uint)_sampleCount;
                                if (reported >= prevGranule && reported <= computedGranule)
                                {
                                    computedGranule = reported;
                                }
                            }

                            os.SetGranule(computedGranule);
                        }

                        os.FlushPage(false, offset == endOffset);
                    }
                    if (offset > endOffset) throw new ParseErrorStr("page truncated");
                }
            }
        }

        private void GenerateOggHeader(BitWriter os, ref bool[] modeBlockflag, ref int modeBits)
        {
            // generate identification packet
            {
                WriteVorbisPacketHeader(os, 1);

                os.Write(0, 32);                    // version
                os.Write(_channels, 8);
                os.Write(_sampleRate, 32);

                os.Write(0, 32);                    // bitrate_max
                os.Write(_avgBytesPerSecond * 8, 32); // bitrate_nominal
                os.Write(0, 32);                    // bitrate_minimum

                os.Write(_blocksize0Pow, 4);
                os.Write(_blocksize1Pow, 4);

                os.Write(1, 1);                     // framing

                // identification packet on its own page
                os.FlushPage();
            }

            // generate comment packet
            {
                WriteVorbisPacketHeader(os, 3);

                string vendor = "converted from Audiokinetic Wwise by ww2ogg " + AppVersion.Current;
                byte[] vendorBytes = Encoding.ASCII.GetBytes(vendor);
                os.Write((uint)vendorBytes.Length, 32);
                for (int i = 0; i < vendorBytes.Length; i++)
                {
                    os.Write(vendorBytes[i], 8);
                }

                if (0 == _loopCount)
                {
                    // no user comments
                    os.Write(0, 32);
                }
                else
                {
                    // two comments, loop start and end
                    os.Write(2, 32);

                    byte[] loopStart = Encoding.ASCII.GetBytes("LoopStart=" + _loopStart);
                    os.Write((uint)loopStart.Length, 32);
                    for (int i = 0; i < loopStart.Length; i++)
                    {
                        os.Write(loopStart[i], 8);
                    }

                    byte[] loopEnd = Encoding.ASCII.GetBytes("LoopEnd=" + _loopEnd);
                    os.Write((uint)loopEnd.Length, 32);
                    for (int i = 0; i < loopEnd.Length; i++)
                    {
                        os.Write(loopEnd[i], 8);
                    }
                }

                os.Write(1, 1); // framing

                os.FlushPage();
            }

            // generate setup packet
            {
                WriteVorbisPacketHeader(os, 5);

                PacketHeader setupPacket = ReadPacket(_dataOffset + _setupPacketOffset, _noGranule);

                _infile.Position = setupPacket.PayloadOffset;
                if (setupPacket.Granule != 0) throw new ParseErrorStr("setup packet granule != 0");
                BitReader ss = new BitReader(_infile);

                // codebook count
                uint codebookCountLess1 = ss.Read(8);
                uint codebookCount = codebookCountLess1 + 1;
                os.Write(codebookCountLess1, 8);

                // rebuild codebooks
                if (_inlineCodebooks)
                {
                    CodebookLibrary cbl = new CodebookLibrary();

                    for (uint i = 0; i < codebookCount; i++)
                    {
                        if (_fullSetup)
                        {
                            cbl.Copy(ss, os);
                        }
                        else
                        {
                            cbl.Rebuild(ss, 0, os);
                        }
                    }
                }
                else
                {
                    /* external codebooks */

                    CodebookLibrary cbl = _sharedCodebooks ?? new CodebookLibrary(_codebooksName);

                    for (uint i = 0; i < codebookCount; i++)
                    {
                        uint codebookId = ss.Read(10);
                        try
                        {
                            cbl.Rebuild((int)codebookId, os);
                        }
                        catch (InvalidId)
                        {
                            if (codebookId == 0x342)
                            {
                                uint codebookIdentifier = ss.Read(14);

                                if (codebookIdentifier == 0x1590)
                                {
                                    // starts with BCV, probably --full-setup
                                    throw new ParseErrorStr(
                                        "invalid codebook id 0x342, try --full-setup");
                                }
                            }

                            // just an invalid codebook
                            throw;
                        }
                    }
                }

                // Time Domain transforms (placeholder)
                os.Write(0, 6);   // time_count_less1
                os.Write(0, 16);  // dummy time value

                if (_fullSetup)
                {
                    while (ss.TotalBitsRead < setupPacket.Size * 8U)
                    {
                        uint bit = ss.Read(1);
                        os.Write(bit, 1);
                    }
                }
                else
                {
                    // floor count
                    uint floorCountLess1 = ss.Read(6);
                    uint floorCount = floorCountLess1 + 1;
                    os.Write(floorCountLess1, 6);

                    // rebuild floors
                    for (uint i = 0; i < floorCount; i++)
                    {
                        // Always floor type 1
                        os.Write(1, 16);

                        uint floor1Partitions = ss.Read(5);
                        os.Write(floor1Partitions, 5);

                        uint[] floor1PartitionClassList = new uint[floor1Partitions];

                        uint maximumClass = 0;
                        for (uint j = 0; j < floor1Partitions; j++)
                        {
                            uint floor1PartitionClass = ss.Read(4);
                            os.Write(floor1PartitionClass, 4);

                            floor1PartitionClassList[j] = floor1PartitionClass;

                            if (floor1PartitionClass > maximumClass)
                                maximumClass = floor1PartitionClass;
                        }

                        uint[] floor1ClassDimensionsList = new uint[maximumClass + 1];

                        for (uint j = 0; j <= maximumClass; j++)
                        {
                            uint classDimensionsLess1 = ss.Read(3);
                            os.Write(classDimensionsLess1, 3);

                            floor1ClassDimensionsList[j] = classDimensionsLess1 + 1;

                            uint classSubclasses = ss.Read(2);
                            os.Write(classSubclasses, 2);

                            if (0 != classSubclasses)
                            {
                                uint masterbook = ss.Read(8);
                                os.Write(masterbook, 8);

                                if (masterbook >= codebookCount)
                                    throw new ParseErrorStr("invalid floor1 masterbook");
                            }

                            for (uint k = 0; k < (1U << (int)classSubclasses); k++)
                            {
                                uint subclassBookPlus1 = ss.Read(8);
                                os.Write(subclassBookPlus1, 8);

                                int subclassBook = (int)subclassBookPlus1 - 1;
                                if (subclassBook >= 0 && (uint)subclassBook >= codebookCount)
                                    throw new ParseErrorStr("invalid floor1 subclass book");
                            }
                        }

                        uint floor1MultiplierLess1 = ss.Read(2);
                        os.Write(floor1MultiplierLess1, 2);

                        uint rangebits = ss.Read(4);
                        os.Write(rangebits, 4);

                        for (uint j = 0; j < floor1Partitions; j++)
                        {
                            uint currentClassNumber = floor1PartitionClassList[j];
                            for (uint k = 0; k < floor1ClassDimensionsList[currentClassNumber]; k++)
                            {
                                uint x = ss.Read((int)rangebits);
                                os.Write(x, (int)rangebits);
                            }
                        }
                    }

                    // residue count
                    uint residueCountLess1 = ss.Read(6);
                    uint residueCount = residueCountLess1 + 1;
                    os.Write(residueCountLess1, 6);

                    // rebuild residues
                    for (uint i = 0; i < residueCount; i++)
                    {
                        uint residueType = ss.Read(2);
                        os.Write(residueType, 16);

                        if (residueType > 2) throw new ParseErrorStr("invalid residue type");

                        uint residueBegin = ss.Read(24);
                        uint residueEnd = ss.Read(24);
                        uint residuePartitionSizeLess1 = ss.Read(24);
                        uint residueClassificationsLess1 = ss.Read(6);
                        uint residueClassbook = ss.Read(8);
                        uint residueClassifications = residueClassificationsLess1 + 1;
                        os.Write(residueBegin, 24);
                        os.Write(residueEnd, 24);
                        os.Write(residuePartitionSizeLess1, 24);
                        os.Write(residueClassificationsLess1, 6);
                        os.Write(residueClassbook, 8);

                        if (residueClassbook >= codebookCount) throw new ParseErrorStr("invalid residue classbook");

                        uint[] residueCascade = new uint[residueClassifications];

                        for (uint j = 0; j < residueClassifications; j++)
                        {
                            uint highBits = 0;
                            uint lowBits = ss.Read(3);
                            os.Write(lowBits, 3);

                            uint bitflag = ss.Read(1);
                            os.Write(bitflag, 1);
                            if (bitflag != 0)
                            {
                                highBits = ss.Read(5);
                                os.Write(highBits, 5);
                            }

                            residueCascade[j] = highBits * 8 + lowBits;
                        }

                        for (uint j = 0; j < residueClassifications; j++)
                        {
                            for (uint k = 0; k < 8; k++)
                            {
                                if ((residueCascade[j] & (1 << (int)k)) != 0)
                                {
                                    uint residueBook = ss.Read(8);
                                    os.Write(residueBook, 8);

                                    if (residueBook >= codebookCount) throw new ParseErrorStr("invalid residue book");
                                }
                            }
                        }
                    }

                    // mapping count
                    uint mappingCountLess1 = ss.Read(6);
                    uint mappingCount = mappingCountLess1 + 1;
                    os.Write(mappingCountLess1, 6);

                    for (uint i = 0; i < mappingCount; i++)
                    {
                        // always mapping type 0, the only one
                        os.Write(0, 16);

                        uint submapsFlag = ss.Read(1);
                        os.Write(submapsFlag, 1);

                        uint submaps = 1;
                        if (submapsFlag != 0)
                        {
                            uint submapsLess1 = ss.Read(4);
                            submaps = submapsLess1 + 1;
                            os.Write(submapsLess1, 4);
                        }

                        uint squarePolarFlag = ss.Read(1);
                        os.Write(squarePolarFlag, 1);

                        if (squarePolarFlag != 0)
                        {
                            uint couplingStepsLess1 = ss.Read(8);
                            uint couplingSteps = couplingStepsLess1 + 1;
                            os.Write(couplingStepsLess1, 8);

                            int pairBits = CodebookLibrary.ILog((uint)_channels - 1U);
                            for (uint j = 0; j < couplingSteps; j++)
                            {
                                uint magnitude = ss.Read(pairBits);
                                uint angle = ss.Read(pairBits);
                                os.Write(magnitude, pairBits);
                                os.Write(angle, pairBits);

                                if (angle == magnitude || magnitude >= _channels || angle >= _channels) throw new ParseErrorStr("invalid coupling");
                            }
                        }

                        // a rare reserved field not removed by Ak!
                        uint mappingReserved = ss.Read(2);
                        os.Write(mappingReserved, 2);
                        if (0 != mappingReserved) throw new ParseErrorStr("mapping reserved field nonzero");

                        if (submaps > 1)
                        {
                            for (uint j = 0; j < _channels; j++)
                            {
                                uint mappingMux = ss.Read(4);
                                os.Write(mappingMux, 4);

                                if (mappingMux >= submaps) throw new ParseErrorStr("mapping_mux >= submaps");
                            }
                        }

                        for (uint j = 0; j < submaps; j++)
                        {
                            // Another! Unused time domain transform configuration placeholder!
                            uint timeConfig = ss.Read(8);
                            os.Write(timeConfig, 8);

                            uint floorNumber = ss.Read(8);
                            os.Write(floorNumber, 8);
                            if (floorNumber >= floorCount) throw new ParseErrorStr("invalid floor mapping");

                            uint residueNumber = ss.Read(8);
                            os.Write(residueNumber, 8);
                            if (residueNumber >= residueCount) throw new ParseErrorStr("invalid residue mapping");
                        }
                    }

                    // mode count
                    uint modeCountLess1 = ss.Read(6);
                    uint modeCount = modeCountLess1 + 1;
                    os.Write(modeCountLess1, 6);

                    modeBlockflag = new bool[modeCount];
                    modeBits = CodebookLibrary.ILog(modeCount - 1);

                    for (uint i = 0; i < modeCount; i++)
                    {
                        uint blockFlag = ss.Read(1);
                        os.Write(blockFlag, 1);

                        modeBlockflag[i] = (blockFlag != 0);

                        // only 0 valid for windowtype and transformtype
                        os.Write(0, 16); // windowtype
                        os.Write(0, 16); // transformtype

                        uint mapping = ss.Read(8);
                        os.Write(mapping, 8);
                        if (mapping >= mappingCount) throw new ParseErrorStr("invalid mode mapping");
                    }

                    os.Write(1, 1); // framing
                }

                os.FlushPage();

                if ((ss.TotalBitsRead + 7) / 8 != setupPacket.Size) throw new ParseErrorStr("didn't read exactly setup packet");

                if (setupPacket.NextOffset != _dataOffset + (long)_firstAudioPacketOffset) throw new ParseErrorStr("first audio packet doesn't follow setup packet");
            }
        }

        private void GenerateOggHeaderWithTriad(BitWriter os)
        {
            // Header page triad
            {
                long offset = _dataOffset + _setupPacketOffset;

                // copy information packet
                {
                    PacketHeader informationPacket = ReadPacket8(offset);
                    uint size = informationPacket.Size;

                    if (informationPacket.Granule != 0)
                    {
                        throw new ParseErrorStr("information packet granule != 0");
                    }

                    _infile.Position = informationPacket.PayloadOffset;

                    int first = _infile.ReadByte();
                    if (1 != first)
                    {
                        throw new ParseErrorStr("wrong type for information packet");
                    }
                    os.Write((uint)first, 8);

                    for (uint i = 1; i < size; i++)
                    {
                        int c = _infile.ReadByte();
                        os.Write((uint)c, 8);
                    }

                    // identification packet on its own page
                    os.FlushPage();

                    offset = informationPacket.NextOffset;
                }

                // copy comment packet
                {
                    PacketHeader commentPacket = ReadPacket8(offset);
                    uint size = commentPacket.Size;

                    if (commentPacket.Granule != 0)
                    {
                        throw new ParseErrorStr("comment packet granule != 0");
                    }

                    _infile.Position = commentPacket.PayloadOffset;

                    int first = _infile.ReadByte();
                    if (3 != first)
                    {
                        throw new ParseErrorStr("wrong type for comment packet");
                    }
                    os.Write((uint)first, 8);

                    for (uint i = 1; i < size; i++)
                    {
                        int c = _infile.ReadByte();
                        os.Write((uint)c, 8);
                    }

                    // identification packet on its own page
                    os.FlushPage();

                    offset = commentPacket.NextOffset;
                }

                // copy setup packet
                {
                    PacketHeader setupPacket = ReadPacket8(offset);

                    _infile.Position = setupPacket.PayloadOffset;
                    if (setupPacket.Granule != 0) throw new ParseErrorStr("setup packet granule != 0");
                    BitReader ss = new BitReader(_infile);

                    uint c = ss.Read(8);

                    // type
                    if (5 != c)
                    {
                        throw new ParseErrorStr("wrong type for setup packet");
                    }
                    os.Write(c, 8);

                    // 'vorbis'
                    for (int i = 0; i < 6; i++)
                    {
                        c = ss.Read(8);
                        os.Write(c, 8);
                    }

                    // codebook count
                    uint codebookCountLess1 = ss.Read(8);
                    uint codebookCount = codebookCountLess1 + 1;
                    os.Write(codebookCountLess1, 8);

                    CodebookLibrary cbl = new CodebookLibrary();

                    // rebuild codebooks
                    for (uint i = 0; i < codebookCount; i++)
                    {
                        cbl.Copy(ss, os);
                    }

                    while (ss.TotalBitsRead < setupPacket.Size * 8U)
                    {
                        uint bit = ss.Read(1);
                        os.Write(bit, 1);
                    }

                    os.FlushPage();

                    offset = setupPacket.NextOffset;
                }

                if (offset != _dataOffset + (long)_firstAudioPacketOffset) throw new ParseErrorStr("first audio packet doesn't follow setup packet");
            }
        }
    }
}
