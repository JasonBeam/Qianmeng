using System.Buffers.Binary;

namespace DesktopBuddy.ModelCore;

/// <summary>Reads dimensions without asking WIC to instantiate a full-resolution bitmap.</summary>
public static class EncodedImageInfo
{
    public static (int Width, int Height) Dimensions(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 33 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            && BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) == 13 && bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
            return Validate(BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]), BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]));
        if (bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xd8)
        {
            var at = 2;
            while (at < bytes.Length)
            {
                if (bytes[at++] != 0xff) break;
                while (at < bytes.Length && bytes[at] == 0xff) at++;
                if (at >= bytes.Length) break;
                var marker = bytes[at++];
                if (marker is 0xda or 0xd9) break;
                if (marker is 0x01 or >= 0xd0 and <= 0xd8) continue;
                if (at > bytes.Length - 2) break;
                var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[at..]);
                if (length < 2 || length > bytes.Length - at) break;
                if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
                {
                    if (length < 8) break;
                    return Validate(BinaryPrimitives.ReadUInt16BigEndian(bytes[(at + 5)..]), BinaryPrimitives.ReadUInt16BigEndian(bytes[(at + 3)..]));
                }
                at += length;
            }
        }
        throw new ModelImportException("纹理不是完整的 PNG 或 JPEG，无法读取尺寸。");
    }

    private static (int Width, int Height) Validate(uint width, uint height)
    {
        if (width == 0 || height == 0 || width > 16384 || height > 16384 || (long)width * height > 100_000_000)
            throw new ModelImportException("纹理分辨率超过安全限制，请先缩小到 16K 以下。");
        return ((int)width, (int)height);
    }
}
