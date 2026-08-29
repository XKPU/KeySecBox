using System.Runtime.InteropServices;

namespace KeySecBox;

internal static class DpapiNative
{
    [DllImport("crypt32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        ref DATA_BLOB pDataOut);

    [StructLayout(LayoutKind.Sequential)]
    public struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    public static byte[] Protect(byte[] data)
    {
        var inBlob = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        Marshal.Copy(data, 0, inBlob.pbData, data.Length);

        var outBlob = new DATA_BLOB();
        var entropy = new DATA_BLOB();

        try
        {
            CryptProtectData(ref inBlob, null, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, ref outBlob);
            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(outBlob.pbData);
        }
    }

    public static byte[]? Unprotect(byte[] data)
    {
        var inBlob = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        Marshal.Copy(data, 0, inBlob.pbData, data.Length);

        var outBlob = new DATA_BLOB();
        var entropy = new DATA_BLOB();

        try
        {
            if (!CryptUnprotectData(ref inBlob, null, ref entropy, IntPtr.Zero, IntPtr.Zero, 0, ref outBlob))
                return null;
            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(outBlob.pbData);
        }
    }
}