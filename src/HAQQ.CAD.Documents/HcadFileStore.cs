using HAQQ.CAD.Core;

namespace HAQQ.CAD.Documents;

/// <summary>Disk access for HCAD files with atomic replacement.</summary>
public static class HcadFileStore
{
    public const string Extension = ".hcad";
    public const string BackupSuffix = ".bak";

    /// <summary>
    /// Saves the document atomically: the data is written and flushed to a temporary file in the
    /// same folder and only then swapped over the target. An interrupted save leaves the previous
    /// file intact. The previous version is kept as "&lt;name&gt;.hcad.bak".
    /// </summary>
    public static void Save(DrawingDocument document, string path)
    {
        WriteAtomic(path, HcadSerializer.SaveToBytes(document), keepBackup: true);
        document.MarkSaved();
    }

    public static HcadLoadResult Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return HcadSerializer.Load(stream);
    }

    /// <summary>Writes <paramref name="data"/> to <paramref name="path"/> so that the file is either fully old or fully new.</summary>
    public static void WriteAtomic(string path, byte[] data, bool keepBackup)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath) ?? throw new IOException("Invalid file path.");
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, "~" + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");

        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data, 0, data.Length);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                if (keepBackup)
                    File.Replace(temp, fullPath, fullPath + BackupSuffix, ignoreMetadataErrors: true);
                else
                    File.Move(temp, fullPath, overwrite: true);
            }
            else
            {
                File.Move(temp, fullPath);
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>A recovery snapshot found on disk.</summary>
public sealed record RecoveryCandidate(string SnapshotPath, string? OriginalPath, DateTime SavedUtc);

/// <summary>
/// Stores autosave snapshots in a recovery folder. A snapshot exists only while a drawing has
/// unsaved changes; it is removed after a successful save or a clean close. Snapshots left behind
/// by a crash are offered for recovery at the next start.
/// </summary>
public sealed class RecoveryStore
{
    public const string SnapshotExtension = ".hcad-recovery";

    public RecoveryStore(string directory)
    {
        Directory = directory;
    }

    public string Directory { get; }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HAQQ CAD", "Recovery");

    public string GetSnapshotPath(Guid documentId) => Path.Combine(Directory, documentId.ToString("N") + SnapshotExtension);

    /// <summary>Serializes the document. Call on the thread that owns the document.</summary>
    public static byte[] CreateSnapshot(DrawingDocument document, string? originalPath) =>
        HcadSerializer.SaveToBytes(document, new RecoveryInfo(originalPath, DateTime.UtcNow));

    /// <summary>Writes a snapshot produced by <see cref="CreateSnapshot"/>. Safe to call on a background thread.</summary>
    public void WriteSnapshot(Guid documentId, byte[] snapshot) =>
        HcadFileStore.WriteAtomic(GetSnapshotPath(documentId), snapshot, keepBackup: false);

    public void DeleteSnapshot(Guid documentId)
    {
        try
        {
            string path = GetSnapshotPath(documentId);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void DeleteSnapshotFile(string snapshotPath)
    {
        try { if (File.Exists(snapshotPath)) File.Delete(snapshotPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Lists readable snapshots, newest first. Unreadable snapshot files are ignored (not deleted).</summary>
    public IReadOnlyList<RecoveryCandidate> FindSnapshots()
    {
        var result = new List<RecoveryCandidate>();
        if (!System.IO.Directory.Exists(Directory)) return result;
        foreach (string file in System.IO.Directory.EnumerateFiles(Directory, "*" + SnapshotExtension))
        {
            try
            {
                var loaded = HcadFileStore.Load(file);
                result.Add(new RecoveryCandidate(file, loaded.Recovery?.OriginalPath,
                    loaded.Recovery?.SavedUtc ?? File.GetLastWriteTimeUtc(file)));
            }
            catch (Exception ex) when (ex is HcadFormatException or IOException or UnauthorizedAccessException)
            {
                // Damaged snapshot: leave it on disk for manual inspection.
            }
        }
        result.Sort((a, b) => b.SavedUtc.CompareTo(a.SavedUtc));
        return result;
    }
}
