using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KeySecBox;

// 导入/导出页（由设置「数据」区内联而来）
public sealed partial class ImportExportPage : Page
{
    public ImportExportPage()
    {
        InitializeComponent(); // 必须调用，否则 XAML 中的命名元素（如 StatusText）全为 null
    }

    private NativeMethods.Store? _store;
    private IntPtr _ownerHwnd;
    private Action? _onDataChanged;

    internal void Init(NativeMethods.Store store, IntPtr ownerHwnd, Action? onDataChanged = null)
    {
        _store = store;
        _ownerHwnd = ownerHwnd;
        _onDataChanged = onDataChanged;
        StatusText.Visibility = Visibility.Collapsed;
    }

    #region 导入导出

    private async void ImportOldDataBtn_Click(object sender, RoutedEventArgs e) => await ImportLegacyAsync();

    // 导入旧版库（1.0.x）
    private async Task ImportLegacyAsync()
    {
        if (_store is not { } store) return;

        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _ownerHwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        string oldDataDir = folder.Path;
        string oldBase = System.IO.Path.Combine(oldDataDir, "vault");
        string settingsPath = oldBase + ".settings";
        if (!System.IO.File.Exists(settingsPath))
        {
            await ShowMessage("所选目录不是旧版 data 目录。");
            return;
        }

        var pwdDlg = new PasswordInputDialog();
        pwdDlg.Init("请输入旧版保险库主密码：");
        if (await ShowChildAsync(pwdDlg) != ContentDialogResult.Primary) return;
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
                    throw new InvalidOperationException(rc == NativeMethods.KSBOX_ERR_WRONG_PASSWORD
                        ? "旧版保险库密码错误。"
                        : $"打开旧版保险库失败（错误码 {rc}）。");

                var catMap = new Dictionary<long, long>();
                var existingByName = store.ListCategories()
                    .Where(c => c.Id != NativeMethods.UncatId)
                    .GroupBy(c => c.Name)
                    .ToDictionary(g => g.Key, g => g.First().Id);

                foreach (var cat in src.ListCategories())
                {
                    if (cat.Id == NativeMethods.UncatId) { catMap[cat.Id] = NativeMethods.UncatId; continue; }

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
                        .Where(id => catMap.TryGetValue(id, out _))
                        .Select(id => catMap[id])
                        .Distinct()
                        .ToList();
                    if (catIds.Count == 0) catIds.Add(NativeMethods.UncatId);

                    long nid = store.AddEntry(catIds, full.Account ?? "", full.Password ?? "", full.Note ?? "");
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
        await ShowMessage($"导入完成：新增 {ok} 条记录，跳过 {skipped} 条。");
    }

    private async void ImportBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_store is not { } store) return;

        var methodDlg = new ImportMethodDialog();
        await ShowChildAsync(methodDlg);

        var method = methodDlg.TakeMethod();
        if (method == null) return;

        // 旧版库沿用原逻辑（选目录 + 旧版主密码 + 核心库合并）
        if (method == ImportMethod.Legacy) { await ImportLegacyAsync(); return; }

        var path = await PickImportFileAsync(method.Value);
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
        if (await ShowChildAsync(advDlg) != ContentDialogResult.Primary) return;

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
        await ShowMessage($"导入完成：新增 {result.Imported} 条，跳过 {result.Skipped} 条。");
    }

    // 按导入方式选择文件
    private async Task<string?> PickImportFileAsync(ImportMethod method)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary
        };
        foreach (var ext in method == ImportMethod.Csv ? new[] { ".csv", ".csvenc" } : new[] { ".json" })
            picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _ownerHwnd);

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    // 反复询问密码直至解密成功或取消
    private async Task<string?> DecryptWithPromptAsync(string path)
    {
        while (true)
        {
            var dlg = new PasswordInputDialog();
            dlg.Init("该文件为加密导出文件，请输入导出时使用的密码：");
            if (await ShowChildAsync(dlg) != ContentDialogResult.Primary) return null;

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
        // 对话框内部完成密码二次校验后 Hide()，未通过则无请求
        await ShowChildAsync(dlg);

        var request = dlg.TakeRequest();
        if (request == null) return;

        // CSV 类导出前先收集分类筛选与字段名偏好
        if (request.Method is ExportMethod.Csv or ExportMethod.EncryptedCsv)
        {
            var optDlg = new CsvExportOptionsDialog();
            optDlg.Init(store);
            await ShowChildAsync(optDlg);
            var prefs = optDlg.TakePrefs();
            if (prefs == null) return; // 取消
            request.CsvPrefs = prefs;
        }

        var target = await PickExportTargetAsync(request);
        if (target == null) return;

        try
        {
            var result = await Task.Run(() => new ExportService(store).Execute(request, target));
            await ShowMessage(SuccessMessage(request, result));
        }
        catch (Exception ex)
        {
            Trace($"export EXCEPTION: {ex}");
            await ShowMessage($"导出失败：{ex.Message}");
        }
        finally
        {
            request.Clear(); // 丢弃加密密码引用
        }
    }

    // 按导出方式选择目标
    private async Task<string?> PickExportTargetAsync(ExportRequest request)
    {
        if (request.Method == ExportMethod.DataDirectory)
        {
            var folderPicker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary
            };
            folderPicker.FileTypeFilter.Add("*"); // FolderPicker 必须至少一个过滤项才能弹出
            WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, _ownerHwnd);

            var folder = await folderPicker.PickSingleFolderAsync();
            // 保留 data 目录名，便于整体拷贝回迁
            return folder == null ? null : Path.Combine(folder.Path, "data");
        }

        // FileSavePicker 扩展名只能单段
        var (label, ext) = request.Method switch
        {
            ExportMethod.Csv => ("CSV 文件", ".csv"),
            ExportMethod.EncryptedCsv => ("加密 CSV 文件", ".csvenc"),
            ExportMethod.EncryptedDataDirectory => ("加密备份文件", ".ksbak"),
            _ => ("导出文件", ".bin")
        };

        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"keysbox_{DateTime.Now:yyyyMMdd_HHmmss}{ext}"
        };
        picker.FileTypeChoices.Add(label, new List<string> { ext });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _ownerHwnd);

        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    private static string SuccessMessage(ExportRequest request, ExportResult result)
    {
        var tail = request.IsEncrypted ? "请牢记加密密码，密码丢失后无法恢复。" : "请妥善保管导出文件。";
        return request.IsDirectoryExport
            ? $"已导出 {result.Count} 个文件到：{result.Target}{Environment.NewLine}{tail}"
            : $"已导出 {result.Count} 条记录到：{result.Target}{Environment.NewLine}{tail}";
    }

    #endregion

    #region 辅助

    internal static void Trace(string msg)
    {
        if (!AppPaths.TraceEnabled) return; // 仅诊断模式下记录
        try { System.IO.File.AppendAllText(AppPaths.TraceLog, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); }
        catch { }
    }

    // 页面内弹出子对话框：直接使用本页 XamlRoot
    private async Task<ContentDialogResult> ShowChildAsync(ContentDialog child)
    {
        child.XamlRoot = XamlRoot;
        ThemeDialog(child);
        return await child.ShowAsync();
    }

    private void ThemeDialog(ContentDialog dlg)
    {
        dlg.RequestedTheme = ActualTheme;
        dlg.CornerRadius = new CornerRadius(AppSettings.DialogCornerRadius);
    }

    private async Task ShowMessage(string text)
    {
        await ShowChildAsync(new ContentDialog
        {
            Title = "KeySecBox",
            Content = text,
            CloseButtonText = "确定"
        });
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    #endregion
}
