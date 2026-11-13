using System.IO.Compression;
using KaungMcp.Plugin;

namespace KaungMcp.Content;

internal static class ContentFileValidation
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf", [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel", [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".txt"] = "text/plain", [".md"] = "text/markdown", [".csv"] = "text/csv",
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".bmp"] = "image/bmp", [".tif"] = "image/tiff", [".tiff"] = "image/tiff", [".webp"] = "image/webp",
        [".wav"] = "audio/wav", [".mp3"] = "audio/mpeg", [".m4a"] = "audio/mp4",
        [".aac"] = "audio/aac", [".ogg"] = "audio/ogg", [".flac"] = "audio/flac"
    };
    public static string Validate(string path, string name)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (!Types.TryGetValue(extension, out var mime)) throw new ArgumentException("不支持该文件格式。");
        using var input = File.OpenRead(path);
        Span<byte> header = stackalloc byte[16];
        var count = input.Read(header);
        if (count == 0) throw new ArgumentException("不能上传空文件。");
        bool Starts(ReadOnlySpan<byte> value) => input.Length >= value.Length && HeaderStarts(path, value);
        var valid = extension switch
        {
            ".pdf" => Starts("%PDF-"u8),
            ".png" => Starts([137, 80, 78, 71, 13, 10, 26, 10]),
            ".jpg" or ".jpeg" => Starts([255, 216, 255]),
            ".bmp" => Starts("BM"u8),
            ".tif" or ".tiff" => Starts([73, 73, 42, 0]) || Starts([77, 77, 0, 42]),
            ".webp" => Starts("RIFF"u8) && count >= 12 && header[8..12].SequenceEqual("WEBP"u8),
            ".wav" => Starts("RIFF"u8) && count >= 12 && header[8..12].SequenceEqual("WAVE"u8),
            ".flac" => Starts("fLaC"u8),
            ".ogg" => Starts("OggS"u8),
            ".mp3" => Starts("ID3"u8) || count >= 2 && header[0] == 255 && (header[1] & 224) == 224,
            ".aac" => count >= 2 && header[0] == 255 && (header[1] & 246) == 240,
            ".m4a" => count >= 12 && header[4..8].SequenceEqual("ftyp"u8),
            ".doc" or ".xls" => Starts([208, 207, 17, 224, 161, 177, 26, 225]),
            ".docx" or ".xlsx" => ValidateOffice(path, extension),
            _ => IsText(input, header[..count])
        };
        if (!valid) throw new ArgumentException("文件内容与声明的格式不匹配或文件损坏。");
        return mime;
    }
    private static bool HeaderStarts(string path, ReadOnlySpan<byte> expected)
    {
        using var stream = File.OpenRead(path);
        Span<byte> actual = stackalloc byte[expected.Length];
        return stream.Read(actual) == expected.Length && actual.SequenceEqual(expected);
    }
    private static bool IsText(Stream input, ReadOnlySpan<byte> header)
    {
        if (header.StartsWith(new byte[] { 255, 254 }) || header.StartsWith(new byte[] { 254, 255 })) return true;
        input.Position = 0;
        var buffer = new byte[65536];
        int read;
        while ((read = input.Read(buffer)) > 0) if (buffer.AsSpan(0, read).Contains((byte)0)) return false;
        return true;
    }
    private static bool ValidateOffice(string path, string extension)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count > 10000 || archive.Entries.Any(e => e.Length > 512L * 1024 * 1024) ||
                archive.Entries.Sum(e => e.Length) > 512L * 1024 * 1024)
                throw new ContentLimitException("Office 解压后的内容超过限制。");
            if (archive.Entries.Any(e => e.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("首期不支持含宏的 Office 文件。");
            return archive.GetEntry("[Content_Types].xml") is not null &&
                archive.GetEntry(extension == ".docx" ? "word/document.xml" : "xl/workbook.xml") is not null;
        }
        catch (InvalidDataException) { return false; }
    }
}
