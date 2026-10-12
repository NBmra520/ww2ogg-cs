using System;
using System.Collections.Generic;
using System.IO;

namespace ww2ogg
{
    internal static class Program
    {
        private sealed class Options
        {
            public string InFilename;
            public string OutFilename;
            public string CodebooksFilename = "packed_codebooks_aoTuV_603.bin";
            public bool InlineCodebooks;
            public bool FullSetup;
            public bool Mp3;
            public bool Wav;
            public bool OutputSet;
            public bool ListOnly;
            public ForcePacketFormat ForcePacketFormat = ForcePacketFormat.NoForcePacketFormat;

            public void ParseArgs(string[] argv)
            {
                bool setInput = false, setOutput = false;
                for (int i = 0; i < argv.Length; i++)
                {
                    string arg = argv[i];

                    if (arg == "-o")
                    {
                        // switch for output file name
                        if (i + 1 >= argv.Length)
                        {
                            throw new ArgumentError("-o needs an option");
                        }

                        if (setOutput)
                        {
                            throw new ArgumentError("only one output file at a time");
                        }

                        OutFilename = argv[++i];
                        setOutput = true;
                        OutputSet = true;
                    }
                    else if (arg == "--mp3")
                    {
                        // with .pck/.bnk input: write MP3 files instead of Ogg
                        if (Wav)
                        {
                            throw new ArgumentError("only one of --mp3 or --wav is allowed");
                        }

                        Mp3 = true;
                    }
                    else if (arg == "--wav")
                    {
                        // with .pck/.bnk input: write WAV files instead of Ogg
                        if (Mp3)
                        {
                            throw new ArgumentError("only one of --mp3 or --wav is allowed");
                        }

                        Wav = true;
                    }
                    else if (arg == "--list")
                    {
                        // with .pck/.bnk input: list embedded WEMs and exit
                        ListOnly = true;
                    }
                    else if (arg == "--inline-codebooks")
                    {
                        // switch for inline codebooks
                        InlineCodebooks = true;
                    }
                    else if (arg == "--full-setup")
                    {
                        // early version with setup almost entirely intact
                        FullSetup = true;
                        InlineCodebooks = true;
                    }
                    else if (arg == "--mod-packets" || arg == "--no-mod-packets")
                    {
                        if (ForcePacketFormat != ForcePacketFormat.NoForcePacketFormat)
                        {
                            throw new ArgumentError("only one of --mod-packets or --no-mod-packets is allowed");
                        }

                        ForcePacketFormat = (arg == "--mod-packets")
                            ? ForcePacketFormat.ForceModPackets
                            : ForcePacketFormat.ForceNoModPackets;
                    }
                    else if (arg == "--pcb")
                    {
                        // override default packed codebooks file
                        if (i + 1 >= argv.Length)
                        {
                            throw new ArgumentError("--pcb needs an option");
                        }

                        CodebooksFilename = argv[++i];
                    }
                    else
                    {
                        // assume anything else is an input file name
                        if (setInput)
                        {
                            throw new ArgumentError("only one input file at a time");
                        }

                        InFilename = arg;
                        setInput = true;
                    }
                }

                if (!setInput)
                {
                    throw new ArgumentError("input name not specified");
                }

                if (!setOutput)
                {
                    int found = InFilename.LastIndexOf('.');

                    OutFilename = found < 0 ? InFilename : InFilename.Substring(0, found);
                    string defaultExt = Wav ? ".wav" : (Mp3 ? ".mp3" : ".ogg");
                    OutFilename += defaultExt;

                    if (OutFilename == InFilename)
                    {
                        OutFilename += "_conv" + defaultExt;
                    }
                }
            }
        }

