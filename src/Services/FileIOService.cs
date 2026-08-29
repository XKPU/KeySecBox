namespace KeySecBox;

public class FileIOService : IFileIOService
{
    public bool FileExists(string path)
    {
        return File.Exists(path);
    }

    public byte[]? ReadAllBytes(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return File.ReadAllBytes(path);
        }
        catch
        {
            return null;
        }
    }

    public bool AtomicWriteAllBytes(string path, byte[] data)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public string? ReadAllText(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return File.ReadAllText(path);
        }
        catch
        {
            return null;
        }
    }

    public bool AtomicWriteAllText(string path, string text)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}