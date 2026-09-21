using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Input;
using BlinkObserverTool.Models;

namespace BlinkObserverTool.Services;

internal sealed class KeyboardSendService
{
    private readonly TargetWindowFinder targetWindowFinder;

    public KeyboardSendService(TargetWindowFinder targetWindowFinder)
    {
        this.targetWindowFinder = targetWindowFinder;
    }

    public string SendKey(BlinkSendConfiguration configuration)
    {
        var targetWindow = targetWindowFinder.FindWindow(configuration.TargetProcessName, configuration.TargetWindowTitleContains);
        if (targetWindow is null)
        {
            throw new InvalidOperationException("送信対象ウィンドウが見つかりません。プロセス名またはウィンドウタイトルを確認してください。");
        }

        if (configuration.BringTargetToForeground)
        {
            var wasForeground = targetWindowFinder.IsForegroundWindow(targetWindow.Handle);
            if (!targetWindowFinder.TryActivateWindow(targetWindow.Handle))
            {
                throw new InvalidOperationException("送信対象ウィンドウを前面にできませんでした。");
            }

            if (!wasForeground)
            {
                Thread.Sleep(60);
            }
        }

        var virtualKey = ResolveVirtualKey(configuration);
        if (virtualKey == 0)
        {
            throw new InvalidOperationException($"送信キー '{configuration.SendKey}' は有効な仮想キーへ変換できません。");
        }

        var inputArray = CreateInputs(configuration, virtualKey);
        var written = SendInput((uint)inputArray.Length, inputArray, Marshal.SizeOf<INPUT>());
        if (written != inputArray.Length)
        {
            var errorCode = Marshal.GetLastWin32Error();
            throw new Win32Exception(errorCode, $"キー送信に失敗しました。Mode={ResolveSendMode(configuration.SendMode, configuration.SendKey)} Win32={errorCode}");
        }

        return $"{targetWindow.ProcessName} / {targetWindow.WindowTitle}";
    }

    private static INPUT[] CreateInputs(BlinkSendConfiguration configuration, ushort virtualKey)
    {
        var resolvedMode = ResolveSendMode(configuration.SendMode, configuration.SendKey);
        return resolvedMode switch
        {
            KeySendMode.VirtualKeyTap => CreateVirtualKeyInputs(virtualKey, configuration),
            KeySendMode.ScanCodeTap => CreateScanCodeInputs(virtualKey, configuration),
            KeySendMode.ScanCodeHold => CreateScanCodeHoldInputs(virtualKey, configuration),
            KeySendMode.InterceptionTap or KeySendMode.InterceptionHold => throw new InvalidOperationException("InputInterceptor 送信は PhysicalKeyboardSendService を使用してください。"),
            _ => CreateVirtualKeyInputs(virtualKey, configuration)
        };
    }

    private static KeySendMode ResolveSendMode(KeySendMode configuredMode, Key key)
    {
        if (configuredMode != KeySendMode.Auto)
        {
            return configuredMode;
        }

        return IsModifierKey(key)
            ? KeySendMode.ScanCodeTap
            : KeySendMode.VirtualKeyTap;
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.LeftShift or Key.RightShift
            or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin;
    }

    private static INPUT[] CreateVirtualKeyInputs(ushort virtualKey, BlinkSendConfiguration configuration)
    {
        return configuration.SendTrigger switch
        {
            KeySendTrigger.KeyDownOnly => [INPUT.CreateKeyboardInput(virtualKey, 0)],
            KeySendTrigger.KeyUpOnly => [INPUT.CreateKeyboardInput(virtualKey, KEYEVENTF_KEYUP)],
            _ => [INPUT.CreateKeyboardInput(virtualKey, 0), INPUT.CreateKeyboardInput(virtualKey, KEYEVENTF_KEYUP)]
        };
    }

    private static INPUT[] CreateScanCodeInputs(ushort virtualKey, BlinkSendConfiguration configuration)
    {
        var scanCode = GetScanCode(virtualKey);
        var extended = IsExtendedVirtualKey(virtualKey) ? KEYEVENTF_EXTENDEDKEY : 0u;
        return configuration.SendTrigger switch
        {
            KeySendTrigger.KeyDownOnly => [INPUT.CreateScanCodeInput(scanCode, extended)],
            KeySendTrigger.KeyUpOnly => [INPUT.CreateScanCodeInput(scanCode, extended | KEYEVENTF_KEYUP)],
            _ => [INPUT.CreateScanCodeInput(scanCode, extended), INPUT.CreateScanCodeInput(scanCode, extended | KEYEVENTF_KEYUP)]
        };
    }

    private static INPUT[] CreateScanCodeHoldInputs(ushort virtualKey, BlinkSendConfiguration configuration)
    {
        var scanCode = GetScanCode(virtualKey);
        var extended = IsExtendedVirtualKey(virtualKey) ? KEYEVENTF_EXTENDEDKEY : 0u;
        if (configuration.SendTrigger == KeySendTrigger.KeyUpOnly)
        {
            return [INPUT.CreateScanCodeInput(scanCode, extended | KEYEVENTF_KEYUP)];
        }

        var down = INPUT.CreateScanCodeInput(scanCode, extended);
        if (configuration.SendTrigger == KeySendTrigger.KeyDownOnly)
        {
            return [down];
        }

        var inputs = new[] { down };
        var written = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (written != inputs.Length)
        {
            var errorCode = Marshal.GetLastWin32Error();
            throw new Win32Exception(errorCode, $"キー押下に失敗しました。Win32={errorCode}");
        }

        Thread.Sleep(Math.Max(1, configuration.KeyHoldMilliseconds));
        return [INPUT.CreateScanCodeInput(scanCode, extended | KEYEVENTF_KEYUP)];
    }

    private static ushort ResolveVirtualKey(BlinkSendConfiguration configuration)
    {
        if (configuration.SendModifierAsCommonKey)
        {
            return configuration.SendKey switch
            {
                Key.LeftShift or Key.RightShift => 0x10,
                Key.LeftCtrl or Key.RightCtrl => 0x11,
                Key.LeftAlt or Key.RightAlt => 0x12,
                _ => (ushort)KeyInterop.VirtualKeyFromKey(configuration.SendKey)
            };
        }

        return (ushort)KeyInterop.VirtualKeyFromKey(configuration.SendKey);
    }

    private static ushort GetScanCode(ushort virtualKey)
    {
        var scanCode = MapVirtualKey(virtualKey, MAPVK_VK_TO_VSC);
        if (scanCode == 0)
        {
            throw new InvalidOperationException($"送信キー 0x{virtualKey:X2} のスキャンコードが取得できません。");
        }

        return (ushort)scanCode;
    }

    private static bool IsExtendedVirtualKey(ushort virtualKey)
    {
        return virtualKey is 0x21 or 0x22 or 0x23 or 0x24
            or 0x25 or 0x26 or 0x27 or 0x28
            or 0x2D or 0x2E
            or 0x6F or 0x90 or 0x91
            or 0xA3 or 0xA5;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint MAPVK_VK_TO_VSC = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;

        public static INPUT CreateKeyboardInput(ushort virtualKey, uint flags)
        {
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = virtualKey,
                        dwFlags = flags
                    }
                }
            };
        }

        public static INPUT CreateScanCodeInput(ushort scanCode, uint flags)
        {
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = scanCode,
                        dwFlags = flags | KEYEVENTF_SCANCODE
                    }
                }
            };
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;

        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint cInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}
