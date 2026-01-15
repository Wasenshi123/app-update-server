using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
            Directory.CreateDirectory(extractPath);

            while (true)
            {
                int bytesRead = await tarStream.ReadAsync(buffer, 0, 512);
                if (bytesRead == 0 || buffer.All(b => b == 0))
                    break;

                string fileName = System.Text.Encoding.ASCII.GetString(buffer, 0, 100).TrimEnd('\0');
                if (string.IsNullOrWhiteSpace(fileName))
                    break;

                string sizeStr = System.Text.Encoding.ASCII.GetString(buffer, 124, 12).TrimEnd('\0', ' ');
                if (!long.TryParse(sizeStr, System.Globalization.NumberStyles.Integer, null, out long fileSize))
                    fileSize = 0;

                // After reading the 512-byte header, we're already positioned correctly to read the file content
                // No need to seek (which doesn't work with GZipStream anyway)

                if (fileSize > 0)
                {
                    // Sanitize filename to prevent path traversal attacks
                    fileName = fileName.Replace('\\', '/').TrimStart('/');
                    var filePath = Path.Combine(extractPath, fileName);
                    
                    // Validate that the resolved path is still within extractPath
                    var fullExtractPath = Path.GetFullPath(extractPath);
                    var fullFilePath = Path.GetFullPath(filePath);
                    if (!fullFilePath.StartsWith(fullExtractPath + Path.DirectorySeparatorChar) && 
                        fullFilePath != fullExtractPath)
                    {
                        _logger.LogWarning("Skipping file with suspicious path: {fileName}", fileName);
                        // Skip this file and read past it
                        long remaining = fileSize;
                        while (remaining > 0)
                        {
                            int toRead = (int)Math.Min(remaining, buffer.Length);
                            int read = await tarStream.ReadAsync(buffer, 0, toRead);
                            if (read == 0) break;
                            remaining -= read;
                        }
                        // Skip padding and continue to next entry
                        long skipPadding = (512 - (fileSize % 512)) % 512;
                        if (skipPadding > 0)
                        {
                            remaining = skipPadding;
                            while (remaining > 0)
                            {
                                int toRead = (int)Math.Min(remaining, buffer.Length);
                                int read = await tarStream.ReadAsync(buffer, 0, toRead);
                                if (read == 0) break;
                                remaining -= read;
                            }
                        }
                        continue;
                    }
                    
                    var directory = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);

                    using (var fileStream = File.Create(filePath))
                    {
                        long remaining = fileSize;
                        while (remaining > 0)
                        {
                            int toRead = (int)Math.Min(remaining, buffer.Length);
                            int read = await tarStream.ReadAsync(buffer, 0, toRead);
                            if (read == 0) break;
                            await fileStream.WriteAsync(buffer, 0, read);
                            remaining -= read;
                        }
                    }
                }

                // Skip padding to next 512-byte boundary
                long padding = (512 - (fileSize % 512)) % 512;
                if (padding > 0)
                {
                    // Read and discard padding bytes instead of seeking
                    long remaining = padding;
                    while (remaining > 0)
                    {
                        int toRead = (int)Math.Min(remaining, buffer.Length);
                        int read = await tarStream.ReadAsync(buffer, 0, toRead);
                        if (read == 0) break;
                        remaining -= read;
                    }
                }
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

                Array.Clear(buffer, 0, 512);

                // File name (100 bytes, offset 0)
                var nameBytes = System.Text.Encoding.ASCII.GetBytes(relativePath);
                Array.Copy(nameBytes, 0, buffer, 0, Math.Min(nameBytes.Length, 100));

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
