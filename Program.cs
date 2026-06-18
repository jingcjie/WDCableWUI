using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace WDCableWUI;

public static class Program
{
    private const string InstanceKey = "WDCable.MainInstance";
    private const uint Infinite = 0xFFFFFFFF;

    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (RedirectToExistingInstance())
        {
            return 0;
        }

        Application.Start(initializationCallbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        return 0;
    }

    private static bool RedirectToExistingInstance()
    {
        var activationArguments = AppInstance.GetCurrent().GetActivatedEventArgs();
        var mainInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (mainInstance.IsCurrent)
        {
            mainInstance.Activated += OnActivated;
            return false;
        }

        RedirectActivation(activationArguments, mainInstance);
        return true;
    }

    private static void RedirectActivation(
        AppActivationArguments activationArguments,
        AppInstance mainInstance)
    {
        var redirectCompleted = CreateEvent(
            IntPtr.Zero,
            bManualReset: true,
            bInitialState: false,
            lpName: null);

        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await mainInstance.RedirectActivationToAsync(activationArguments);
                }
                finally
                {
                    SetEvent(redirectCompleted);
                }
            });

            _ = CoWaitForMultipleObjects(
                dwFlags: 0,
                dwMilliseconds: Infinite,
                nHandles: 1,
                pHandles: [redirectCompleted],
                out _);

            using var process = Process.GetProcessById((int)mainInstance.ProcessId);
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                ShowWindow(process.MainWindowHandle, 9);
                SetForegroundWindow(process.MainWindowHandle);
            }
        }
        finally
        {
            if (redirectCompleted != IntPtr.Zero)
            {
                CloseHandle(redirectCompleted);
            }
        }
    }

    private static void OnActivated(object? sender, AppActivationArguments args)
    {
        var window = App.MainWindow;
        window?.DispatcherQueue.TryEnqueue(() =>
        {
            window.Activate();
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            ShowWindow(handle, 9);
            SetForegroundWindow(handle);
        });
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEvent(
        IntPtr lpEventAttributes,
        bool bManualReset,
        bool bInitialState,
        string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(
        uint dwFlags,
        uint dwMilliseconds,
        ulong nHandles,
        IntPtr[] pHandles,
        out uint pdwIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
