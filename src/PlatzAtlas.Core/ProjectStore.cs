namespace PlatzAtlas.Core;

internal static class ProjectStore
{
    public static string PreviousPath(string path) => path + ".previous";

    public static bool HasSavedHead(string path, string fingerprint) => Matches(ReadExisting(path), fingerprint);

    public static void Publish(string path, byte[] bytes, string? expectedFingerprint, bool overwrite, bool unchanged)
    {
        WithPublicationLock(path, () =>
        {
            var existing = ReadExisting(path);
            if (expectedFingerprint is not null && !Matches(existing, expectedFingerprint))
            {
                throw new IOException("The saved project changed on disk. Use Save As to keep your edits in another file.");
            }

            if (existing is not null)
            {
                if (expectedFingerprint is null && !overwrite)
                {
                    throw new IOException("A project already exists at this location. Choose another name.");
                }

                if (unchanged)
                {
                    return false;
                }

                RequireReplaceableHead(path, existing);
            }

            WriteHead(path, bytes, existing is not null);
            return true;
        });
    }

    public static T PublishApproved<T>(string path, Action requireApproval,
        Func<byte[], (T Result, byte[]? Bytes)> prepare)
    {
        return WithPublicationLock(path, () =>
        {
            requireApproval();
            var existing = ReadExisting(path) ?? throw new ProjectEditException(EditRejection.StaleRevision,
                "The saved project is missing. Reopen it before applying the change.");
            var (result, bytes) = prepare(existing);
            if (bytes is not null)
            {
                RequireReplaceableHead(path, existing);
                WriteHead(path, bytes, replace: true, beforeReplace: requireApproval);
            }
            else
                requireApproval();
            return result;
        });
    }

    private static T WithPublicationLock<T>(string path, Func<T> publish)
    {
        // Studio writers in this Windows session share a short-lived lock for the same destination path.
        var lockName = ProjectFormat.Digest(System.Text.Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
        using var owner = new Mutex(false, $@"Local\PLATZ-ATLAS-Project-{lockName}");
        try
        {
            if (!owner.WaitOne(0))
                throw new IOException("Another Studio window is saving this project. Try again.");
        }
        catch (AbandonedMutexException)
        {
            // An interrupted writer released this mutex. The next read still validates the disk head.
        }

        try
        {
            return publish();
        }
        finally
        {
            owner.ReleaseMutex();
        }
    }

    private static void RequireReplaceableHead(string path, byte[] existing)
    {
        RequireProject(existing, "The destination is not a readable PLATZ project. Choose another name.");
        var previous = ReadExisting(PreviousPath(path));
        if (previous is not null)
            RequireProject(previous, "The previous revision file is unreadable. Choose another save location.");
    }

    private static void WriteHead(string path, byte[] bytes, bool replace, Action? beforeReplace = null)
    {
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }

            beforeReplace?.Invoke();
            if (replace)
                File.Replace(staging, path, PreviousPath(path));
            else
                File.Move(staging, path);
        }
        finally
        {
            // A cleanup failure cannot hide the publication result, and a staging file is never a head.
            try { File.Delete(staging); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool Matches(byte[]? existing, string fingerprint) =>
        existing is not null && ProjectFormat.Digest(existing) == fingerprint;

    private static byte[]? ReadExisting(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static void RequireProject(byte[] bytes, string message)
    {
        try
        {
            if (ProjectFormat.Read(bytes).ProjectId == ProjectSession.PublicFixtureId)
                throw new IOException("The public museum fixture is read-only. Choose another save location.");
        }
        catch (InvalidDataException exception)
        {
            throw new IOException(message, exception);
        }
    }
}
