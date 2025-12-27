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

                tarStream.Seek(376 - 512, SeekOrigin.Current);

                if (fileSize > 0)
                {
                    var filePath = Path.Combine(extractPath, fileName);
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

                long padding = (512 - (fileSize % 512)) % 512;
                if (padding > 0)
                    tarStream.Seek(padding, SeekOrigin.Current);
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

                var nameBytes = System.Text.Encoding.ASCII.GetBytes(relativePath);
                Array.Copy(nameBytes, 0, buffer, 0, Math.Min(nameBytes.Length, 100));

                System.Text.Encoding.ASCII.GetBytes("0000644").CopyTo(buffer, 100);
                System.Text.Encoding.ASCII.GetBytes("0000000").CopyTo(buffer, 108);
                System.Text.Encoding.ASCII.GetBytes("0000000").CopyTo(buffer, 116);

                string sizeOctal = Convert.ToString(fileInfo.Length, 8).PadLeft(11, '0') + " ";
                System.Text.Encoding.ASCII.GetBytes(sizeOctal).CopyTo(buffer, 124);

                long unixTime = ((DateTimeOffset)fileInfo.LastWriteTimeUtc).ToUnixTimeSeconds();
                string timeOctal = Convert.ToString(unixTime, 8).PadLeft(11, '0') + " ";
                System.Text.Encoding.ASCII.GetBytes(timeOctal).CopyTo(buffer, 136);

                buffer[156] = (byte)'0'; // Type flag

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
