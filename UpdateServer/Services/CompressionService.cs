using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UpdateServer.Services
{
    public class CompressionService
    {
        private readonly ILogger<CompressionService> _logger;

        public CompressionService(ILogger<CompressionService> logger)
        {
            _logger = logger;
        }

        public async Task ExtractTarGz(string archivePath, string extractPath)
        {
            _logger.LogDebug("Extracting {archive} to {path}", archivePath, extractPath);

            using (var fileStream = File.OpenRead(archivePath))
            using (var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress))
            {
                await ExtractTar(gzipStream, extractPath);
            }
        }

        public async Task ExtractTar(Stream tarStream, string extractPath)
        {
            var buffer = new byte[512];

            string? fullExtractPath;
            try
            {
                fullExtractPath = Path.GetFullPath(extractPath);
            }
            catch (ArgumentException ex)
            {
                throw new IOException($"Invalid extract path: {extractPath}", ex);
            }

            Directory.CreateDirectory(fullExtractPath);

            string? gnuLongName = null;

            while (true)
            {
                int bytesRead = await tarStream.ReadAsync(buffer, 0, 512);
                if (bytesRead == 0 || buffer.All(b => b == 0))
                {
                    break;
                }

                if (bytesRead < 512)
                {
                    break;
                }

                string sizeStr = Encoding.ASCII.GetString(buffer, 124, 12).TrimEnd('\0', ' ');
                long fileSize = ParseTarOctalSize(sizeStr);

                char typeFlag = (char)buffer[156];
                if (typeFlag == '\0')
                {
                    typeFlag = '0';
                }

                // GNU long name block — payload is the path for the *next* header.
                if (typeFlag == 'L')
                {
                    if (fileSize <= 0 || fileSize > 1024 * 1024)
                    {
                        _logger.LogWarning("GNU long-name block has abnormal size {size}; discarding payload", fileSize);
                        gnuLongName = null;
                        await SkipTarFileBodyAsync(tarStream, buffer, fileSize);
                        await SkipTarPaddingAsync(tarStream, buffer, fileSize);
                        continue;
                    }

                    gnuLongName = await ReadTarStringPayloadAsync(tarStream, buffer, fileSize);
                    gnuLongName = gnuLongName?.Replace('\0', ' ').Trim();
                    await SkipTarPaddingAsync(tarStream, buffer, fileSize);
                    continue;
                }

                // PAX extended / global / GNU longlink: skip payload; do not treat as a file path.
                if (typeFlag == 'x' || typeFlag == 'g' || typeFlag == 'X' || typeFlag == 'K')
                {
                    await SkipTarFileBodyAsync(tarStream, buffer, fileSize);
                    await SkipTarPaddingAsync(tarStream, buffer, fileSize);
                    gnuLongName = null;
                    continue;
                }

                string entryName = BuildTarEntryName(buffer, gnuLongName);
                gnuLongName = null;

                if (string.IsNullOrWhiteSpace(entryName))
                {
                    await SkipTarFileBodyAsync(tarStream, buffer, fileSize);
                    await SkipTarPaddingAsync(tarStream, buffer, fileSize);
                    continue;
                }

                if (typeFlag == '5')
                {
                    // Directory entry (size usually 0)
                    if (TryResolveSafeExtractPath(fullExtractPath, entryName, _logger, out var dirPath))
                    {
                        Directory.CreateDirectory(dirPath);
                    }

                    await SkipTarFileBodyAsync(tarStream, buffer, fileSize);
                    await SkipTarPaddingAsync(tarStream, buffer, fileSize);
                    continue;
                }

                if (typeFlag != '0' && typeFlag != '7')
                {
                    // Unsupported entry — consume payload and continue.
                    await SkipTarFileBodyAsync(tarStream, buffer, fileSize);
                    await SkipTarPaddingAsync(tarStream, buffer, fileSize);
                    continue;
                }

                if (fileSize > 0)
                {
                    if (!TryResolveSafeExtractPath(fullExtractPath, entryName, _logger, out var filePath))
                    {
                        await SkipTarFileBodyAsync(tarStream, buffer, fileSize);
                        await SkipTarPaddingAsync(tarStream, buffer, fileSize);
                        continue;
                    }

                    var directory = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    using (var fileStream = File.Create(filePath))
                    {
                        long remaining = fileSize;
                        while (remaining > 0)
                        {
                            int toRead = (int)Math.Min(remaining, buffer.Length);
                            int read = await tarStream.ReadAsync(buffer, 0, toRead);
                            if (read == 0)
                            {
                                break;
                            }

                            await fileStream.WriteAsync(buffer, 0, read);
                            remaining -= read;
                        }
                    }
                }

                await SkipTarPaddingAsync(tarStream, buffer, fileSize);
            }
        }

        private static string BuildTarEntryName(byte[] header, string? gnuLongName)
        {
            if (!string.IsNullOrEmpty(gnuLongName))
            {
                return gnuLongName.Replace('\\', '/').TrimStart('/');
            }

            var name = Encoding.ASCII.GetString(header, 0, 100).TrimEnd('\0', ' ');
            var magic = Encoding.ASCII.GetString(header, 257, 6).TrimEnd('\0', ' ');
            if (magic.StartsWith("ustar", StringComparison.Ordinal))
            {
                var prefix = Encoding.ASCII.GetString(header, 345, 155).TrimEnd('\0', ' ');
                if (!string.IsNullOrEmpty(prefix))
                {
                    name = prefix.TrimEnd('/') + "/" + name;
                }
            }

            return name.Replace('\\', '/').TrimStart('/');
        }

        private static string SanitizePathSegment(string segment)
        {
            if (string.IsNullOrEmpty(segment))
            {
                return "_";
            }

            var invalid = Path.GetInvalidFileNameChars();
            var chars = segment.Select(c =>
                c < 32 || invalid.Contains(c) ? '_' : c).ToArray();
            return new string(chars);
        }

        /// <summary>Builds a path under extractRoot from a /-separated tar entry name; blocks traversal.</summary>
        private static bool TryResolveSafeExtractPath(
            string fullExtractRoot,
            string entryRelativePath,
            ILogger logger,
            out string fullFilePath)
        {
            fullFilePath = "";
            try
            {
                var segments = entryRelativePath
                    .Replace('\\', '/')
                    .Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Where(s => s != "." && s != "..")
                    .Select(SanitizePathSegment)
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToArray();

                if (segments.Length == 0)
                {
                    return false;
                }

                var combined = Path.Combine(new[] { fullExtractRoot }.Concat(segments).ToArray());
                var fullCombined = Path.GetFullPath(combined);

                if (!fullCombined.StartsWith(fullExtractRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(fullCombined, fullExtractRoot, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("Skipping tar entry outside extract root: {entry}", entryRelativePath);
                    return false;
                }

                fullFilePath = fullCombined;
                return true;
            }
            catch (ArgumentException)
            {
                logger.LogWarning("Skipping tar entry with invalid path characters: {entry}", entryRelativePath);
                return false;
            }
        }

        private static async Task<string?> ReadTarStringPayloadAsync(Stream tarStream, byte[] buffer, long payloadSize)
        {
            if (payloadSize <= 0 || payloadSize > 1024 * 1024)
            {
                return null;
            }

            var nameBuf = new byte[payloadSize];
            long offset = 0;
            while (offset < payloadSize)
            {
                int toRead = (int)Math.Min(payloadSize - offset, buffer.Length);
                int read = await tarStream.ReadAsync(buffer, 0, toRead);
                if (read == 0)
                {
                    break;
                }

                Buffer.BlockCopy(buffer, 0, nameBuf, (int)offset, read);
                offset += read;
            }

            return Encoding.ASCII.GetString(nameBuf).TrimEnd('\0');
        }

        private static async Task SkipTarFileBodyAsync(Stream tarStream, byte[] buffer, long fileSize)
        {
            long remaining = fileSize;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(remaining, buffer.Length);
                int read = await tarStream.ReadAsync(buffer, 0, toRead);
                if (read == 0)
                {
                    break;
                }

                remaining -= read;
            }
        }

        private static long ParseTarOctalSize(string sizeField)
        {
            var trimmed = sizeField.TrimEnd('\0', ' ').Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return 0;
            }

            try
            {
                return Convert.ToInt64(trimmed, 8);
            }
            catch (Exception)
            {
                return long.TryParse(trimmed, System.Globalization.NumberStyles.Integer, null, out var dec)
                    ? dec
                    : 0;
            }
        }

        private static async Task SkipTarPaddingAsync(Stream tarStream, byte[] buffer, long fileSize)
        {
            long padding = (512 - (fileSize % 512)) % 512;
            if (padding <= 0)
            {
                return;
            }

            long rem = padding;
            while (rem > 0)
            {
                int toRead = (int)Math.Min(rem, buffer.Length);
                int read = await tarStream.ReadAsync(buffer, 0, toRead);
                if (read == 0)
                {
                    break;
                }

                rem -= read;
            }
        }

        public async Task CreateTarGz(string sourceDir, string outputPath)
        {
            _logger.LogDebug("Creating tar.gz archive: {output}", outputPath);

            using (var fileStream = File.Create(outputPath))
            using (var gzipStream = new GZipStream(fileStream, CompressionLevel.Optimal))
            {
                await CreateTar(sourceDir, gzipStream);
            }
        }

        public async Task CreateTar(string sourceDir, Stream tarStream)
        {
            var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
            var buffer = new byte[512];

            foreach (var file in files)
            {
                var relativePath = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
                var fileInfo = new FileInfo(file);

                // ustar name field is 100 bytes; longer paths were silently truncated and broke
                // package-manifest.json / upgrades/... layout on the client. Fail fast until GNU longname write is implemented.
                const int maxUstarNameBytes = 100;
                foreach (var c in relativePath)
                {
                    if (c > 0x7f)
                    {
                        throw new InvalidOperationException(
                            $"Tar entry path must be ASCII-only (ustar writer): {relativePath}");
                    }
                }

                var nameBytes = System.Text.Encoding.ASCII.GetBytes(relativePath);
                if (nameBytes.Length > maxUstarNameBytes)
                {
                    throw new InvalidOperationException(
                        $"Tar entry path exceeds {maxUstarNameBytes} bytes (ustar limit): {relativePath} ({nameBytes.Length} bytes). Shorten upgrade ids or folder names.");
                }

                Array.Clear(buffer, 0, 512);

                // File name (100 bytes, offset 0)
                Array.Copy(nameBytes, 0, buffer, 0, nameBytes.Length);

                // File mode (8 bytes, offset 100) - must be 8 bytes with space or null terminator
                var modeBytes = System.Text.Encoding.ASCII.GetBytes("0000644 ");
                Array.Copy(modeBytes, 0, buffer, 100, Math.Min(modeBytes.Length, 8));

                // Owner user ID (8 bytes, offset 108)
                var uidBytes = System.Text.Encoding.ASCII.GetBytes("0000000 ");
                Array.Copy(uidBytes, 0, buffer, 108, Math.Min(uidBytes.Length, 8));

                // Owner group ID (8 bytes, offset 116)
                var gidBytes = System.Text.Encoding.ASCII.GetBytes("0000000 ");
                Array.Copy(gidBytes, 0, buffer, 116, Math.Min(gidBytes.Length, 8));

                // File size (12 bytes, offset 124) - octal with space
                string sizeOctal = Convert.ToString(fileInfo.Length, 8).PadLeft(11, '0') + " ";
                System.Text.Encoding.ASCII.GetBytes(sizeOctal).CopyTo(buffer, 124);

                // Modification time (12 bytes, offset 136) - octal with space
                long unixTime = ((DateTimeOffset)fileInfo.LastWriteTimeUtc).ToUnixTimeSeconds();
                string timeOctal = Convert.ToString(unixTime, 8).PadLeft(11, '0') + " ";
                System.Text.Encoding.ASCII.GetBytes(timeOctal).CopyTo(buffer, 136);

                // Type flag (1 byte, offset 156) - '0' for regular file
                buffer[156] = (byte)'0';

                // USTAR magic (6 bytes, offset 257)
                System.Text.Encoding.ASCII.GetBytes("ustar").CopyTo(buffer, 257);

                // USTAR version (2 bytes, offset 263)
                System.Text.Encoding.ASCII.GetBytes("00").CopyTo(buffer, 263);

                // Calculate checksum (8 bytes, offset 148)
                // Checksum is sum of all bytes, with checksum field itself treated as spaces (0x20)
                int checksum = 0;
                for (int i = 0; i < 512; i++)
                {
                    if (i >= 148 && i < 156)
                    {
                        // Treat checksum field as spaces during calculation
                        checksum += 0x20;
                    }
                    else
                    {
                        checksum += buffer[i];
                    }
                }
                // Write checksum as octal string (6 digits + space + null)
                string checksumStr = Convert.ToString(checksum, 8).PadLeft(6, '0') + " \0";
                System.Text.Encoding.ASCII.GetBytes(checksumStr).CopyTo(buffer, 148);

                await tarStream.WriteAsync(buffer, 0, 512);

                using (var fileStream = File.OpenRead(file))
                {
                    await fileStream.CopyToAsync(tarStream);
                }

                long padding = (512 - (fileInfo.Length % 512)) % 512;
                if (padding > 0)
                {
                    Array.Clear(buffer, 0, (int)padding);
                    await tarStream.WriteAsync(buffer, 0, (int)padding);
                }
            }

            Array.Clear(buffer, 0, 512);
            await tarStream.WriteAsync(buffer, 0, 512);
            await tarStream.WriteAsync(buffer, 0, 512);
        }
    }
}
