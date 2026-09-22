using System.IO;
using System.Windows;
using System.Windows.Controls;
using KeySecBox;

namespace KeySecBox.Views;

/// <summary>导入/导出页。</summary>
public partial class ImportExportPage : UserControl
{
    private NativeMethods.Store? _store;
    private Window? _owner;
    private Action? _onDataChanged;

    public ImportExportPage()
    {
        InitializeComponent(); // 必须调用，否则 XAML 中的命名元素（如 StatusText）全为 null
    }

    internal void Init(NativeMethods.Store store, Window owner, Action? onDataChanged = null)
    {
        _store = store;
        _owner = owner;
        _onDataChanged = onDataChanged;
        StatusText.Visibility = Visibility.Collapsed;
    }

    #region 导入导出

    // 导入旧版库（1.0.x）
    private async Task ImportLegacyAsync()
    {
        if (_store is not { } store) return;

        string? oldDataDir = PickFolder("选择旧版 data 目录");
        if (oldDataDir == null) return;

        string oldBase = Path.Combine(oldDataDir, "vault");
        string settingsPath = oldBase + ".settings";
        if (!File.Exists(settingsPath))
        {
            await ShowMessage("所选目录不是旧版 data 目录。");
            return;
        }

        var pwdDlg = new PasswordInputDialog();
        pwdDlg.Init("请输入旧版保险库主密码：");
        if (pwdDlg.ShowDialogAsync(OwnerWindow()) != true) return;

        string oldPwd = pwdDlg.Answer;
        if (oldPwd.Length == 0)
        {
            await ShowMessage("密码不能为空。");
            return;
        }

        long ok = 0, skipped = 0;
        try
        {
            // 核心库只读打开旧版库，合并进当前新版库
            await Task.Run(() =>
            {
                using var src = new NativeMethods.Store();
                int rc = src.OpenLegacy(oldDataDir, oldPwd);
                if (rc != NativeMethods.KSBOX_OK)
                {
                    throw new InvalidOperationException(rc == NativeMethods.KSBOX_ERR_WRONG_PASSWORD
                        ? "旧版保险库密码错误。"
                        : $"打开旧版保险库失败（错误码 {rc}）。");
                }

                var catMap = new Dictionary<long, long>();
                var existingByName = store.ListCategories()
                    .Where(c => c.Id != NativeMethods.UncatId)
                    .GroupBy(c => c.Name)
                    .ToDictionary(g => g.Key, g => g.First().Id);

                foreach (var cat in src.ListCategories())
                {
                    if (cat.Id == NativeMethods.UncatId)
                    {
                        catMap[cat.Id] = NativeMethods.UncatId;
                        continue;
                    }

                    if (existingByName.TryGetValue(cat.Name, out long existing))
                    {
                        catMap[cat.Id] = existing;
                        continue;
                    }

                    long nid = store.AddCategory(cat.Name);
                    if (nid > 0) existingByName[cat.Name] = nid;
                    catMap[cat.Id] = nid > 0 ? nid : NativeMethods.UncatId;
                }

                foreach (var ent in src.QueryAll())
                {
                    var full = src.GetEntry(ent.Id);
                    if (full == null) { skipped++; continue; }

                    var catIds = full.CategoryIds
                        .Where(id => catMap.ContainsKey(id))
                        .Select(id => catMap[id])
                        .Distinct()
                        .ToList();
                    if (catIds.Count == 0) catIds.Add(NativeMethods.UncatId);

                    long nid = store.AddEntry(catIds, full.Account ?? "", full.Password ?? "",
                                              full.Note ?? "");
                    if (nid <= 0) { skipped++; continue; }

                    var rec = src.GetRecovery(ent.Id);
                    if (rec.Count > 0 && store.SetRecovery(nid, rec) != NativeMethods.KSBOX_OK)
                        Trace($"old import: set recovery failed for new id={nid}");
                    ok++;
                }
            });
        }
        catch (Exception ex)
        {
            Trace($"old import EX: {ex}");
            await ShowMessage($"导入失败：{ex.Message}");
            return;
        }

        if (ok > 0)
        {
            store.Save();
            _onDataChanged?.Invoke();
        }

        SetStatus($"上次导入（旧版库）：新增 {ok} 条，跳过 {skipped} 条。");
        await ShowMessage($"导入完成：新增 {ok} 条记录，跳过 {skipped} 条。");
    }

