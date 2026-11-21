namespace LocalMind.AgentHub.Core;

/// <summary>An image the owner attached. The dimensions and type come from the bytes themselves, never from the file name.</summary>
public sealed record ImageData(string Name, string Mime, byte[] Bytes, int Width, int Height)
{
    public string Hash => ProjectStore.Hash(Bytes);
    public long Size => Bytes.Length;
    public string Summary => $"{Name} · {Width}×{Height} · {Size / 1024.0:0.#} KB · {Mime} · {Hash[..12]}";
}

public static class Images
{
    public const int MaxBytes = 8_000_000;
    public const int MaxPixels = 40_000_000;
    public const int MaxImages = 4;

    private static StoreException Bad(string code, string message, string recovery) => new(code, message, recovery);

    /// <summary>Reads PNG, JPEG, GIF or WebP headers. Anything else, an empty or oversized file, or an image too large to send is refused with the reason.</summary>
    public static ImageData Read(string name, byte[] b)
    {
        name = Path.GetFileName(name.Trim());
        if (b.Length == 0) throw Bad("empty_image", "That image is empty.", "Choose another file.");
        if (b.Length > MaxBytes) throw Bad("image_too_large", $"That image is {b.Length / 1_000_000.0:0.#} MB; the limit is {MaxBytes / 1_000_000} MB.", "Crop or scale it down.");
        string mime; int w, h;
        if (b.Length > 24 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) { mime = "image/png"; w = BigEndian(b, 16); h = BigEndian(b, 20); }
        else if (b.Length > 10 && b[0] == 0xFF && b[1] == 0xD8) { mime = "image/jpeg"; (w, h) = Jpeg(b); }
        else if (b.Length > 10 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F') { mime = "image/gif"; w = b[6] | b[7] << 8; h = b[8] | b[9] << 8; }
        else if (b.Length > 30 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') { mime = "image/webp"; (w, h) = WebP(b); }
        else throw Bad("unsupported_image", "Only PNG, JPEG, GIF and WebP images can be attached.", "Save or export the screenshot as a PNG.");
        if (w <= 0 || h <= 0) throw Bad("unreadable_image", "That image's size could not be read, so it was not attached.", "Export it again as a PNG.");
        if ((long)w * h > MaxPixels) throw Bad("image_too_large", $"That image is {w}×{h}; the limit is {MaxPixels / 1_000_000} million pixels.", "Scale it down.");
        return new(name.Length == 0 ? "image" : name, mime, b, w, h);
    }

    private static int BigEndian(byte[] b, int at) => b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3];

    private static (int, int) Jpeg(byte[] b)
    {
        int i = 2;
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xFF) { i++; continue; }
            byte m = b[i + 1];
            if (m is >= 0xC0 and <= 0xCF && m is not (0xC4 or 0xC8 or 0xCC)) return (b[i + 7] << 8 | b[i + 8], b[i + 5] << 8 | b[i + 6]);
            if (m is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }
            i += 2 + (b[i + 2] << 8 | b[i + 3]);
        }
        return (0, 0);
    }

    private static (int, int) WebP(byte[] b)
    {
        string kind = System.Text.Encoding.ASCII.GetString(b, 12, 4);
        if (kind == "VP8X") return (1 + (b[24] | b[25] << 8 | b[26] << 16), 1 + (b[27] | b[28] << 8 | b[29] << 16));
        if (kind == "VP8 ") return ((b[26] | b[27] << 8) & 0x3FFF, (b[28] | b[29] << 8) & 0x3FFF);
        if (kind == "VP8L") { uint v = (uint)(b[21] | b[22] << 8 | b[23] << 16 | b[24] << 24); return ((int)(v & 0x3FFF) + 1, (int)((v >> 14) & 0x3FFF) + 1); }
        return (0, 0);
    }

    /// <summary>A binding for the owner's confirmation: this exact set of images, to this exact destination and model. Change either and it no longer matches.</summary>
    public static string Token(string connectionId, string model, IEnumerable<ImageData> images) =>
        ProjectStore.Hash(System.Text.Encoding.UTF8.GetBytes(connectionId + "|" + model + "|" + string.Join(",", images.Select(i => i.Hash))));
}
