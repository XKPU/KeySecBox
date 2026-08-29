using System.Buffers.Binary;
using System.Text;

namespace KeySecBox;

public class BinaryFormatService : IBinaryFormatService
{
    private static readonly byte[] MagicMaster = Encoding.ASCII.GetBytes("KSXM");
    private static readonly byte[] MagicEntries = Encoding.ASCII.GetBytes("KSXE");
    private static readonly byte[] MagicRecovery = Encoding.ASCII.GetBytes("KSXR");
    private const uint FormatVersion = 1;
    private const byte KdfPbkdf2 = 1;

    public void PutU32(Stream stream, uint v)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, v);
        stream.Write(buf);
    }

    public void PutI64(Stream stream, long v)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buf, v);
        stream.Write(buf);
    }

    public uint GetU32(ReadOnlySpan<byte> data, ref int offset)
    {
        var v = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        offset += 4;
        return v;
    }

    public long GetI64(ReadOnlySpan<byte> data, ref int offset)
    {
        var v = BinaryPrimitives.ReadInt64LittleEndian(data[offset..]);
        offset += 8;
        return v;
    }

    public byte[] GetBytes(ReadOnlySpan<byte> data, ref int offset, int count)
    {
        var result = data.Slice(offset, count).ToArray();
        offset += count;
        return result;
    }

    public byte[] BuildMasterFile(byte[] salt, uint iterations, byte[] chkNonce, byte[] chkBlob)
    {
        using var ms = new MemoryStream();
        ms.Write(MagicMaster);
        PutU32(ms, FormatVersion);
        ms.Write(salt);
        ms.WriteByte(KdfPbkdf2);
        PutU32(ms, iterations);
        ms.Write(chkNonce);
        PutU32(ms, (uint)chkBlob.Length);
        ms.Write(chkBlob);
        return ms.ToArray();
    }

    public byte[] BuildEntryRecord(long id, string account, string note, byte[] pwNonce, byte[] pwCipherWithTag)
    {
        var accBytes = Encoding.UTF8.GetBytes(account);
        var noteBytes = Encoding.UTF8.GetBytes(note);
        using var ms = new MemoryStream();
        PutI64(ms, id);
        PutU32(ms, (uint)accBytes.Length);
        ms.Write(accBytes);
        PutU32(ms, (uint)noteBytes.Length);
        ms.Write(noteBytes);
        ms.Write(pwNonce);
        PutU32(ms, (uint)pwCipherWithTag.Length);
        ms.Write(pwCipherWithTag);
        return ms.ToArray();
    }

    public byte[] BuildRecoveryRecord(long id, byte[] nonce, byte[] cipherWithTag)
    {
        using var ms = new MemoryStream();
        PutI64(ms, id);
        ms.Write(nonce);
        PutU32(ms, (uint)cipherWithTag.Length);
        ms.Write(cipherWithTag);
        return ms.ToArray();
    }

    public byte[] BuildEntriesFile(IEnumerable<byte[]> records)
    {
        using var ms = new MemoryStream();
        ms.Write(MagicEntries);
        PutU32(ms, FormatVersion);
        foreach (var rec in records)
            ms.Write(rec);
        return ms.ToArray();
    }

    public byte[] BuildRecoveryFile(IEnumerable<byte[]> records)
    {
        using var ms = new MemoryStream();
        ms.Write(MagicRecovery);
        PutU32(ms, FormatVersion);
        foreach (var rec in records)
            ms.Write(rec);
        return ms.ToArray();
    }

    public (byte[] salt, uint iterations, byte[] chkNonce, byte[] chkBlob) ParseMasterFile(byte[] data)
    {
        int p = 0;
        if (data.Length < 4 || !data.AsSpan(0, 4).SequenceEqual(MagicMaster))
            throw new InvalidDataException("Invalid master file magic");
        p = 4;
        uint ver = GetU32(data, ref p);
        if (ver != 1) throw new InvalidDataException($"Unsupported master version: {ver}");
        var salt = GetBytes(data, ref p, 16);
        byte kdf = data[p++];
        if (kdf != KdfPbkdf2) throw new InvalidDataException("Unsupported KDF");
        uint iterations = GetU32(data, ref p);
        var chkNonce = GetBytes(data, ref p, 12);
        uint cLen = GetU32(data, ref p);
        var chkBlob = GetBytes(data, ref p, (int)cLen);
        return (salt, iterations, chkNonce, chkBlob);
    }

    public List<(long id, long offset, long total)> ScanEntriesFile(byte[] data)
    {
        var result = new List<(long, long, long)>();
        int p = 4;
        uint ver = GetU32(data, ref p);
        if (ver != 1) return result;

        while (p < data.Length)
        {
            long recStart = p;
            long id = GetI64(data, ref p);
            uint accLen = GetU32(data, ref p);
            if (p + accLen > data.Length) break;
            p += (int)accLen;
            uint noteLen = GetU32(data, ref p);
            if (p + noteLen > data.Length) break;
            p += (int)noteLen;
            if (p + 12 > data.Length) break;
            p += 12;
            uint pwLen = GetU32(data, ref p);
            if (p + pwLen > data.Length) break;
            p += (int)pwLen;
            long total = p - recStart;
            result.Add((id, recStart, total));
        }
        return result;
    }

    public (string? account, string? note, byte[] pwNonce, byte[] pwCipher) ParseEntryRecord(byte[] data, long offset)
    {
        int p = (int)offset;
        GetI64(data, ref p);
        uint accLen = GetU32(data, ref p);
        var account = accLen > 0 ? Encoding.UTF8.GetString(data, p, (int)accLen) : null;
        p += (int)accLen;
        uint noteLen = GetU32(data, ref p);
        var note = noteLen > 0 ? Encoding.UTF8.GetString(data, p, (int)noteLen) : null;
        p += (int)noteLen;
        var pwNonce = GetBytes(data, ref p, 12);
        uint pwLen = GetU32(data, ref p);
        var pwCipher = GetBytes(data, ref p, (int)pwLen);
        return (account, note, pwNonce, pwCipher);
    }

    public List<(long id, long offset, long total)> ScanRecoveryFile(byte[] data)
    {
        var result = new List<(long, long, long)>();
        if (data.Length < 8) return result;
        int p = 4;
        uint ver = GetU32(data, ref p);
        if (ver != 1) return result;

        while (p < data.Length)
        {
            long recStart = p;
            long id = GetI64(data, ref p);
            if (p + 12 > data.Length) break;
            p += 12;
            uint len = GetU32(data, ref p);
            if (p + len > data.Length) break;
            p += (int)len;
            long total = p - recStart;
            result.Add((id, recStart, total));
        }
        return result;
    }
}