    private async void ImportBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_store is not { } store) return;

        var methodDlg = new ImportMethodDialog();
        methodDlg.ShowDialogAsync(OwnerWindow());

        var method = methodDlg.TakeMethod();
        if (method == null) return;

        // 旧版库沿用原逻辑（选目录 + 旧版主密码 + 核心库合并）
        if (method == ImportMethod.Legacy) { await ImportLegacyAsync(); return; }

        var path = PickImportFile(method.Value);
        if (path == null) return;

        string text;
        try
        {
            // 先判断是否加密，加密则解密后解析
            if (ImportSource.LooksEncrypted(path))
            {
                var plain = await DecryptWithPromptAsync(path);
                if (plain == null) return; // 用户取消
                text = plain;
            }
            else
            {
                text = await Task.Run(() => ImportSource.ReadText(path));
            }
        }
        catch (Exception ex)
        {
            Trace($"import read EXCEPTION: {ex}");
            await ShowMessage("读取文件失败。");
            return;
        }

        ImportSource source;
        try
        {
            source = await Task.Run(() => method == ImportMethod.Csv
                ? ImportSource.FromCsv(text)
                : ImportSource.FromJson(text));
        }
        catch (Exception ex)
        {
            Trace($"import parse EXCEPTION: {ex}");
            await ShowMessage("解析文件内容失败。");
            return;
        }

        if (source.Records.Count == 0) { await ShowMessage("文件中没有可识别的数据。"); return; }

        var advDlg = new AdvancedImportDialog();
        advDlg.Init(store, source, Path.GetFileName(path));
        if (advDlg.ShowDialogAsync(OwnerWindow()) != true) return;

        var mapping = advDlg.TakeMapping();
        if (mapping == null) return;

        ImportRunResult result;
        try
        {
            result = await Task.Run(() => ImportSource.Run(store, source, mapping));
        }
        catch (Exception ex)
        {
            Trace($"import EXCEPTION: {ex}");
            await ShowMessage($"导入写入失败：{ex.Message}");
            return;
        }

        if (result.Imported > 0)
        {
            store.Save();
            _onDataChanged?.Invoke(); // 刷新主界面
        }

        SetStatus($"上次导入：新增 {result.Imported} 条，跳过 {result.Skipped} 条。");
        await ShowMessage($"导入完成：新增 {result.Imported} 条，跳过 {result.Skipped} 条。");
    }

    // 按导入方式选择文件
    private string? PickImportFile(ImportMethod method)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要导入的文件",
            Filter = method == ImportMethod.Csv
                ? "CSV 文件 (*.csv;*.csvenc)|*.csv;*.csvenc|所有文件 (*.*)|*.*"
                : "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
        };
        return dlg.ShowDialog(OwnerWindow()) == true ? dlg.FileName : null;
    }

    // 反复询问密码直至解密成功或取消
    private async Task<string?> DecryptWithPromptAsync(string path)
    {
        while (true)
        {
            var dlg = new PasswordInputDialog();
            dlg.Init("该文件为加密导出文件，请输入导出时使用的密码：");
            if (dlg.ShowDialogAsync(OwnerWindow()) != true) return null;

            string pwd = dlg.Answer;
            if (pwd.Length == 0) { await ShowMessage("密码不能为空。"); continue; }

            var plain = await Task.Run(() => ImportSource.TryDecrypt(path, pwd));
            pwd = "";
            if (plain == null) { await ShowMessage("密码错误或文件已损坏，请重试。"); continue; }
            return plain;
        }
    }

    private async void ExportBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_store is not { } store) return;

        // 条目数用于对话框提示与禁用无数据可用的导出方式
        var dlg = new ExportMethodDialog();
        dlg.Init(store.QueryAll().Count);
        // 对话框内部完成密码二次校验后关闭，未通过则无请求
        dlg.ShowDialogAsync(OwnerWindow());

        var request = dlg.TakeRequest();
        if (request == null) return;

        // CSV 类导出前先收集分类筛选与字段名偏好
        if (request.Method is ExportMethod.Csv or ExportMethod.EncryptedCsv)
        {
            var optDlg = new CsvExportOptionsDialog();
            optDlg.Init(store);
            optDlg.ShowDialogAsync(OwnerWindow());
            var prefs = optDlg.TakePrefs();
            if (prefs == null) return; // 取消
            request.CsvPrefs = prefs;
        }

        var target = PickExportTarget(request);
        if (target == null) return;

        try
        {
            var result = await Task.Run(() => new ExportService(store).Execute(request, target));
            SetStatus($"上次导出：{result.Count} {(request.IsDirectoryExport ? "个文件" : "条记录")} → {result.Target}");
            await ShowMessage(SuccessMessage(request, result));
        }
        catch (Exception ex)
        {
            Trace($"export EXCEPTION: {ex}");
            SetStatus($"导出失败：{ex.Message}");
            await ShowMessage($"导出失败：{ex.Message}");
        }
        finally
        {
            request.Clear(); // 丢弃加密密码引用
        }
    }

    // 按导出方式选择目标
    private string? PickExportTarget(ExportRequest request)
    {
        if (request.Method == ExportMethod.DataDirectory)
        {
            string? folder = PickFolder("选择导出目录");
            // 保留 data 目录名，便于整体拷贝回迁
            return folder == null ? null : Path.Combine(folder, "data");
        }

        var (label, ext) = request.Method switch
        {
            ExportMethod.Csv => ("CSV 文件", ".csv"),
            ExportMethod.EncryptedCsv => ("加密 CSV 文件", ".csvenc"),
            ExportMethod.EncryptedDataDirectory => ("加密备份文件", ".ksbak"),
            _ => ("导出文件", ".bin"),
        };

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "选择导出位置",
            FileName = $"keysbox_{DateTime.Now:yyyyMMdd_HHmmss}{ext}",
            Filter = $"{label} (*{ext})|*{ext}|所有文件 (*.*)|*.*",
            DefaultExt = ext,
        };
        return dlg.ShowDialog(OwnerWindow()) == true ? dlg.FileName : null;
    }

    private static string SuccessMessage(ExportRequest request, ExportResult result)
    {
        var tail = request.IsEncrypted
            ? "请牢记加密密码，密码丢失后无法恢复。"
            : "请妥善保管导出文件。";
        return request.IsDirectoryExport
            ? $"已导出 {result.Count} 个文件到：{result.Target}{Environment.NewLine}{tail}"
            : $"已导出 {result.Count} 条记录到：{result.Target}{Environment.NewLine}{tail}";
    }

    #endregion

    #region 辅助

    internal static void Trace(string msg)
    {
        if (!AppPaths.TraceEnabled) return; // 仅诊断模式下记录
        try { File.AppendAllText(AppPaths.TraceLog, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); }
        catch { }
    }

    /// <summary>选择文件夹（原生 Win32 对话框，见 <see cref="FolderPicker"/>）。</summary>
    private string? PickFolder(string title) => FolderPicker.Pick(OwnerWindow(), title);

    private Window OwnerWindow() =>
        _owner ?? Window.GetWindow(this) ?? Application.Current.MainWindow;

    private async Task ShowMessage(string text)
    {
        var dlg = new MessageDialog(text);
        dlg.ShowDialogAsync(OwnerWindow());
        await Task.CompletedTask;
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = string.IsNullOrEmpty(text)
            ? Visibility.Collapsed : Visibility.Visible;
    }

    #endregion
}
