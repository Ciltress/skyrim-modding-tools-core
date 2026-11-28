using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda.Plugins;

namespace SkyrimHelper;

public sealed partial class RecordIndex
{
    /// <summary>
    /// What the index keeps of each plugin, kept on disk between sessions: a session indexed all 1,408 plugins of a large setup
    /// again before its first record call could answer, 10-14 s every time. A plugin's entry is keyed by its file's full path,
    /// size and time of last change, and by this format's version, so a plugin changed, replaced or moved is read again, and
    /// only that one. A file that cannot be read or written is no error: that plugin is read, as without a cache.
    /// </summary>
    internal sealed class IndexCache(string dir)
    {
        /// <summary>Raised whenever what Read keeps of a record, or how it is written here, changes.</summary>
        private const int FormatVersion = 1;

        /// <summary>Entries no session has used for this long are deleted.</summary>
        private static readonly TimeSpan Unused = TimeSpan.FromDays(30);

        private static readonly ConcurrentDictionary<string, Type?> Types = new(StringComparer.Ordinal);

        private string? FileFor(string plugin)
        {
            try
            {
                var info = new FileInfo(plugin);
                if (!info.Exists) return null;
                var key = $"{FormatVersion}|{Path.GetFullPath(plugin).ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                return Path.Combine(dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32] + ".idx");
            }
            catch (Exception)
            {
                return null;
            }
        }

        public (List<Seen> Records, string? Error)? Load(string plugin)
        {
            if (FileFor(plugin) is not { } file || !File.Exists(file)) return null;
            try
            {
                using var reader = new BinaryReader(File.OpenRead(file), Encoding.UTF8);
                if (reader.ReadInt32() != FormatVersion) return null;
                var error = reader.ReadBoolean() ? reader.ReadString() : null;
                var count = reader.ReadInt32();
                var records = new List<Seen>(count);
                for (var i = 0; i < count; i++)
                {
                    var formKey = FormKey.Factory(reader.ReadString());
                    var type = reader.ReadString();
                    var signature = reader.ReadString();
                    var getter = Types.GetOrAdd(reader.ReadString(), name => System.Type.GetType(name));
                    var editorId = reader.ReadBoolean() ? reader.ReadString() : null;
                    var name = reader.ReadBoolean() ? reader.ReadString() : null;
                    var unplayable = reader.ReadByte() switch { 1 => false, 2 => true, _ => (bool?)null };
                    if (getter is null) return null;
                    records.Add(new Seen(formKey, type, signature, getter, editorId, name, unplayable));
                }
                // Touched, so Prune keeps what is in use.
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
                return (records, error);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Save(string plugin, List<Seen> records, string? error)
        {
            if (FileFor(plugin) is not { } file) return;
            var partial = $"{file}.{Environment.ProcessId}.part";
            try
            {
                Directory.CreateDirectory(dir);
                using (var writer = new BinaryWriter(File.Create(partial), Encoding.UTF8))
                {
                    writer.Write(FormatVersion);
                    writer.Write(error is not null);
                    if (error is not null) writer.Write(error);
                    writer.Write(records.Count);
                    foreach (var seen in records)
                    {
                        writer.Write(seen.FormKey.ToString());
                        writer.Write(seen.Type);
                        writer.Write(seen.Signature);
                        writer.Write(seen.GetterType.AssemblyQualifiedName!);
                        writer.Write(seen.EditorId is not null);
                        if (seen.EditorId is not null) writer.Write(seen.EditorId);
                        writer.Write(seen.Name is not null);
                        if (seen.Name is not null) writer.Write(seen.Name);
                        writer.Write((byte)(seen.Unplayable switch { false => 1, true => 2, null => 0 }));
                    }
                }
                File.Move(partial, file, overwrite: true);
            }
            catch (Exception)
            {
                try { File.Delete(partial); } catch (Exception) { /* nothing kept */ }
            }
        }

        /// <summary>Entries of plugins changed since, or no longer in any load order, once unused for <see cref="Unused"/>.</summary>
        public void Prune()
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                var before = DateTime.UtcNow - Unused;
                foreach (var file in Directory.EnumerateFiles(dir, "*.idx"))
                    if (File.GetLastWriteTimeUtc(file) < before) File.Delete(file);
            }
            catch (Exception)
            {
                // Pruned next time.
            }
        }

        /// <summary>The cache's folder: the system's folder for a program's local data, as the core's LOOT cache.</summary>
        public static string DefaultDir() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
                "skyrim-modding-tools", "record-index");
    }
}
