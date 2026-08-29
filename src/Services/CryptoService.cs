using System.Security.Cryptography;
using System.Text;

namespace KeySecBox;

public class CryptoService : ICryptoService
{
    public byte[] DeriveKey(string password, byte[] salt, uint iterations)
    {
        var key = new byte[32];
        DeriveKey(password, salt.AsSpan(), iterations, key);
        return key;
    }

    public void DeriveKey(string password, ReadOnlySpan<byte> salt, uint iterations, Span<byte> outputKey)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, outputKey, (int)iterations, HashAlgorithmName.SHA256);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    public byte[] GenerateRandomBytes(int length)
    {
        var buffer = new byte[length];
        GenerateRandomBytes(buffer);
        return buffer;
    }

    public void GenerateRandomBytes(Span<byte> buffer)
    {
        RandomNumberGenerator.Fill(buffer);
    }

    // 返回布局：nonce(12) || ciphertext || tag(16)
    public byte[] Encrypt(byte[] key, byte[] plaintext)
    {
        var output = new byte[12 + plaintext.Length + 16];
        // nonce 必须直接生成在输出缓冲区上：
        // 若先在栈上生成再拷入 output，下面的 Encrypt 重载会重新生成并覆盖 nonce，
        // 导致写入文件的 nonce 与实际用于加密的 nonce 不一致，解密时 GCM tag 校验必然失败。
        Span<byte> nonce = output.AsSpan(0, 12);
        GenerateRandomBytes(nonce);
        var cipherAndTag = output.AsSpan(12);
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, cipherAndTag[..^16], cipherAndTag[^16..]);
        return output;
    }

    public void Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, Span<byte> nonce, Span<byte> output)
    {
        GenerateRandomBytes(nonce);
        var ciphertext = output[..^16];
        var tag = output[^16..];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
    }

    public byte[] Decrypt(byte[] key, byte[] nonce, byte[] cipherWithTag)
    {
        var cipherLen = cipherWithTag.Length - 16;
        var output = new byte[cipherLen];
        if (!TryDecrypt(key, nonce, cipherWithTag, output))
            throw new CryptographicException("Decryption failed: GCM tag mismatch");
        return output;
    }

    public bool TryDecrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> cipherWithTag, Span<byte> output)
    {
        try
        {
            var ciphertext = cipherWithTag[..^16];
            var tag = cipherWithTag[^16..];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, ciphertext, tag, output);
            return true;
        }
        catch
        {
            return false;
        }
    }
}