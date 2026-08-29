namespace KeySecBox;

public interface IFileIOService
{
    bool FileExists(string path);
    byte[]? ReadAllBytes(string path);
    bool AtomicWriteAllBytes(string path, byte[] data);
    string? ReadAllText(string path);
    bool AtomicWriteAllText(string path, string text);
    bool DeleteFile(string path);
}