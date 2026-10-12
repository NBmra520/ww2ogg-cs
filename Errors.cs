using System;

namespace ww2ogg
{
    // Port of errors.h
    internal class ArgumentError : Exception
    {
        public ArgumentError(string message) : base("Argument error: " + message) { }
    }

    internal class FileOpenError : Exception
    {
        public string FileName { get; }

        public FileOpenError(string name)
        {
            FileName = name;
        }

        public override string Message => "Error opening " + FileName;
    }

    internal class ParseError : Exception
    {
        public virtual string Details => "unspecified.";

        public override string Message => "Parse error: " + Details;
    }

    internal class ParseErrorStr : ParseError
    {
        private readonly string _str;

        public ParseErrorStr(string s)
        {
            _str = s;
        }

        public override string Details => _str;
    }

    internal class SizeMismatch : ParseError
    {
        private readonly ulong _realSize, _readSize;

        public SizeMismatch(ulong realSize, ulong readSize)
        {
            _realSize = realSize;
            _readSize = readSize;
        }

        public override string Details => "expected " + _realSize + " bits, read " + _readSize;
    }

    internal class InvalidId : ParseError
    {
        private readonly int _id;

        public InvalidId(int id)
        {
            _id = id;
        }

        public override string Details => "invalid codebook id " + _id + ", try --inline-codebooks";
    }
}
