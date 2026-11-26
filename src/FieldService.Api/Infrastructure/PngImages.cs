using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace FieldService.Api.Infrastructure;

/// <summary>
/// Obrazki demonstracyjne (zdjęcia części i urządzeń) generowane z klucza: ten sam klucz = ten sam obrazek.
/// Bez plików w repo i bez cudzych zdjęć. Pełny rozmiar 800 px (kilkaset KB — widać różnicę z miniaturą 160 px).
/// Gotowe PNG trzymamy w pamięci z limitem rozmiaru.
/// </summary>
public sealed class PngImages(IMemoryCache cache)
{
    public const int FullSize = 800;
    public const int ThumbnailSize = 160;

    public byte[] Get(string key, bool thumbnail)
    {
        var size = thumbnail ? ThumbnailSize : FullSize;
        return cache.GetOrCreate($"png|{key}|{size}", entry =>
        {
            var png = Render(key, size);
            entry.Size = png.Length;
            entry.SlidingExpiration = TimeSpan.FromHours(1);
            return png;
        })!;
    }

    internal static byte[] Render(string key, int size)
    {
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var (r0, g0, b0) = Hsv(seed[0] * 360 / 256, 0.45, 0.85);
        var (r1, g1, b1) = Hsv((seed[0] * 360 / 256 + 40) % 360, 0.6, 0.45);
        var cx = size * (0.3 + seed[1] / 640.0);
        var cy = size * (0.3 + seed[2] / 640.0);
        var radius = size * (0.18 + seed[3] / 1280.0);
        var stripe = 8 + seed[4] % 24;
        // szum w blokach 4×4 px: rozmiar pliku bliższy zdjęciu (setki KB, nie kilka KB), ale nie megabajty
        var random = new Random(BinaryPrimitives.ReadInt32LittleEndian(seed));
        var blocks = size / 4 + 1;
        var noise = Enumerable.Range(0, blocks * blocks).Select(_ => random.Next(-8, 9)).ToArray();

        // wiersz PNG: bajt filtra (0) + RGB
        var raw = new byte[size * (size * 3 + 1)];
        for (var y = 0; y < size; y++)
        {
            var row = y * (size * 3 + 1);
            for (var x = 0; x < size; x++)
            {
                var t = (x + y) / (2.0 * size);
                double r = r0 + (r1 - r0) * t, g = g0 + (g1 - g0) * t, b = b0 + (b1 - b0) * t;
                if ((x * size / 800 + y * size / 800) / stripe % 2 == 0) { r *= 0.92; g *= 0.92; b *= 0.92; }
                var dx = x - cx; var dy = y - cy;
                if (dx * dx + dy * dy < radius * radius) { r = 255 - r * 0.3; g = 255 - g * 0.3; b = 255 - b * 0.3; }
                var n = noise[y / 4 * blocks + x / 4];
                var p = row + 1 + x * 3;
                raw[p] = Clamp(r + n);
                raw[p + 1] = Clamp(g + n);
                raw[p + 2] = Clamp(b + n);
            }
        }

        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, size);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), size);
        header[8] = 8; // 8 bitów na kanał
        header[9] = 2; // RGB
        WriteChunk(output, "IHDR", header);
        using (var compressed = new MemoryStream())
        {
            using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);
            WriteChunk(output, "IDAT", compressed.ToArray());
        }
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeBytes, data));
        output.Write(crc);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(byte[] type, byte[] data)
    {
        var c = 0xFFFFFFFF;
        foreach (var b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }

    private static byte Clamp(double v) => (byte)Math.Clamp((int)v, 0, 255);

    private static (double R, double G, double B) Hsv(int h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        var m = v - c;
        var (r, g, b) = (h / 60) switch
        {
            0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x), 3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x)
        };
        return ((r + m) * 255, (g + m) * 255, (b + m) * 255);
    }
}
