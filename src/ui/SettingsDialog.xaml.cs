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

        VersionText.Text = $"KeySecBox v{AppVersion}";
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

    private async void ImportOldDataBtn_Click(object sender, RoutedEventArgs e)
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

        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add(".csv");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _ownerHwnd);

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        string content;
        try { content = await File.ReadAllTextAsync(file.Path); }
        catch { await ShowMessage("读取 CSV 文件失败。"); return; }

        // 宽泛解析：按表头列名映射，兼容常见密码管理器导出
        var parsed = ParseRows(content);
        if (parsed.Count == 0) { await ShowMessage("CSV 中没有可识别的数据。"); return; }

        var catDlg = new ImportDialog();
        catDlg.Init(store, store.ListCategories(), parsed.Count);
        if (await ShowChildAsync(catDlg) != ContentDialogResult.Primary) return;

        long ok = 0, skipped = 0;
        try
        {
            await Task.Run(() =>
            {
                for (int i = 0; i < parsed.Count; i++)
                {
                    var (account, password, note) = parsed[i];
                    if (string.IsNullOrWhiteSpace(account)) { skipped++; continue; }
                    if (store.AddEntry(new[] { catDlg.CategoryId }, account, password, note) > 0) ok++;
                    else skipped++;
                    // 每 500 条打点一次，崩溃时可定位进度
                    if ((i + 1) % 500 == 0)
                        System.IO.File.AppendAllText(AppPaths.TraceLog, $"[{DateTime.Now:HH:mm:ss}] import progress: {i + 1}/{parsed.Count}, ok={ok}, skip={skipped}\n");
                }
            });
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText(AppPaths.TraceLog, $"[{DateTime.Now:HH:mm:ss}] import EXCEPTION: {ex}\n");
            await ShowMessage($"导入写入失败：{ex.Message}");
            return;
        }
        if (ok > 0) store.Save();
        _onDataChanged?.Invoke(); // 刷新主界面
        await ShowMessage($"导入完成：新增 {ok} 条，跳过 {skipped} 条。");
    }

    // 返回 (账户, 密码, 备注)。识别表头列名并做字段映射。
    private static List<(string Account, string Password, string Note)> ParseRows(string csvText)
    {
        var rows = Csv.Parse(csvText);
        if (rows.Count == 0) return new();

        var header = rows[0];

        // 表头检测：首行是否包含常见列名（无大小写/前后空白差异）
        int[] MapCol(params string[] names)
        {
            var idx = new List<int>();
            for (int c = 0; c < header.Length; c++)
            {
                var h = header[c].Trim().ToLowerInvariant();
                if (names.Contains(h)) idx.Add(c);
            }
            return idx.ToArray();
        }

        var accCols = MapCol("account", "username", "user", "login", "email", "账户", "账号", "用户名");
        var pwdCols = MapCol("password", "密码");
        var urlCols = MapCol("url", "website", "web", "link", "site", "网址", "链接");
        var noteCols = MapCol("note", "notes", "comment", "comments", "描述", "备注", "说明", "注释");

        bool hasHeader = accCols.Length > 0 || pwdCols.Length > 0 || urlCols.Length > 0 || noteCols.Length > 0;
        var data = hasHeader ? rows.Skip(1).ToList() : rows;

        var result = new List<(string, string, string)>();
        foreach (var row in data)
        {
            if (row.Length == 0) continue;
            string GetCol(int[] col, int fallback = -1)
            {
                int c = col.Length > 0 ? col[0] : fallback;
                if (c < 0 || c >= row.Length) return "";
                return row[c].Trim();
            }

            if (!hasHeader)
            {
                // 无表头：按 账户,密码,备注 固定列序
                string a = row.Length > 0 ? row[0].Trim() : "";
                string p = row.Length > 1 ? row[1].Trim() : "";
                string n = row.Length > 2 ? row[2].Trim() : "";
                result.Add((a, p, n));
                continue;
            }

            string account = GetCol(accCols);
            if (account == "")
                account = GetCol(MapCol("name", "title", "名称", "标题")); // 无账户列时退化用 name
            string password = GetCol(pwdCols);

            // 备注 = 备注列；URL 并入备注，无备注列时备注即 URL
            var noteParts = new List<string>();
            string note = GetCol(noteCols);
            if (note != "") noteParts.Add(note);
            string url = GetCol(urlCols);
            if (url != "") noteParts.Add("URL: " + url);
            string finalNote = string.Join("\n", noteParts);

            if (string.IsNullOrWhiteSpace(account) && string.IsNullOrWhiteSpace(password))
                continue; // 跳过表头残留的空行/填充行

            result.Add((account, password, finalNote));
        }
        return result;
    }

    private async void ExportBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_store is not { } store) return;

        // 必须用 GetEntry 逐条取密码：QueryAll 刻意不输出 password 字段
        var all = store.QueryAll();
        if (all.Count == 0) { await ShowMessage("保险库中没有可导出的条目。"); return; }

        var dlg = new ExportDialog();
        dlg.Init(all.Count);
        // ExportDialog 内部校验通过会 Hide()，故只以 Verified 作为是否继续的依据
        await ShowChildAsync(dlg);
        if (!dlg.Verified) return;

        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"keysbox_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };
        picker.FileTypeChoices.Add("CSV 文件", new List<string> { ".csv" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _ownerHwnd);

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        var rows = new List<(string, string, string)>();
        await Task.Run(() =>
        {
            foreach (var ent in all)
            {
                var full = store.GetEntry(ent.Id); // 唯一返回密码的解密入口
                if (full == null) continue;
                rows.Add((full.Account ?? "", full.Password ?? "", full.Note ?? ""));
                full.Password = ""; full.Note = ""; full.Account = ""; // 立即断开引用
            }
        });

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("账户,密码,备注");
        foreach (var (acc, pwd, note) in rows)
        {
            sb.Append(Csv.Escape(acc)).Append(',').Append(Csv.Escape(pwd)).Append(',')
              .Append(Csv.Escape(note)).Append('\n');
        }

        try
        {
            await Windows.Storage.FileIO.WriteTextAsync(file, sb.ToString(),
                Windows.Storage.Streams.UnicodeEncoding.Utf8);
            await ShowMessage($"已导出 {all.Count} 条记录。请妥善保管该明文文件。");
        }
        catch
        {
            await ShowMessage("写入导出文件失败。");
        }
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
        dlg.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(12);
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
