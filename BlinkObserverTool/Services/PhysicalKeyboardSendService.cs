using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Input;
using BlinkObserverTool.Models;
using InputInterceptorNS;

namespace BlinkObserverTool.Services;

internal sealed class PhysicalKeyboardSendService : IDisposable
{
    private readonly TargetWindowFinder targetWindowFinder;
    private readonly object sendSync = new();
    private KeyboardHook? keyboardHook;
    private bool isInitialized;
    private bool hasCapturedKeyboardDevice;
    private KeyCode? lastKeyDownCode;

    public PhysicalKeyboardSendService(TargetWindowFinder targetWindowFinder)
    {
        this.targetWindowFinder = targetWindowFinder;
    }

    public bool IsDriverInstalled()
    {
        return InputInterceptor.CheckDriverInstalled();
    }

    public bool CanInstallDriver()
    {
        return InputInterceptor.CheckAdministratorRights();
    }

    public string GetDriverStatus()
    {
        if (!IsDriverInstalled())
        {
            return "未導入";
        }

        if (!TryProbeKeyboardDevices(out var hasKeyboardDevice) || !hasKeyboardDevice)
        {
            return "導入済み(再起動待ち/デバイス未検出)";
        }

        if (!isInitialized || keyboardHook is null)
        {
            return "導入済み(送信準備前)";
        }

        if (keyboardHook.HasException)
        {
            return $"初期化例外: {keyboardHook.Exception?.Message}";
        }

        return hasCapturedKeyboardDevice || keyboardHook.CanSimulateInput
            ? "利用可能"
            : "導入済み(物理キーボード入力待ち)";
    }

    public string InstallDriver()
    {
        if (IsDriverInstalled())
        {
            return "InputInterceptor ドライバは既に導入済みです。";
        }

        if (!CanInstallDriver())
        {
            throw new InvalidOperationException("InputInterceptor ドライバの導入には管理者権限が必要です。管理者として再実行してください。");
        }

        if (!InputInterceptor.InstallDriver())
        {
            throw new InvalidOperationException("InputInterceptor ドライバの導入に失敗しました。");
        }

        return "InputInterceptor ドライバを導入しました。必要に応じて再起動後に再試行してください。";
    }

