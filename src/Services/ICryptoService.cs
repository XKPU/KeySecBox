namespace KeySecBox;

public interface ICryptoService
{
    byte[] DeriveKey(string password, byte[] salt, uint iterations);
    void DeriveKey(string password, ReadOnlySpan<byte> salt, uint iterations, Span<byte> outputKey);
    byte[] GenerateRandomBytes(int length);
    void GenerateRandomBytes(Span<byte> buffer);
    byte[] Encrypt(byte[] key, byte[] plaintext);
    void Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, Span<byte> nonce, Span<byte> output);
    byte[] Decrypt(byte[] key, byte[] nonce, byte[] cipherWithTag);
    bool TryDecrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> cipherWithTag, Span<byte> output);
}