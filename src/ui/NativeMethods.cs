using System.Runtime.InteropServices;
using System.Text.Json;

namespace KeySecBox;

internal static class NativeMethods
{
    private const string DllName = "KeySecBox.DLL.dll";

    public const int KSBOX_OK = 0;
    public const int KSBOX_ERR_WRONG_PASSWORD = 1;
    public const int KSBOX_ERR_NO_VAULT = 2;
    public const int KSBOX_ERR_NOT_UNLOCKED = 3;
    public const int KSBOX_ERR_IO = 4;
    public const int KSBOX_ERR_NOT_FOUND = 5;
    public const int KSBOX_ERR_DUP = 6;
    public const int KSBOX_ERR_GENERIC = -1;

    // 内置“未分类”分类（C++ 侧 setup 自动创建，id=0，不可增删改）。
    // 新建/编辑条目不选择分类时，即归入此分类。
    public const long UncatId = 0;

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ksbx_store_create();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ksbx_store_destroy(IntPtr s);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ksbx_open(IntPtr s, string file, string masterPwd);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ksbx_setup(IntPtr s, string file, string masterPwd);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ksbx_change_password(IntPtr s, string newMasterPwd);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern long ksbx_add_category(IntPtr s, string name);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ksbx_rename_category(IntPtr s, long id, string name);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ksbx_remove_category(IntPtr s, long id);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ksbx_list_categories(IntPtr s);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern long ksbx_add_entry(IntPtr s, long categoryId, string account, string password, string note);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ksbx_update_entry(IntPtr s, long id, long categoryId, string account, string password, string note);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ksbx_remove_entry(IntPtr s, long id);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ksbx_get_entry(IntPtr s, long id);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ksbx_set_recovery(IntPtr s, long id, string keysJson);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ksbx_get_recovery(IntPtr s, long id);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ksbx_query_all(IntPtr s);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ksbx_query_category(IntPtr s, long categoryId);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ksbx_search(IntPtr s, string keyword);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ksbx_save(IntPtr s);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ksbx_set_tomb_limit(IntPtr s, uint maxBytes, uint maxCount);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ksbx_get_tomb_limit(IntPtr s, out uint maxBytes, out uint maxCount);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ksbx_free(IntPtr ptr);

    private static string? PtrToString(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        var s = Marshal.PtrToStringUni(p);
        ksbx_free(p);
        return s;
    }

    // C++ 侧 JSON 字段为小写 camelCase（id/categoryId/account/password/note/name），
    // System.Text.Json 默认大小写敏感，必须忽略大小写否则字段全部反序列化为默认值。
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // ---- 托管封装 ----
    public sealed class Store : IDisposable
    {
        private readonly IntPtr _handle;

        public Store()
        {
            _handle = ksbx_store_create();
            if (_handle == IntPtr.Zero) throw new OutOfMemoryException();
        }

        public int Open(string file, string pwd) => ksbx_open(_handle, file, pwd);
        public int Setup(string file, string pwd) => ksbx_setup(_handle, file, pwd);
        public int ChangePassword(string pwd) => ksbx_change_password(_handle, pwd);

        public long AddCategory(string name) => ksbx_add_category(_handle, name);
        public int RenameCategory(long id, string name) => ksbx_rename_category(_handle, id, name);
        public int RemoveCategory(long id) => ksbx_remove_category(_handle, id);
        public List<Category> ListCategories()
        {
            var json = PtrToString(ksbx_list_categories(_handle));
            if (string.IsNullOrEmpty(json)) return new();
            return JsonSerializer.Deserialize<List<Category>>(json, JsonOpts) ?? new();
        }

        public long AddEntry(long catId, string account, string pwd, string note)
            => ksbx_add_entry(_handle, catId, account, pwd, note);
        public int UpdateEntry(long id, long catId, string account, string pwd, string note)
            => ksbx_update_entry(_handle, id, catId, account, pwd, note);
        public int RemoveEntry(long id) => ksbx_remove_entry(_handle, id);
        public Entry? GetEntry(long id)
        {
            var json = PtrToString(ksbx_get_entry(_handle, id));
            if (string.IsNullOrEmpty(json)) return null;
            return JsonSerializer.Deserialize<Entry>(json, JsonOpts);
        }

        // ---- 双重验证恢复密钥（独立 .recovery 文件，逐把增删）----
        public int SetRecovery(long id, List<string> keys)
            => ksbx_set_recovery(_handle, id, JsonSerializer.Serialize(keys ?? new List<string>()));
        public List<string> GetRecovery(long id)
        {
            var json = PtrToString(ksbx_get_recovery(_handle, id));
            if (string.IsNullOrEmpty(json)) return new();
            return JsonSerializer.Deserialize<List<string>>(json, JsonOpts) ?? new();
        }

        public List<Entry> QueryAll()
        {
            var json = PtrToString(ksbx_query_all(_handle));
            return DeserializeEntries(json);
        }
        public List<Entry> QueryCategory(long catId)
        {
            var json = PtrToString(ksbx_query_category(_handle, catId));
            return DeserializeEntries(json);
        }
        public List<Entry> Search(string keyword)
        {
            var json = PtrToString(ksbx_search(_handle, keyword));
            return DeserializeEntries(json);
        }

        public int Save() => ksbx_save(_handle);

        public int SetTombLimit(uint maxBytes, uint maxCount) => ksbx_set_tomb_limit(_handle, maxBytes, maxCount);

        public void GetTombLimit(out uint maxBytes, out uint maxCount)
            => ksbx_get_tomb_limit(_handle, out maxBytes, out maxCount);

        private static List<Entry> DeserializeEntries(string? json)
        {
            if (string.IsNullOrEmpty(json)) return new();
            return JsonSerializer.Deserialize<List<Entry>>(json, JsonOpts) ?? new();
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero) ksbx_store_destroy(_handle);
            GC.SuppressFinalize(this);
        }
    }

    public class Category
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
    }

    public class Entry
    {
        public long Id { get; set; }
        public long CategoryId { get; set; }
        public string Account { get; set; } = "";
        public string Password { get; set; } = "";
        public string Note { get; set; } = "";

        // UI 展示用途（非持久化字段，由 MainWindow 在刷新时填充）
        public string CategoryName { get; set; } = "";
        public string NoteDisplay => string.IsNullOrEmpty(Note) ? "(空备注)" : Note;
    }
}
