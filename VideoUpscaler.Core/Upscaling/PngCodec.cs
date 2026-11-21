using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace VideoUpscaler.Core.Upscaling;

/// <summary>Image RVB 24 bits, lignes compactées, 3 octets par pixel.</summary>
public sealed class RgbImage
{
    public RgbImage(int width, int height, byte[] pixels)
    {
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(nameof(width), "Les dimensions doivent être positives.");
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != checked(width * height * 3))
            throw new ArgumentException("Le tampon ne correspond pas aux dimensions.", nameof(pixels));

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
}

/// <summary>
/// Codec PNG minimaliste (8 bits, RVB, non entrelacé) qui évite toute dépendance à GDI+.
/// L'encodeur choisit un filtre par ligne, comme libpng, et compresse en zlib.
/// </summary>
public static class PngCodec
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static RgbImage Decode(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = File.ReadAllBytes(path);
        return Decode(bytes);
    }

    public static RgbImage Decode(ReadOnlySpan<byte> png)
    {
        if (png.Length < Signature.Length || !png[..Signature.Length].SequenceEqual(Signature))
            throw new InvalidDataException("Le fichier n'est pas un PNG valide.");

        var offset = Signature.Length;
        var width = 0;
        var height = 0;
        var bitDepth = 0;
        var colorType = 0;
        var interlace = 0;
        var sawHeader = false;
        var idat = new MemoryStream();

        while (offset + 8 <= png.Length)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png[offset..]));
            offset += 4;
            if (length < 0 || offset + 4 + length + 4 > png.Length)
                throw new InvalidDataException("Bloc PNG tronqué.");

            var type = png.Slice(offset, 4);
            offset += 4;
            var data = png.Slice(offset, length);
            offset += length;
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png[offset..]);
            offset += 4;

            if (Crc32(type, data) != crc)
                throw new InvalidDataException("Le CRC d'un bloc PNG est invalide.");

            if (type.SequenceEqual("IHDR"u8))
            {
                if (length < 13)
                    throw new InvalidDataException("En-tête PNG invalide.");
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]));
                bitDepth = data[8];
                colorType = data[9];
                if (data[10] != 0 || data[11] != 0)
                    throw new InvalidDataException("Compression PNG non prise en charge.");
                interlace = data[12];
                sawHeader = true;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idat.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }
        }

        if (!sawHeader || width < 1 || height < 1)
            throw new InvalidDataException("En-tête PNG absent.");
        if (bitDepth != 8)
            throw new InvalidDataException($"PNG {bitDepth} bits non pris en charge (8 bits attendus).");
        if (interlace != 0)
            throw new InvalidDataException("PNG entrelacé non pris en charge.");

        var channels = colorType switch
        {
            0 => 1, // niveaux de gris
            2 => 3, // RVB
            4 => 2, // gris + alpha
            6 => 4, // RVBA
            _ => throw new InvalidDataException($"Type de couleur PNG {colorType} non pris en charge.")
        };

        var stride = checked(width * channels);
        var raw = Decompress(idat.GetBuffer().AsSpan(0, checked((int)idat.Length)), checked(height * (stride + 1)));
        var rows = Unfilter(raw, width, height, channels);

        var pixels = new byte[checked(width * height * 3)];
        for (var y = 0; y < height; y++)
        {
            var source = y * stride;
            var target = y * width * 3;
            switch (channels)
            {
                case 3:
                    Buffer.BlockCopy(rows, source, pixels, target, stride);
                    break;
                case 1:
                    for (var x = 0; x < width; x++)
                    {
                        var value = rows[source + x];
                        var index = target + x * 3;
                        pixels[index] = value;
                        pixels[index + 1] = value;
                        pixels[index + 2] = value;
                    }

                    break;
                case 2:
                    for (var x = 0; x < width; x++)
                    {
                        var value = rows[source + x * 2];
                        var index = target + x * 3;
                        pixels[index] = value;
                        pixels[index + 1] = value;
                        pixels[index + 2] = value;
                    }

                    break;
                default:
                    for (var x = 0; x < width; x++)
                    {
                        var index = target + x * 3;
                        var sourceIndex = source + x * 4;
                        pixels[index] = rows[sourceIndex];
                        pixels[index + 1] = rows[sourceIndex + 1];
                        pixels[index + 2] = rows[sourceIndex + 2];
                    }

                    break;
            }
        }

        return new RgbImage(width, height, pixels);
    }

    public static void Encode(string path, RgbImage image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(image);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        stream.Write(Signature);

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)image.Height);
        header[8] = 8;
        header[9] = 2;
        WriteChunk(stream, "IHDR"u8, header);
        WriteChunk(stream, "IDAT"u8, BuildIdat(image));
        WriteChunk(stream, "IEND"u8, []);
    }

    private static byte[] BuildIdat(RgbImage image)
    {
        var stride = image.Width * 3;
        var filtered = new byte[checked(image.Height * (stride + 1))];
        var empty = new byte[stride];

        // Les lignes sont indépendantes : le filtrage est parallélisé, puis compressé en un flux zlib.
        Parallel.For(0, image.Height, y =>
        {
            var row = image.Pixels.AsSpan(y * stride, stride);
            var prior = y == 0 ? empty.AsSpan() : image.Pixels.AsSpan((y - 1) * stride, stride);
            var candidate = ArrayPool<byte>.Shared.Rent(stride);
            var best = ArrayPool<byte>.Shared.Rent(stride);
            try
            {
                var bestFilter = 0;
                long bestScore = long.MaxValue;
                for (var filter = 0; filter <= 2; filter++)
                {
                    FilterRow(row, prior, candidate.AsSpan(0, stride), filter);
                    var score = Score(candidate.AsSpan(0, stride));
                    if (score >= bestScore)
                        continue;

                    bestScore = score;
                    bestFilter = filter;
                    candidate.AsSpan(0, stride).CopyTo(best);
                }

                var target = y * (stride + 1);
                filtered[target] = (byte)bestFilter;
                best.AsSpan(0, stride).CopyTo(filtered.AsSpan(target + 1));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(candidate);
                ArrayPool<byte>.Shared.Return(best);
            }
        });

        using var compressed = new MemoryStream(filtered.Length / 2);
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(filtered);
        return compressed.ToArray();
    }

    private static void FilterRow(ReadOnlySpan<byte> row, ReadOnlySpan<byte> prior, Span<byte> target, int filter)
    {
        for (var i = 0; i < row.Length; i++)
        {
            var left = i >= 3 ? row[i - 3] : (byte)0;
            var above = prior[i];
            var upperLeft = i >= 3 ? prior[i - 3] : (byte)0;
            var predicted = filter switch
            {
                0 => 0,
                1 => left,
                2 => above,
                3 => (left + above) >> 1,
                _ => Paeth(left, above, upperLeft)
            };
            target[i] = (byte)(row[i] - predicted);
        }
    }

    private static long Score(ReadOnlySpan<byte> row)
    {
        long sum = 0;
        foreach (var value in row)
        {
            var signed = (sbyte)value;
            sum += signed < 0 ? -signed : signed;
        }

        return sum;
    }

    private static byte[] Unfilter(byte[] raw, int width, int height, int channels)
    {
        var stride = width * channels;
        var output = new byte[checked(height * stride)];
        var previous = new byte[stride];

        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            var source = (y * (stride + 1)) + 1;
            var target = y * stride;
            for (var i = 0; i < stride; i++)
            {
                var left = i >= channels ? output[target + i - channels] : (byte)0;
                var above = y > 0 ? previous[i] : (byte)0;
                var upperLeft = i >= channels && y > 0 ? previous[i - channels] : (byte)0;
                var predicted = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => above,
                    3 => (left + above) >> 1,
                    4 => Paeth(left, above, upperLeft),
                    _ => throw new InvalidDataException($"Filtre PNG {filter} invalide.")
                };
                output[target + i] = (byte)(raw[source + i] + predicted);
            }

            output.AsSpan(target, stride).CopyTo(previous);
        }

        return output;
    }

    private static byte[] Decompress(ReadOnlySpan<byte> data, int expectedLength)
    {
        var output = new byte[expectedLength];
        using var input = new MemoryStream(data.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        try
        {
            zlib.ReadExactly(output);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("Flux PNG incomplet.");
        }

        return output;
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)data.Length);
        stream.Write(buffer);
        stream.Write(type);
        stream.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(buffer, Crc32(type, data));
        stream.Write(buffer);
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in type)
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        foreach (var value in data)
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var value = n;
            for (var k = 0; k < 8; k++)
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            table[n] = value;
        }

        return table;
    }
}
