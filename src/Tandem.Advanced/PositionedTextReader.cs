using System.Text;

namespace Tandem.Advanced;

// Character-oriented text reader with byte-order-mark detection. Lines are read
// as bounded fragments so oversized lines never require unbounded memory.
internal sealed class PositionedTextReader : IDisposable
{
    private readonly StreamReader _reader;
    private readonly CancellationToken _cancellationToken;
    internal bool End => _reader.Peek() < 0;

    internal int Peek() => _reader.Peek();

    internal PositionedTextReader(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _cancellationToken = cancellationToken;
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.SequentialScan
        );
        try
        {
            Span<byte> prefix = stackalloc byte[4];
            var count = stream.Read(prefix);
            var bom = 0;
            Encoding encoding = new UTF8Encoding(false, true);
            if (count >= 4 && prefix.SequenceEqual(new byte[] { 0xff, 0xfe, 0, 0 }))
            {
                encoding = new UTF32Encoding(false, false, true);
                bom = 4;
            }
            else if (count >= 4 && prefix.SequenceEqual(new byte[] { 0, 0, 0xfe, 0xff }))
            {
                encoding = new UTF32Encoding(true, false, true);
                bom = 4;
            }
            else if (count >= 3 && prefix[0] == 0xef && prefix[1] == 0xbb && prefix[2] == 0xbf)
            {
                bom = 3;
            }
            else if (count >= 2 && prefix[0] == 0xff && prefix[1] == 0xfe)
            {
                encoding = new UnicodeEncoding(false, false, true);
                bom = 2;
            }
            else if (count >= 2 && prefix[0] == 0xfe && prefix[1] == 0xff)
            {
                encoding = new UnicodeEncoding(true, false, true);
                bom = 2;
            }

            stream.Position = bom;
            _reader = new StreamReader(stream, encoding, false, 4096);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal (string Text, bool LineEnded) Fragment(int maximumCharacters)
    {
        var text = new StringBuilder();
        while (!End)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var next = (char)_reader.Peek();
            if (next is '\r' or '\n')
            {
                ReadCharacter();
                if (next == '\r' && _reader.Peek() == '\n')
                {
                    ReadCharacter();
                }

                return (text.ToString(), true);
            }
            if (text.Length + (char.IsHighSurrogate(next) ? 2 : 1) > maximumCharacters)
            {
                return (text.ToString(), false);
            }

            var first = ReadCharacter();
            text.Append(first);
            if (char.IsHighSurrogate(first))
            {
                var second = (char)_reader.Read();
                if (!char.IsLowSurrogate(second))
                {
                    throw new InvalidDataException("Invalid Unicode text.");
                }

                text.Append(second);
            }
        }
        return (text.ToString(), true);
    }

    private char ReadCharacter()
    {
        var character = (char)_reader.Read();
        if (character == '\0')
        {
            throw new InvalidDataException("File contains binary NUL data.");
        }

        return character;
    }

    public void Dispose() => _reader.Dispose();
}
