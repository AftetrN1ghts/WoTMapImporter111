using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace WoTMapImporter.Editor.Package
{
    /// <summary>
    /// A `.pkg` file is just a ZIP archive. Wraps System.IO.Compression.ZipArchive to make
    /// lookups by lowercased name fast.
    /// </summary>
    public sealed class PkgFile : IDisposable
    {
        private readonly ZipArchive _archive;
        private readonly Dictionary<string, ZipArchiveEntry> _entries;
        private readonly string _filePath;
        private bool _disposed;

        public string Name { get; }

        public PkgFile(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Pkg file not found", path);
            _filePath = path;
            Name = Path.GetFileName(path);
            _archive = ZipFile.OpenRead(path);
            _entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in _archive.Entries)
            {
                _entries[e.FullName.Replace('\\', '/')] = e;
            }
        }

        private ZipArchiveEntry FindEntry(string name)
        {
            string clean = name.Replace('\\', '/');
            if (_entries.TryGetValue(clean, out var entry))
                return entry;

            // Fallback: search by bare filename
            string bareName = "/" + Path.GetFileName(clean);
            foreach (var kvp in _entries)
            {
                if (kvp.Key.EndsWith(bareName, StringComparison.OrdinalIgnoreCase) || kvp.Key.Equals(Path.GetFileName(clean), StringComparison.OrdinalIgnoreCase))
                {
                    return kvp.Value;
                }
            }
            return null;
        }

        public bool Exists(string name)
        {
            return FindEntry(name) != null;
        }

        private Stream OpenZipEntry(ZipArchiveEntry entry)
        {
            try
            {
                using var s = entry.Open();
                var ms = new MemoryStream();
                s.CopyTo(ms);
                ms.Position = 0;
                return ms;
            }
            catch (NotSupportedException)
            {
                try
                {
                    System.Type zipFileType = null;
                    foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                    {
                        zipFileType = asm.GetType("ICSharpCode.SharpZipLib.Zip.ZipFile");
                        if (zipFileType != null) break;
                    }

                    if (zipFileType == null) return null;

                    var zipObj = System.Activator.CreateInstance(zipFileType, new object[] { _filePath });
                    var getEntryMethod = zipFileType.GetMethod("GetEntry", new System.Type[] { typeof(string) });
                    var entryObj = getEntryMethod.Invoke(zipObj, new object[] { entry.FullName });

                    if (entryObj == null) return null;

                    var getInputStreamMethod = zipFileType.GetMethod("GetInputStream", new System.Type[] { entryObj.GetType() });
                    var stream = (Stream)getInputStreamMethod.Invoke(zipObj, new object[] { entryObj });

                    var outMs = new MemoryStream();
                    stream.CopyTo(outMs);
                    outMs.Position = 0;

                    if (zipObj is IDisposable disp) disp.Dispose();

                    return outMs;
                }
                catch
                {
                    return null;
                }
            }
        }

        public byte[] ReadBytes(string name)
        {
            var entry = FindEntry(name);
            if (entry == null) return null;
            using var s = OpenZipEntry(entry);
            if (s == null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        public Stream OpenRead(string name)
        {
            var entry = FindEntry(name);
            if (entry == null) return null;
            return OpenZipEntry(entry);
        }

        public IEnumerable<string> GetEntries()
        {
            return _entries.Keys;
        }

        public IEnumerable<string> GetEntriesWithExtension(string ext)
        {
            foreach (var name in _entries.Keys)
            {
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    yield return name;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _archive?.Dispose();
        }
    }
}
