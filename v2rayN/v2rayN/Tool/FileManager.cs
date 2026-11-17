using System;
using System.IO;
using System.Text;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Zip;

namespace v2rayN.Tool
{
    public static class FileManager
    {
        public static bool ByteArrayToFile(string fileName, byte[] content)
        {
            try
            {
                using (FileStream fs = new FileStream(fileName, FileMode.Create, FileAccess.Write))
                    fs.Write(content, 0, content.Length);
                return true;
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
            }
            return false;
        }

        public static void UncompressFile(string fileName, byte[] content)
        {
            try
            {
                byte[] buffer = new byte[4096];
                int n;
                using (FileStream fs = File.Create(fileName))
                using (GZipInputStream input = new GZipInputStream(new MemoryStream(content)))
                {
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                        fs.Write(buffer, 0, n);
                }
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
            }
        }

        public static string NonExclusiveReadAllText(string path)
        {
            return NonExclusiveReadAllText(path, Encoding.Default);
        }

        public static string NonExclusiveReadAllText(string path, Encoding encoding)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader sr = new StreamReader(fs, encoding))
                    return sr.ReadToEnd();
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
                throw;
            }
        }

        public static bool ZipExtractToFile(string fileName, string ignoredName)
        {
            try
            {
                using (ZipFile archive = new ZipFile(fileName))
                {
                    foreach (ZipEntry entry in archive)
                    {
                        if (entry.IsDirectory || entry.Size <= 0)
                            continue;
                        string entryName = Path.GetFileName(entry.Name);
                        if (!Utils.IsNullOrEmpty(ignoredName) && entryName.Contains(ignoredName))
                            continue;

                        string outputPath = GetSafeOutputPath(Utils.StartupPath(), entryName);
                        if (outputPath == null)
                            throw new IOException("Unsafe ZIP entry path: " + entry.Name);
                        ExtractEntry(archive, entry, outputPath);
                    }
                }
                if (File.Exists(fileName))
                    File.Delete(fileName);
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
                return false;
            }
            return true;
        }

        public static bool ZipExtractToFullFile(string fileName)
        {
            try
            {
                using (ZipFile archive = new ZipFile(fileName))
                {
                    foreach (ZipEntry entry in archive)
                    {
                        if (entry.IsDirectory || entry.Size <= 0)
                            continue;

                        string outputPath = GetSafeOutputPath(Utils.StartupPath(), entry.Name);
                        if (outputPath == null)
                            throw new IOException("Unsafe ZIP entry path: " + entry.Name);
                        ExtractEntry(archive, entry, outputPath);
                    }
                }
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
                return false;
            }
            return true;
        }

        internal static string GetSafeOutputPath(string root, string entryName)
        {
            string rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string normalized = entryName.Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            string fullPath = Path.GetFullPath(Path.Combine(rootPath, normalized));
            if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                return null;
            return fullPath;
        }

        internal static void ExtractEntry(ZipFile archive, ZipEntry entry, string outputPath)
        {
            string directory = Path.GetDirectoryName(outputPath);
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);
            using (Stream input = archive.GetInputStream(entry))
            using (FileStream output = File.Create(outputPath))
                input.CopyTo(output);
        }
    }
}
