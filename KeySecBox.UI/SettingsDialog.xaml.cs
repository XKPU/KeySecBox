using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KeySecBox;

public sealed partial class SettingsDialog : ContentDialog
{
    private NativeMethods.Store? _store;
    private Action<ThemeMode>? _applyTheme;
    private IntPtr _ownerHwnd;
    private Action? _onDataChanged;

    #region 初始化

    public SettingsDialog()
    {
        InitializeComponent();
        PrimaryButtonClick += OnPrimaryButtonClick;
        Loaded += (_, _) => DialogAnim.Play(this);
    }

    internal void Init(NativeMethods.Store store, Action<ThemeMode> applyTheme, IntPtr ownerHwnd, Action? onDataChanged = null)
    {
        _store = store;
        _applyTheme = applyTheme;
        _ownerHwnd = ownerHwnd;
        _onDataChanged = onDataChanged;

        ThemePicker.SelectedIndex = AppSettings.Theme switch
        {
            ThemeMode.Light => 1,
            ThemeMode.Dark => 2,
            _ => 0
        };

        // 帧率滑块：min 1, max 显示器刷新率
        int maxRate = AppSettings.MonitorRefreshRate;
        FrameRateSlider.Minimum = 1;
        FrameRateSlider.Maximum = maxRate;
        FrameRateSlider.Value = Math.Min(AppSettings.FrameRate, maxRate);
        UpdateFrameRateHint();

        DiagToggle.IsOn = store.GetDiagnostics();

        LoadAppearance();

        VersionText.Text = $"KeySecBox v{AppVersion}";
    }

    private bool _loadingAppearance;

    private void LoadAppearance()
    {
        _loadingAppearance = true;

        DialogCornerSlider.Value = AppSettings.DialogCornerRadius;
        UpdateDialogCornerHint();

        _loadingAppearance = false;
    }

    private void UpdateDialogCornerHint()
        => DialogCornerHint.Text = $"当前：{(int)Math.Round(DialogCornerSlider.Value)} px（0 为直角）";

