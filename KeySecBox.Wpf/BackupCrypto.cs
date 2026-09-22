using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace KeySecBox;

/// <summary>
/// 导出文件的加密容器：PBKDF2-HMAC-SHA256 派生密钥 + AES-GCM 加密，
/// 参数与恢复记录同量级（60 万次迭代 / 32 字节密钥 / 16 字节 tag）。
/// 文件布局：magic(8) | iterations(4) | salt(16) | nonce(12) | tag(16) | 密文(n)
/// </summary>
internal static class BackupCrypto
{
    public const string Magic = "KSBXBAK1";

    private const int Iterations = 600_000;
    private const int KeyBytes = 32;
    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    /// <summary>用 <paramref name="password"/> 加密 <paramref name="payload"/> 并写入 <paramref name="path"/>。</summary>
    public static void EncryptToFile(string path, byte[] payload, string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException("加密密码为空，无法导出加密文件。");

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = DeriveKey(password, salt, Iterations);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var cipher = new byte[payload.Length];
        var tag = new byte[TagBytes];
        try
        {
            using var gcm = new AesGcm(key, TagBytes);
            gcm.Encrypt(nonce, payload, cipher, tag);
        }
        finally
        {
            Array.Clear(key, 0, key.Length);
        }

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false);
        bw.Write(Encoding.ASCII.GetBytes(Magic));
        bw.Write(Iterations);
        bw.Write(salt);
        bw.Write(nonce);
        bw.Write(tag);
        bw.Write(cipher);
    }

    /// <summary>读取并解密加密文件；密码错误或文件损坏返回 null。</summary>
    public static byte[]? DecryptFromFile(string path, string password)
    {
        try
        {
            byte[] cipher;
            int iterations;
            byte[] salt, nonce, tag;

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false))
            {
                if (Encoding.ASCII.GetString(br.ReadBytes(Magic.Length)) != Magic) return null;
                iterations = br.ReadInt32();
                salt = br.ReadBytes(SaltBytes);
                nonce = br.ReadBytes(NonceBytes);
                tag = br.ReadBytes(TagBytes);

                using var rest = new MemoryStream();
                fs.CopyTo(rest);
                cipher = rest.ToArray();
            }

            var key = DeriveKey(password, salt, iterations);
            var plain = new byte[cipher.Length];
            try
            {
                using var gcm = new AesGcm(key, TagBytes);
                gcm.Decrypt(nonce, cipher, tag, plain); // 密码错误抛 CryptographicException
            }
            catch (CryptographicException)
            {
                return null;
            }
            finally
            {
                Array.Clear(key, 0, key.Length);
            }
            return plain;
        }
        catch
        {
            return null;
        }
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(KeyBytes);
    }
}
