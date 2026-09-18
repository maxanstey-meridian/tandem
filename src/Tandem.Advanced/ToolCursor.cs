using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tandem.Infrastructure;

namespace Tandem.Advanced;

// Stateless continuations. All decoded paths still go through workspace authority.
internal static class ToolCursor
{
    internal static string Scope(params object?[] values) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(values)));

    internal static string Encode<T>(string scope, T value) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Envelope<T>(scope, value)));

    internal static T Decode<T>(string cursor, string scope)
    {
        try
        {
            if (cursor.Length > 16384)
            {
                throw new FormatException();
            }

            var envelope = JsonSerializer.Deserialize<Envelope<T>>(
                Convert.FromBase64String(cursor)
            );
            if (envelope is null || envelope.Scope != scope || envelope.Value is null)
            {
                throw new FormatException();
            }

            return envelope.Value;
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            throw new ToolInputException(
                "Invalid or mismatched continuation. Restart without cursor, then copy nextCursor with unchanged arguments."
            );
        }
    }

    private sealed record Envelope<T>(string Scope, T Value);
}

internal sealed record TextFileVersion(long Length, long Modified)
{
    internal static TextFileVersion Read(string path)
    {
        var file = new FileInfo(path);
        return new(file.Length, file.LastWriteTimeUtc.Ticks);
    }

    internal void Validate(string path)
    {
        if (this != Read(path))
        {
            throw new ToolInputException(
                "File changed since this page was read. Restart without cursor."
            );
        }
    }
}

// StreamReader buffers decoding; Position tracks consumed bytes rather than read-ahead.
internal sealed class PositionedTextReader : IDisposable
{
    private readonly StreamReader _reader;
    private readonly Encoding _encoding;
    private readonly CancellationToken _cancellationToken;
    internal long Position { get; private set; }
    internal bool End => _reader.Peek() < 0;

    internal PositionedTextReader(string path, long? position, CancellationToken cancellationToken)
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
            _encoding = new UTF8Encoding(false, true);
            if (count >= 4 && prefix.SequenceEqual(new byte[] { 0xff, 0xfe, 0, 0 }))
            {
                _encoding = new UTF32Encoding(false, false, true);
                bom = 4;
            }
            else if (count >= 4 && prefix.SequenceEqual(new byte[] { 0, 0, 0xfe, 0xff }))
            {
                _encoding = new UTF32Encoding(true, false, true);
                bom = 4;
            }
            else if (count >= 3 && prefix[0] == 0xef && prefix[1] == 0xbb && prefix[2] == 0xbf)
            {
                bom = 3;
            }
            else if (count >= 2 && prefix[0] == 0xff && prefix[1] == 0xfe)
            {
                _encoding = new UnicodeEncoding(false, false, true);
                bom = 2;
            }
            else if (count >= 2 && prefix[0] == 0xfe && prefix[1] == 0xff)
            {
                _encoding = new UnicodeEncoding(true, false, true);
                bom = 2;
            }
            Position = position ?? bom;
            if (Position < bom || Position > stream.Length)
            {
                throw new ToolInputException("Invalid file continuation. Restart without cursor.");
            }

            stream.Position = Position;
            _reader = new StreamReader(stream, _encoding, false, 4096);
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
                Position += _encoding.GetByteCount(new[] { first, second });
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

        if (!char.IsHighSurrogate(character))
        {
            Span<char> value = stackalloc char[1];
            value[0] = character;
            Position += _encoding.GetByteCount(value);
        }
        return character;
    }

    public void Dispose() => _reader.Dispose();
}
