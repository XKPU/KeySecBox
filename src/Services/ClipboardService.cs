using Microsoft.UI.Dispatching;

namespace KeySecBox;

public interface IClipboardService
{
    void CopyToClipboard(string text, bool sensitive = true);
}

public class ClipboardService : IClipboardService
{
    private DispatcherQueueTimer? _timer;

    public void CopyToClipboard(string text, bool sensitive = true)
    {
        Windows.ApplicationModel.DataTransfer.DataPackage package = new()
        {
            RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy
        };
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);

        if (sensitive)
        {
            _timer?.Stop();
            _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(60);
            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                try { Windows.ApplicationModel.DataTransfer.Clipboard.Clear(); } catch { }
            };
            _timer.Start();
        }
    }
}