using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using BlinkObserverTool.Models;

namespace BlinkObserverTool.Services;

internal sealed class TargetWindowFinder
{
    public IReadOnlyList<TargetWindowInfo> GetCandidateWindows()
    {
        var results = new List<TargetWindowInfo>();
        EnumWindows((windowHandle, _) =>
        {
            if (!IsWindowVisible(windowHandle))
            {
                return true;
            }

            var title = GetWindowTitle(windowHandle);
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            GetWindowThreadProcessId(windowHandle, out var processId);
            Process? process;
            try
            {
                process = Process.GetProcessById((int)processId);
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }

            results.Add(new TargetWindowInfo
            {
                Handle = windowHandle,
                ProcessId = process.Id,
                ProcessName = process.ProcessName,
                WindowTitle = title
            });
            return true;
        }, nint.Zero);

        return results
            .OrderBy(window => window.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(window => window.WindowTitle, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public TargetWindowInfo? FindWindow(string processName, string windowTitleContains)
    {
        var normalizedProcessName = Normalize(processName);
        var normalizedWindowTitle = Normalize(windowTitleContains);

        return GetCandidateWindows().FirstOrDefault(window =>
            (string.IsNullOrWhiteSpace(normalizedProcessName)
             || string.Equals(window.ProcessName, normalizedProcessName, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(normalizedWindowTitle)
                || window.WindowTitle.Contains(normalizedWindowTitle, StringComparison.OrdinalIgnoreCase)));
    }

    public bool TryActivateWindow(nint windowHandle)
    {
        if (windowHandle == nint.Zero)
        {
            return false;
        }

        if (IsForegroundWindow(windowHandle))
        {
            return true;
        }

        ShowWindow(windowHandle, 9);
        return SetForegroundWindow(windowHandle);
    }

    public bool IsForegroundWindow(nint windowHandle)
    {
        return windowHandle != nint.Zero && GetForegroundWindow() == windowHandle;
    }

    private static string Normalize(string value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static string GetWindowTitle(nint windowHandle)
    {
        var builder = new StringBuilder(512);
        _ = GetWindowText(windowHandle, builder, builder.Capacity);
        return builder.ToString();
    }

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
