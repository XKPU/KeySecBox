using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace KeySecBox;

/// <summary>
/// 文件夹选择器。
///
/// WPF 没有内建文件夹选择器（WinUI 的 <c>FolderPicker</c> 在 WPF 无对应物），
/// 而引入 <c>UseWindowsForms</c> 会导致 <c>Button</c>/<c>Application</c>/<c>UserControl</c>
/// 等类型在两个命名空间间产生二义性（实测 13 处编译失败），代价过高。
/// 因此这里直接调用 Win32 的 <c>SHBrowseForFolder</c>：零依赖、原生外观。
/// </summary>
internal static class FolderPicker
{
    public static string? Pick(Window? owner, string title)
    {
        var bi = new BROWSEINFO
        {
            hwndOwner = owner != null ? new WindowInteropHelper(owner).Handle : IntPtr.Zero,
            lpszTitle = title,
            ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE | BIF_EDITBOX,
        };

        IntPtr pidl = SHBrowseForFolder(ref bi);

        try
        {
            if (pidl == IntPtr.Zero) return null; // 用户取消
            var sb = new StringBuilder(260);
            if (!SHGetPathFromIDList(pidl, sb)) return null;
            var path = sb.ToString();
            return string.IsNullOrEmpty(path) ? null : path;
        }
        finally
        {
            if (pidl != IntPtr.Zero) CoTaskMemFree(pidl);
        }
    }

    // ---- Win32 ----

    private const uint BIF_RETURNONLYFSDIRS = 0x0001;
    private const uint BIF_NEWDIALOGSTYLE = 0x0040;
    private const uint BIF_EDITBOX = 0x0010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BROWSEINFO
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszDisplayName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolder(ref BROWSEINFO lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDList(IntPtr pidl, StringBuilder pszPath);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);
}
