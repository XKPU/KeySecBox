namespace KeySecBox;

public interface IBinaryFormatService
{
    void PutU32(Stream stream, uint v);
    void PutI64(Stream stream, long v);
    uint GetU32(ReadOnlySpan<byte> data, ref int offset);
    long GetI64(ReadOnlySpan<byte> data, ref int offset);
    byte[] GetBytes(ReadOnlySpan<byte> data, ref int offset, int count);
    byte[] BuildMasterFile(byte[] salt, uint iterations, byte[] chkNonce, byte[] chkBlob);
    byte[] BuildEntryRecord(long id, string account, string note, byte[] pwNonce, byte[] pwCipherWithTag);
    byte[] BuildRecoveryRecord(long id, byte[] nonce, byte[] cipherWithTag);
    byte[] BuildEntriesFile(IEnumerable<byte[]> records);
    byte[] BuildRecoveryFile(IEnumerable<byte[]> records);
    (byte[] salt, uint iterations, byte[] chkNonce, byte[] chkBlob) ParseMasterFile(byte[] data);
    List<(long id, long offset, long total)> ScanEntriesFile(byte[] data);
    (string? account, string? note, byte[] pwNonce, byte[] pwCipher) ParseEntryRecord(byte[] data, long offset);
    List<(long id, long offset, long total)> ScanRecoveryFile(byte[] data);
}