using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace KeySecBox;

internal static class NativeMethods
{
    private const string DllName = "KeySecBox.DLL.dll";

    #region 返回码

    public const int KSBOX_OK = 0;
    public const int KSBOX_ERR_WRONG_PASSWORD = 1;
    public const int KSBOX_ERR_NO_VAULT = 2;
    public const int KSBOX_ERR_NOT_UNLOCKED = 3;
    public const int KSBOX_ERR_IO = 4;
    public const int KSBOX_ERR_NOT_FOUND = 5;
    public const int KSBOX_ERR_DUP = 6;
    public const int KSBOX_ERR_LEGACY = 7;
    public const int KSBOX_ERR_GENERIC = -1;

    #endregion

    // 内置"未分类"(id=0，setup 自动创建，不可增删改)
    public const long UncatId = 0;

    #region P/Invoke

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
    private static extern int ksbx_verify_password(IntPtr s, string masterPwd);

    // 只读打开旧版库
    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ksbx_open_legacy(IntPtr s, string legacyDir, string masterPwd);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern long ksbx_add_category(IntPtr s, string name);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ksbx_rename_category(IntPtr s, long id, string name);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ksbx_move_category(IntPtr s, long id, long newPos);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ksbx_remove_category(IntPtr s, long id);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ksbx_list_categories(IntPtr s);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern long ksbx_add_entry(IntPtr s, string categoryIdsJson, string account, string password, string note);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ksbx_update_entry(IntPtr s, long id, string categoryIdsJson, string account, string password, string note);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ksbx_remove_entry(IntPtr s, long id);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ksbx_move_entry(IntPtr s, long id, long categoryId, long newPos);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ksbx_move_all_entry(IntPtr s, long id, long newPos);

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
    private static extern int ksbx_get_diagnostics(IntPtr s, out int enabled);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ksbx_set_diagnostics(IntPtr s, int enabled);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ksbx_free(IntPtr ptr);

    #endregion

    private static string? PtrToString(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        var s = Marshal.PtrToStringUni(p);
        ksbx_free(p);
        return s;
    }

    // C++ 侧 JSON 字段为小写 camelCase，System.Text.Json 默认大小写敏感，需忽略
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    #region 托管封装

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
        public int VerifyPassword(string pwd) => ksbx_verify_password(_handle, pwd);

        // 只读打开旧版库（legacyDir = 旧版 data 目录），供导入合并
        public int OpenLegacy(string legacyDir, string pwd) => ksbx_open_legacy(_handle, legacyDir, pwd);

        public long AddCategory(string name) => ksbx_add_category(_handle, name);
        public int RenameCategory(long id, string name) => ksbx_rename_category(_handle, id, name);
        public int MoveCategory(long id, long newPos) => ksbx_move_category(_handle, id, newPos);
        public int RemoveCategory(long id) => ksbx_remove_category(_handle, id);
        public List<Category> ListCategories()
        {
            var json = PtrToString(ksbx_list_categories(_handle));
            if (string.IsNullOrEmpty(json)) return new();
            return JsonSerializer.Deserialize<List<Category>>(json, JsonOpts) ?? new();
        }

        private static string SerializeCats(IEnumerable<long> catIds)
            => JsonSerializer.Serialize((catIds ?? new List<long>()).Distinct().ToList());

        public long AddEntry(IEnumerable<long> catIds, string account, string pwd, string note)
            => ksbx_add_entry(_handle, SerializeCats(catIds), account, pwd, note);
        public int UpdateEntry(long id, IEnumerable<long> catIds, string account, string pwd, string note)
            => ksbx_update_entry(_handle, id, SerializeCats(catIds), account, pwd, note);
        public int RemoveEntry(long id) => ksbx_remove_entry(_handle, id);
        public int MoveEntry(long id, long catId, long newPos) => ksbx_move_entry(_handle, id, catId, newPos);
        public int MoveAllEntry(long id, long newPos) => ksbx_move_all_entry(_handle, id, newPos);
        public Entry? GetEntry(long id)
        {
            var json = PtrToString(ksbx_get_entry(_handle, id));
            if (string.IsNullOrEmpty(json)) return null;
            return JsonSerializer.Deserialize<Entry>(json, JsonOpts);
        }