        private static void Usage()
        {
            Console.WriteLine();
            Console.WriteLine("usage: ww2ogg input.wem [-o output.ogg|output.opus|output.mp3|output.wav]");
            Console.WriteLine("                        [--inline-codebooks] [--full-setup]");
            Console.WriteLine("                        [--mod-packets | --no-mod-packets]");
            Console.WriteLine("                        [--pcb packed_codebooks_aoTuV_603.bin]");
            Console.WriteLine("       ww2ogg input.pck|input.bnk [-o outdir] [--mp3|--wav] [--list] [other options above]");
            Console.WriteLine("         (containers are extracted; Vorbis WEM -> .ogg, Opus WEM -> .opus; --mp3/--wav decodes)");
            Console.WriteLine();
        }

        private enum InputKind
        {
            Wem,
            Container
        }

        private static int Main(string[] args)
        {
            Console.WriteLine("Audiokinetic Wwise RIFF/RIFX Vorbis to Ogg Vorbis converter "
                + AppVersion.Current + " by hcs");
            Console.WriteLine();

            Options opt = new Options();

            try
            {
                opt.ParseArgs(args);
            }
            catch (ArgumentError ae)
            {
                Console.WriteLine(ae.Message);
                Usage();
                return 1;
            }

            InputKind kind;
            try
            {
                kind = PeekKind(opt.InFilename);
            }
            catch (FileOpenError fe)
            {
                Console.WriteLine(fe.Message);
                return 1;
            }

            if (kind == InputKind.Container)
            {
                return RunContainer(opt);
            }

            byte[] wemData;
            try
            {
                wemData = File.ReadAllBytes(opt.InFilename);
            }
            catch (Exception)
            {
                Console.WriteLine(new FileOpenError(opt.InFilename).Message);
                return 1;
            }

            bool isOpus = WwiseRiffOpus.IsOpusWem(wemData);

            // The codec is known now, so the default output name can use .opus
            // for Opus WEMs instead of always .ogg.
            if (!opt.OutputSet)
            {
                string defaultExt = DefaultExt(opt, isOpus);
                int found = opt.InFilename.LastIndexOf('.');
                opt.OutFilename = (found < 0
                    ? opt.InFilename
                    : opt.InFilename.Substring(0, found)) + defaultExt;

                if (opt.OutFilename == opt.InFilename)
                {
                    opt.OutFilename += "_conv" + defaultExt;
                }
            }

            try
            {
                Console.WriteLine("Input: " + opt.InFilename);
                Console.WriteLine("Output: " + opt.OutFilename);

                CodebookLibrary shared =
                    (!isOpus && !opt.InlineCodebooks)
                        ? new CodebookLibrary(opt.CodebooksFilename)
                        : null;

                ConvertWem(wemData, opt.InFilename, opt.OutFilename, opt, shared, true);

                Console.WriteLine("Done!");
                Console.WriteLine();
            }
            catch (FileOpenError fe)
            {
                Console.WriteLine(fe.Message);
                return 1;
            }
            catch (ParseError pe)
            {
                Console.WriteLine(pe.Message);
                return 1;
            }

            return 0;
        }

        private static string DefaultExt(Options opt, bool isOpus)
        {
            if (opt.Wav) return ".wav";
            if (opt.Mp3) return ".mp3";
            return isOpus ? ".opus" : ".ogg";
        }

