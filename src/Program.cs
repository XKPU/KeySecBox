using Microsoft.UI.Dispatching;
using Microsoft.Windows.ApplicationModel.DynamicDependency;
using WinRT;

namespace KeySecBox;

public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}