        // 双重验证恢复密钥（独立 .recovery 文件，逐把增删）
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

        public bool GetDiagnostics()
        {
            ksbx_get_diagnostics(_handle, out int enabled);
            return enabled != 0;
        }

        public int SetDiagnostics(bool enabled) => ksbx_set_diagnostics(_handle, enabled ? 1 : 0);

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

    #endregion

    #region 模型

    public class Category : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool _isEditSort;
        private bool _canMoveUp;
        private bool _canMoveDown;
        private string _name = "";

        public long Id { get; set; }

        public string Name
        {
            get => _name;
            set => SetProp(ref _name, value ?? "", nameof(Name));
        }

        // 用刷新查询出的新数据就地更新（重命名后行内容原地刷新，避免整表重建）
        public void PatchFrom(Category src)
        {
            Id = src.Id;
            Name = src.Name;
        }

        private void SetProp<T>(ref T field, T value, string name)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            OnChanged(name);
        }

        // UI 展示用（非持久化，由 MainWindow 排序模式填充）
        public bool IsEditSort
        {
            get => _isEditSort;
            set
            {
                _isEditSort = value;
                OnChanged(nameof(IsEditSort));
                OnChanged(nameof(ShowSortArrows));
                OnChanged(nameof(ShowActionButtons));
            }
        }

        public bool CanMoveUp
        {
            get => _canMoveUp;
            set => SetProp(ref _canMoveUp, value, nameof(CanMoveUp));
        }

        public bool CanMoveDown
        {
            get => _canMoveDown;
            set => SetProp(ref _canMoveDown, value, nameof(CanMoveDown));
        }

        // 排序模式下行内按钮切换；内置"未分类"一律无按钮
        public bool ShowSortArrows => IsEditSort && Id != UncatId;
        public bool ShowActionButtons => !IsEditSort && Id != UncatId;
    }

    public class Entry : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public long Id { get; set; }

        // 多分类 id 列表；序列化字段 categoryIds
        public List<long> CategoryIds { get; set; } = new();

        // 兼容字段 = 首个分类；C++ 侧同时输出 categoryId 与 categoryIds
        public long CategoryId
        {
            get => CategoryIds.Count > 0 ? CategoryIds[0] : UncatId;
            set { if (!CategoryIds.Contains(value)) CategoryIds.Insert(0, value); }
        }

        private string _account = "";
        private string _password = "";
        private string _note = "";
        private string _categoryName = "";
        private bool _canMoveUp;
        private bool _canMoveDown;

        public List<string> Recovery { get; set; } = new();

        public string Account
        {
            get => _account;
            set => SetProp(ref _account, value ?? "", nameof(Account));
        }

        public string Password
        {
            get => _password;
            set => SetProp(ref _password, value ?? "", nameof(Password));
        }

        public string Note
        {
            get => _note;
            set { SetProp(ref _note, value ?? "", nameof(Note)); OnChanged(nameof(NoteDisplay)); }
        }

        // UI 展示用（非持久化，由 MainWindow 刷新时填充）
        public string CategoryName
        {
            get => _categoryName;
            set => SetProp(ref _categoryName, value ?? "", nameof(CategoryName));
        }

        public bool CanMoveUp
        {
            get => _canMoveUp;
            set => SetProp(ref _canMoveUp, value, nameof(CanMoveUp));
        }

        public bool CanMoveDown
        {
            get => _canMoveDown;
            set => SetProp(ref _canMoveDown, value, nameof(CanMoveDown));
        }

        private void SetProp<T>(ref T field, T value, string name)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            OnChanged(name);
        }

        // 用刷新查询出的新数据就地更新本实例（仅数据字段；展示字段由 MainWindow 另行设置）
        public void PatchFrom(Entry src)
        {
            Id = src.Id;
            CategoryIds = new List<long>(src.CategoryIds);
            Account = src.Account;
            Password = src.Password;
            Note = src.Note;
            Recovery = src.Recovery;
        }

        public string NoteDisplay
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Note)) return "";
                int newline = Note.IndexOfAny(new[] { '\r', '\n' });
                return newline >= 0 ? Note.Substring(0, newline) : Note;
            }
        }
        public string RecoveryDisplay => Recovery.Count == 0 ? "(无恢复密钥)" : string.Join("；", Recovery);
    }

    #endregion
}