        private static bool IsWavTarget(string path)
        {
            return path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMp3Target(string path)
        {
            return path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
        }

        // Converts one standalone or embedded WEM to the requested output:
        //  - Opus WEM: the embedded Ogg Opus stream is written verbatim as
        //    .opus/.ogg, or decoded with Concentus for .wav/.mp3
        //  - Vorbis WEM: original ww2ogg path (Ogg Vorbis, then NVorbis decode
        //    for .wav/.mp3)
        private static void ConvertWem(byte[] wemData, string wemName, string outPath,
            Options opt, CodebookLibrary shared, bool printInfo)
        {
            if (WwiseRiffOpus.IsOpusWem(wemData))
            {
                WwiseRiffOpus opus = new WwiseRiffOpus(wemData, wemName);
                if (printInfo) opus.PrintInfo();

                if (IsWavTarget(outPath) || IsMp3Target(outPath))
                {
                    if (printInfo)
                    {
                        Console.WriteLine(IsMp3Target(outPath)
                            ? "Decoding Opus and encoding MP3 "
                                + Mp3Converter.DefaultBitRate + " kbps..."
                            : "Decoding Opus to WAV (16-bit PCM, 48 kHz)...");
                    }

                    OpusConverter.PcmResult pcm =
                        OpusConverter.DecodeOggOpus(opus.GetOggData());

                    using (FileStream outStream = CreateOutFile(outPath))
                    {
                        if (IsMp3Target(outPath))
                        {
                            Mp3Converter.EncodePcmToMp3(
                                pcm.Pcm16, pcm.Channels, pcm.SampleRate, outStream);
                        }
                        else
                        {
                            WavConverter.WritePcmWav(
                                outStream, pcm.Pcm16, pcm.Channels, pcm.SampleRate);
                        }
                    }
                }
                else
                {
                    using (FileStream outStream = CreateOutFile(outPath))
                    {
                        opus.GenerateOgg(outStream);
                    }
                }

                return;
            }

            WwiseRiffVorbis ww = new WwiseRiffVorbis(
                wemData,
                wemName,
                opt.CodebooksFilename,
                opt.InlineCodebooks,
                opt.FullSetup,
                opt.ForcePacketFormat,
                shared);

            if (printInfo)
            {
                ww.PrintInfo();

                if (IsMp3Target(outPath))
                {
                    Console.WriteLine("Encoding MP3 " + Mp3Converter.DefaultBitRate + " kbps...");
                }
                else if (IsWavTarget(outPath))
                {
                    Console.WriteLine("Decoding WAV (16-bit PCM)...");
                }
            }

            bool wantMp3 = IsMp3Target(outPath);
            bool wantWav = IsWavTarget(outPath);

            if (!wantMp3 && !wantWav)
            {
                using (FileStream of = CreateOutFile(outPath))
                {
                    ww.GenerateOgg(of);
                }
                return;
            }

            using (MemoryStream oggStream = new MemoryStream())
            {
                ww.GenerateOgg(oggStream);

                using (FileStream outStream = CreateOutFile(outPath))
                {
                    if (wantMp3)
                    {
                        Mp3Converter.ConvertOggToMp3(oggStream, outStream);
                    }
                    else
                    {
                        WavConverter.ConvertOggToWav(oggStream, outStream);
                    }
                }
            }
        }

        private static FileStream CreateOutFile(string filename)
        {
            try
            {
                return File.Create(filename);
            }
            catch (Exception)
            {
                throw new FileOpenError(filename);
            }
        }

        // Classifies the input from its contents: standalone WEM or Wwise
        // pck/bnk container.
        private static InputKind PeekKind(string filename)
        {
            byte[] head;
            try
            {
                using (FileStream fs = new FileStream(
                    filename, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    head = new byte[4];
                    int got = 0;
                    while (got < head.Length)
                    {
                        int n = fs.Read(head, got, head.Length - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    if (got < head.Length)
                    {
                        byte[] small = new byte[got];
                        Buffer.BlockCopy(head, 0, small, 0, got);
                        head = small;
                    }
                }
            }
            catch (Exception)
            {
                throw new FileOpenError(filename);
            }

            if (head.Length >= 4)
            {
                if ((head[0] == (byte)'A' && head[1] == (byte)'K'
                        && head[2] == (byte)'P' && head[3] == (byte)'K')
                    || (head[0] == (byte)'B' && head[1] == (byte)'K'
                        && head[2] == (byte)'H' && head[3] == (byte)'D'))
                {
                    return InputKind.Container;
                }
            }

            return InputKind.Wem;
        }

        // Extract every WEM from a .pck/.bnk and convert each one to a separate
        // .ogg (default), .mp3 (--mp3) or .wav (--wav) file in the output dir.
        private static int RunContainer(Options opt)
        {
            Console.WriteLine("Input: " + opt.InFilename + " (Wwise pck/bnk container)");

            byte[] containerData;
            try
            {
                containerData = File.ReadAllBytes(opt.InFilename);
            }
            catch (Exception)
            {
                Console.WriteLine(new FileOpenError(opt.InFilename).Message);
                return 1;
            }

            List<WemEntry> entries;
            try
            {
                entries = WwiseContainer.ListWems(containerData);
            }
            catch (ParseError pe)
            {
                Console.WriteLine(pe.Message);
                return 1;
            }

            Console.WriteLine(entries.Count + " WEM entries:");
            for (int i = 0; i < entries.Count; i++)
            {
                WemEntry e = entries[i];
                string tag = "?";
                if (e.Size >= 4 && e.Offset + 4 <= containerData.Length)
                {
                    tag = ((char)containerData[e.Offset] == 'R'
                        && (char)containerData[e.Offset + 1] == 'I'
                        && (char)containerData[e.Offset + 2] == 'F')
                        ? ((char)containerData[e.Offset + 3] == 'X' ? "RIFX" : "RIFF")
                        : "----";
                }
                Console.WriteLine("  [" + (i + 1) + "] " + (e.Name ?? "?")
                    + "  offset=" + e.Offset + "  size=" + e.Size + "  " + tag);
            }

            if (opt.ListOnly) return 0;

            string outDir = opt.OutputSet
                ? opt.OutFilename
                : Path.GetFileNameWithoutExtension(opt.InFilename) + "_extracted";

            try
            {
                Directory.CreateDirectory(outDir);
            }
            catch (Exception)
            {
                Console.WriteLine(new FileOpenError(outDir).Message);
                return 1;
            }

            Console.WriteLine("Output dir: " + outDir);

            // Load the external codebooks only if at least one embedded entry is
            // a Vorbis WEM; Opus WEMs do not need them.
            CodebookLibrary shared = null;
            bool anyVorbis = false;
            for (int i = 0; i < entries.Count && !opt.InlineCodebooks; i++)
            {
                byte[] probe = new byte[entries[i].Size];
                Buffer.BlockCopy(containerData, (int)entries[i].Offset, probe, 0, (int)entries[i].Size);
                if (!WwiseRiffOpus.IsOpusWem(probe))
                {
                    anyVorbis = true;
                    break;
                }
            }

            if (anyVorbis)
            {
                try
                {
                    shared = new CodebookLibrary(opt.CodebooksFilename);
                }
                catch (FileOpenError fe)
                {
                    Console.WriteLine(fe.Message);
                    return 1;
                }
            }

            int ok = 0, skipped = 0;

            for (int i = 0; i < entries.Count; i++)
            {
                WemEntry entry = entries[i];
                string wemName = entry.Name ?? ("embedded_" + i + ".wem");

                byte[] wemData = new byte[entry.Size];
                Buffer.BlockCopy(containerData, (int)entry.Offset, wemData, 0, (int)entry.Size);

                bool entryOpus = WwiseRiffOpus.IsOpusWem(wemData);
                string ext = opt.Wav ? ".wav"
                    : (opt.Mp3 ? ".mp3" : (entryOpus ? ".opus" : ".ogg"));
                string outPath = Path.Combine(outDir,
                    Path.GetFileNameWithoutExtension(wemName) + ext);

                Console.Write("[" + (i + 1) + "/" + entries.Count + "] " + wemName
                    + (entryOpus ? " (Opus)" : "") + " ... ");

                try
                {
                    ConvertWem(wemData, wemName, outPath, opt, shared, false);

                    Console.WriteLine("OK");
                    ok++;
                }
                catch (ParseError pe)
                {
                    // unsupported codec / corrupt embedded entry
                    Console.WriteLine("skipped (" + pe.Message + ")");
                    skipped++;
                }
            }

            Console.WriteLine();
            Console.WriteLine("Done! " + ok + " converted, " + skipped + " skipped.");
            return ok == 0 ? 1 : 0;
        }
    }
}