    public string SendKey(BlinkSendConfiguration configuration)
    {
        lock (sendSync)
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

            EnsureReady();
            EnsureKeyboardDeviceCaptured();
            var keyCode = ConvertKey(NormalizeModifierKey(configuration));
            var holdMilliseconds = Math.Max(1, configuration.KeyHoldMilliseconds);
            try
            {
                SendTrigger(configuration.SendMode, keyCode, configuration.SendTrigger, holdMilliseconds);
            }
            catch
            {
                _ = ReleaseAllSimulatedKeys();
                throw;
            }

            return $"{targetWindow.ProcessName} / {targetWindow.WindowTitle} (InputInterceptor)";
        }
    }

    public string ReleaseAllSimulatedKeys()
    {
        lock (sendSync)
        {
            var released = 0;
            if (!isInitialized || keyboardHook is null)
            {
                lastKeyDownCode = null;
                ReleaseGlobalModifierState();
                return "物理キー状態を解放しました。(送信未初期化/OS修飾キー解放実行)";
            }

            foreach (var keyCode in EnumerateReleaseTargets())
            {
                if (TrySetKeyState(keyCode, KeyState.Up, retryCount: 2))
                {
                    released++;
                }
            }

            lastKeyDownCode = null;
            ReleaseGlobalModifierState();
            return $"物理キー状態を解放しました。(解放試行: {released} キー + OS修飾キー解放)";
        }
    }

    public void Dispose()
    {
        ReleaseAllSimulatedKeys();
        ResetSession();
    }

    private void SendTrigger(KeySendMode sendMode, KeyCode keyCode, KeySendTrigger trigger, int holdMilliseconds)
    {
        if (sendMode == KeySendMode.InterceptionTap)
        {
            SendTapTrigger(keyCode, trigger, holdMilliseconds);
            return;
        }

        if (trigger == KeySendTrigger.KeyUpOnly)
        {
            if (!TrySetKeyState(keyCode, KeyState.Up, retryCount: 3))
            {
                throw new InvalidOperationException("InputInterceptor によるキー解放に失敗しました。");
            }

            EnsureModifierFamilyReleased(keyCode);
            if (lastKeyDownCode == keyCode)
            {
                lastKeyDownCode = null;
            }

            return;
        }

        // 取りこぼしで押しっぱなしになっていた場合を先に解消して、次の押下エッジを確実に作る。
        ReleaseGlobalModifierState();
        _ = TrySetKeyState(keyCode, KeyState.Up, retryCount: 1);
        if (!TrySetKeyState(keyCode, KeyState.Down, retryCount: 3))
        {
            throw new InvalidOperationException("InputInterceptor によるキー押下に失敗しました。");
        }

        lastKeyDownCode = keyCode;
        if (trigger == KeySendTrigger.KeyDownOnly)
        {
            if (IsModifierKeyCode(keyCode))
            {
                Thread.Sleep(Math.Min(holdMilliseconds, 80));
                if (!TrySetKeyState(keyCode, KeyState.Up, retryCount: 3))
                {
                    throw new InvalidOperationException("InputInterceptor による修飾キー解放に失敗しました。");
                }

                lastKeyDownCode = null;
                EnsureModifierFamilyReleased(keyCode);
                ReleaseGlobalModifierState();
            }

            return;
        }

        Thread.Sleep(holdMilliseconds);
        if (!TrySetKeyState(keyCode, KeyState.Up, retryCount: 3))
        {
            throw new InvalidOperationException("InputInterceptor によるキー解放に失敗しました。");
        }

        lastKeyDownCode = null;
        EnsureModifierFamilyReleased(keyCode);
        ReleaseGlobalModifierState();
    }

    private bool TrySetKeyState(KeyCode keyCode, KeyState keyState, int retryCount)
    {
        if (keyboardHook is null)
        {
            return false;
        }

        for (var index = 0; index <= retryCount; index++)
        {
            if (keyboardHook.SetKeyState(keyCode, keyState))
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return false;
    }

    private void SendTapTrigger(KeyCode keyCode, KeySendTrigger trigger, int holdMilliseconds)
    {
        if (keyboardHook is null)
        {
            throw new InvalidOperationException("InputInterceptor のキーボードフックが初期化されていません。");
        }

        var succeeded = trigger switch
        {
            KeySendTrigger.KeyDownOnly => keyboardHook.SimulateKeyDown(keyCode),
            KeySendTrigger.KeyUpOnly => keyboardHook.SimulateKeyUp(keyCode),
            _ => keyboardHook.SimulateKeyPress(keyCode, holdMilliseconds)
        };
        if (!succeeded)
        {
            throw new InvalidOperationException("InputInterceptor によるキー送信に失敗しました。");
        }

        if (trigger == KeySendTrigger.KeyDownOnly)
        {
            lastKeyDownCode = keyCode;
            if (IsModifierKeyCode(keyCode))
            {
                Thread.Sleep(Math.Min(holdMilliseconds, 80));
                if (!keyboardHook.SimulateKeyUp(keyCode))
                {
                    throw new InvalidOperationException("InputInterceptor による修飾キー解放に失敗しました。");
                }

                lastKeyDownCode = null;
            }
        }
        else
        {
            if (lastKeyDownCode == keyCode)
            {
                lastKeyDownCode = null;
            }
        }

        EnsureModifierFamilyReleased(keyCode);
        ReleaseGlobalModifierState();
    }

    private IEnumerable<KeyCode> EnumerateReleaseTargets()
    {
        if (lastKeyDownCode is not null)
        {
            yield return lastKeyDownCode.Value;
        }

        yield return KeyCode.LeftShift;
        yield return KeyCode.RightShift;
        yield return KeyCode.Control;
        yield return KeyCode.Alt;
    }

    private static bool IsModifierKeyCode(KeyCode keyCode)
    {
        return keyCode is KeyCode.LeftShift or KeyCode.RightShift or KeyCode.Control or KeyCode.Alt;
    }

    private void EnsureModifierFamilyReleased(KeyCode keyCode)
    {
        if (keyboardHook is null || !IsModifierKeyCode(keyCode))
        {
            return;
        }

        switch (keyCode)
        {
            case KeyCode.LeftShift:
            case KeyCode.RightShift:
                _ = TrySetKeyState(KeyCode.LeftShift, KeyState.Up, retryCount: 1);
                _ = TrySetKeyState(KeyCode.RightShift, KeyState.Up, retryCount: 1);
                break;
            case KeyCode.Control:
                _ = TrySetKeyState(KeyCode.Control, KeyState.Up, retryCount: 1);
                break;
            case KeyCode.Alt:
                _ = TrySetKeyState(KeyCode.Alt, KeyState.Up, retryCount: 1);
                break;
        }
    }

    private static void ReleaseGlobalModifierState()
    {
        // InputInterceptor 側で解放が取りこぼされた場合でも、OS の修飾キー状態をリセットする。
        SendVirtualKeyUp(0xA0); // VK_LSHIFT
        SendVirtualKeyUp(0xA1); // VK_RSHIFT
        SendVirtualKeyUp(0x10); // VK_SHIFT
        SendVirtualKeyUp(0xA2); // VK_LCONTROL
        SendVirtualKeyUp(0xA3); // VK_RCONTROL
        SendVirtualKeyUp(0x11); // VK_CONTROL
        SendVirtualKeyUp(0xA4); // VK_LMENU
        SendVirtualKeyUp(0xA5); // VK_RMENU
        SendVirtualKeyUp(0x12); // VK_MENU
    }

    private static void SendVirtualKeyUp(byte virtualKey)
    {
        keybd_event(virtualKey, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private void EnsureReady()
    {
        if (!IsDriverInstalled())
        {
            throw new InvalidOperationException("InputInterceptor ドライバが未導入です。本体ウィンドウの『物理入力ドライバ導入』を実行してください。");
        }

        if (isInitialized && keyboardHook is not null)
        {
            if (keyboardHook.Context != IntPtr.Zero && !keyboardHook.HasException)
            {
                return;
            }

            ResetSession();
        }

        if (isInitialized)
        {
            return;
        }

        if (!InputInterceptor.Initialize())
        {
            throw new InvalidOperationException("InputInterceptor の初期化に失敗しました。再起動後に再試行してください。");
        }

        if (!TryGetKeyboardDevices(out var hasKeyboardDevice) || !hasKeyboardDevice)
        {
            InputInterceptor.Dispose();
            throw new InvalidOperationException("InputInterceptor ドライバは導入済みですが、まだキーボードデバイスを初期化できていません。PC を再起動してから再試行してください。");
        }

        keyboardHook = new KeyboardHook(KeyboardFilter.All, CaptureKeyboardDevice);
        if (keyboardHook.Context == IntPtr.Zero || keyboardHook.HasException)
        {
            throw new InvalidOperationException(
                keyboardHook.Exception is null
                    ? "InputInterceptor のキーボードフック初期化に失敗しました。"
                    : $"InputInterceptor のキーボードフック初期化に失敗しました: {keyboardHook.Exception.Message}");
        }

        isInitialized = true;
    }

    private void ResetSession()
    {
        keyboardHook?.Dispose();
        keyboardHook = null;
        hasCapturedKeyboardDevice = false;
        lastKeyDownCode = null;
        if (isInitialized)
        {
            InputInterceptor.Dispose();
            isInitialized = false;
        }
    }

    private void EnsureKeyboardDeviceCaptured()
    {
        if (keyboardHook is null)
        {
            throw new InvalidOperationException("InputInterceptor のキーボードフックが初期化されていません。");
        }

        hasCapturedKeyboardDevice = hasCapturedKeyboardDevice || keyboardHook.CanSimulateInput;
        if (!hasCapturedKeyboardDevice)
        {
            throw new InvalidOperationException("InputInterceptor はまだ物理キーボードを捕捉できていません。BlinkObserverTool 起動後に実キーボードで Shift などを1回押してから再試行してください。");
        }
    }

    private void CaptureKeyboardDevice(ref KeyStroke keyStroke)
    {
        hasCapturedKeyboardDevice = true;
    }

    private static Key NormalizeModifierKey(BlinkSendConfiguration configuration)
    {
        if (!configuration.SendModifierAsCommonKey)
        {
            return configuration.SendKey;
        }

        return configuration.SendKey switch
        {
            Key.RightShift => Key.LeftShift,
            Key.RightCtrl => Key.LeftCtrl,
            Key.RightAlt => Key.LeftAlt,
            _ => configuration.SendKey
        };
    }

    private static bool TryProbeKeyboardDevices(out bool hasKeyboardDevice)
    {
        hasKeyboardDevice = false;
        var initializedForProbe = false;
        if (InputInterceptor.Disposed)
        {
            if (!InputInterceptor.Initialize())
            {
                return false;
            }

            initializedForProbe = true;
        }

        try
        {
            return TryGetKeyboardDevices(out hasKeyboardDevice);
        }
        finally
        {
            if (initializedForProbe)
            {
                InputInterceptor.Dispose();
            }
        }
    }

    private static bool TryGetKeyboardDevices(out bool hasKeyboardDevice)
    {
        hasKeyboardDevice = false;
        var context = IntPtr.Zero;
        try
        {
            context = InputInterceptor.CreateContext();
            if (context == IntPtr.Zero)
            {
                return false;
            }

            var devices = InputInterceptor.GetDeviceList(context, InputInterceptor.IsKeyboard);
            hasKeyboardDevice = devices.Count > 0;
            return true;
        }
        catch (NullReferenceException)
        {
            return false;
        }
        finally
        {
            if (context != IntPtr.Zero)
            {
                InputInterceptor.DestroyContext(context);
            }
        }
    }

    private static KeyCode ConvertKey(Key key)
    {
        return key switch
        {
            Key.LeftShift => KeyCode.LeftShift,
            Key.RightShift => KeyCode.RightShift,
            Key.LeftCtrl => KeyCode.Control,
            Key.RightCtrl => KeyCode.Control,
            Key.LeftAlt => KeyCode.Alt,
            Key.RightAlt => KeyCode.Alt,
            Key.Space => KeyCode.Space,
            Key.Enter => KeyCode.Enter,
            Key.Tab => KeyCode.Tab,
            Key.D0 => KeyCode.Zero,
            Key.D1 => KeyCode.One,
            Key.D2 => KeyCode.Two,
            Key.D3 => KeyCode.Three,
            Key.D4 => KeyCode.Four,
            Key.D5 => KeyCode.Five,
            Key.D6 => KeyCode.Six,
            Key.D7 => KeyCode.Seven,
            Key.D8 => KeyCode.Eight,
            Key.D9 => KeyCode.Nine,
            Key.Up => KeyCode.Up,
            Key.Down => KeyCode.Down,
            Key.Left => KeyCode.Left,
            Key.Right => KeyCode.Right,
            Key.F1 => KeyCode.F1,
            Key.F2 => KeyCode.F2,
            Key.F3 => KeyCode.F3,
            Key.F4 => KeyCode.F4,
            Key.F5 => KeyCode.F5,
            Key.F6 => KeyCode.F6,
            Key.F7 => KeyCode.F7,
            Key.F8 => KeyCode.F8,
            Key.F9 => KeyCode.F9,
            Key.F10 => KeyCode.F10,
            Key.F11 => KeyCode.F11,
            Key.F12 => KeyCode.F12,
            Key.A => KeyCode.A,
            Key.B => KeyCode.B,
            Key.C => KeyCode.C,
            Key.D => KeyCode.D,
            Key.E => KeyCode.E,
            Key.F => KeyCode.F,
            Key.G => KeyCode.G,
            Key.H => KeyCode.H,
            Key.I => KeyCode.I,
            Key.J => KeyCode.J,
            Key.K => KeyCode.K,
            Key.L => KeyCode.L,
            Key.M => KeyCode.M,
            Key.N => KeyCode.N,
            Key.O => KeyCode.O,
            Key.P => KeyCode.P,
            Key.Q => KeyCode.Q,
            Key.R => KeyCode.R,
            Key.S => KeyCode.S,
            Key.T => KeyCode.T,
            Key.U => KeyCode.U,
            Key.V => KeyCode.V,
            Key.W => KeyCode.W,
            Key.X => KeyCode.X,
            Key.Y => KeyCode.Y,
            Key.Z => KeyCode.Z,
            _ => throw new InvalidOperationException($"InputInterceptor ではキー '{key}' をまだサポートしていません。")
        };
    }

    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