    private void OnDialogCornerChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loadingAppearance) return;
        UpdateDialogCornerHint();
        AppSettings.DialogCornerRadius = (int)Math.Round(DialogCornerSlider.Value);
        CornerRadius = new CornerRadius(AppSettings.DialogCornerRadius); // 即时预览当前设置对话框
    }

    // 从程序集文件属性读取版本（由构建时 version.txt 注入）
    private static string AppVersion
    {
        get
        {
            try
            {
                var loc = typeof(SettingsDialog).Assembly.Location;
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(loc);
                if (!string.IsNullOrEmpty(info.FileVersion)) return info.FileVersion;
            }
            catch { }
            return "?";
        }
    }

    private void UpdateFrameRateHint()
    {
        int fps = (int)Math.Round(FrameRateSlider.Value);
        int maxRate = AppSettings.MonitorRefreshRate;
        FrameRateHint.Text = $"当前：{fps} fps（上限 {maxRate} Hz）";
    }

    private void OnFrameRateChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs args)
    {
        UpdateFrameRateHint();
    }

    #endregion

    #region 改密

    private async void ChangePwdBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_store is not { } store) return;
        var root = XamlRoot;   // 关闭设置前先捕获，供后续对话框使用
        var dlg = new ChangePasswordDialog();
        dlg.Init(store);
        dlg.XamlRoot = root;
        ThemeDialog(dlg);
        Hide();
        await dlg.ShowAsync();
        if (!dlg.Succeeded) return;

        // 改密成功后，用新密码重包恢复记录（不修改恢复方式本身）
        if (dlg.NewMaster is { } newMaster && RecoveryManager.GetConfig().Any)
            await RePackRecoveryAsync(root, newMaster);

        var info = new ContentDialog
        {
            XamlRoot = root,
            Title = "KeySecBox",
            Content = "密码已修改，所有条目已用新密码重新加密。",
            CloseButtonText = "确定"
        };
        ThemeDialog(info);
        await info.ShowAsync();
    }

    #endregion

    #region 恢复方式

    // 设置中重新配置恢复方式：需先验证当前主密码
    private async void RecoveryCfgBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_store is not { } store) return;
        var rdlg = new RecoverySetupDialog();
        rdlg.Init(null, store); // 传入 store，对话框内验证当前主密码
        if (await ShowChildAsync(rdlg) != ContentDialogResult.Primary) return;
    }

    // 忘记密码：立即恢复，取回的主密码填入改密对话框旧密码框，引导改一个新密码
    private async void ForgotPwdBtn_Click(object sender, RoutedEventArgs e)
    {
        var root = XamlRoot;
        var fdlg = new ForgotPasswordDialog();
        await ShowChildAsync(fdlg);
        if (string.IsNullOrEmpty(fdlg.RecoveredMaster))
        {
            await ShowMessage("未能取回主密码。");
            return;
        }
        string recovered = fdlg.RecoveredMaster;
        fdlg.RecoveredMaster = null;

        var cdlg = new ChangePasswordDialog();
        cdlg.Init(_store!);
        cdlg.SetOldPassword(recovered);
        cdlg.XamlRoot = root;
        ThemeDialog(cdlg);
        Hide();
        await cdlg.ShowAsync();
        if (cdlg.Succeeded && cdlg.NewMaster is { } nm && RecoveryManager.GetConfig().Any)
            await RePackRecoveryAsync(root, nm);
    }

    private async Task RePackRecoveryAsync(XamlRoot root, string newMaster)
    {
        try
        {
            // 改密后取回库必须同步更新（旧记录对应旧密码），仅「更新」可完成
            var rdlg = new RecoverySetupDialog();
            rdlg.Init(newMaster, null, updateMode: true);
            rdlg.XamlRoot = root;
            ThemeDialog(rdlg);
            await rdlg.ShowAsync();
        }
        catch (Exception ex)
        {
            Trace($"repack recovery EX: {ex.Message}");
        }
    }

    #endregion

    #region 导入导出

    private async void ImportOldDataBtn_Click(object sender, RoutedEventArgs e) => await ImportLegacyAsync();

    /// <summary>导入旧版库（1.0.x）：选定旧版 data 目录后用原逻辑合并进当前库。</summary>
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
            // 不论扩展名一律先判断是否加密：加密导出需密码解密后再解析
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

    /// <summary>按导入方式选择文件：CSV 允许 .csv 与 .csvenc，JSON 仅 .json。</summary>
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

    /// <summary>加密导出文件：反复询问密码直至解密成功或用户取消。</summary>
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

    /// <summary>按导出方式选择目标：目录类选文件夹，其余选文件。</summary>
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

        // 注意：FileSavePicker 的扩展名只能是单段（不允许 ".csv.enc" 这类多段）
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

    #region 保存

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true; // 校验通过后由异步流程关闭
        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        // 主题
        var theme = ThemePicker.SelectedIndex switch
        {
            1 => ThemeMode.Light,
            2 => ThemeMode.Dark,
            _ => ThemeMode.System
        };
        AppSettings.Theme = theme;
        _applyTheme?.Invoke(theme);

        // 动画帧率
        int fps = (int)Math.Round(FrameRateSlider.Value);
        AppSettings.FrameRate = fps;

        if (_store is { } store)
        {
            // 诊断模式
            bool diag = DiagToggle.IsOn; // UI 线程取值，后台线程严禁触碰 UI 元素
            int drc = await Task.Run(() => store.SetDiagnostics(diag));
            if (drc != NativeMethods.KSBOX_OK)
            {
                StatusText.Foreground = LookupBrush("SystemControlErrorTextForegroundBrush", Windows.UI.Color.FromArgb(255, 0xC4, 0x2B, 0x1C));
                StatusText.Text = $"保存诊断设置失败（错误码 {drc}）。";
                StatusText.Visibility = Visibility.Visible;
                return;
            }
            AppPaths.TraceEnabled = store.GetDiagnostics(); // 同步运行期追踪开关
        }

        StatusText.Foreground = LookupBrush("AccentTextFillColorPrimaryBrush", Windows.UI.Color.FromArgb(255, 0x67, 0x50, 0xA4));
        StatusText.Text = "设置已保存。";
        StatusText.Visibility = Visibility.Visible;
        Hide();
    }

    #endregion

    #region 辅助

    internal static void Trace(string msg)
    {
        if (!AppPaths.TraceEnabled) return; // 仅诊断模式下记录
        try { System.IO.File.AppendAllText(AppPaths.TraceLog, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); }
        catch { }
    }

    // 同一 XamlRoot 同时只允许一个 ContentDialog：显示子对话框前必须先收起自身。
    private async Task<ContentDialogResult> ShowChildAsync(ContentDialog child)
    {
        var root = XamlRoot;
        Hide();
        child.XamlRoot = root;
        ThemeDialog(child);
        return await child.ShowAsync();
    }

    // ContentDialog 不继承父对话框主题，显式套用
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

    private static Microsoft.UI.Xaml.Media.Brush LookupBrush(string key,
        Windows.UI.Color fallback)
    {
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out var value)
                && value is Microsoft.UI.Xaml.Media.Brush brush)
                return brush;
        }
        catch
        {
        }

        return new Microsoft.UI.Xaml.Media.SolidColorBrush(fallback);
    }

    #endregion
}
