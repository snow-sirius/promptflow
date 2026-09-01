using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;

namespace PromptFlow.Services;

public sealed class NativeHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312, WmXButtonDown = 0x020B;
    private const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModWin = 0x0008;
    private readonly Window _window;
    private HwndSource? _source;
    private int _id;
    private uint _mods;
    private uint _vk;
    private static IntPtr _hook;
    private static LowLevelMouseProc? _proc;
    public event EventHandler? Triggered;
    public event EventHandler<string>? RegistrationFailed;

    public NativeHotkeyService(Window window) => _window = window;
    public bool Register(string hotkey)
    {
        Unregister();
        if (!TryParse(hotkey, out _mods, out _vk, out var xButton)) { RegistrationFailed?.Invoke(this, "无法识别快捷键格式"); return false; }
        _source = (HwndSource)PresentationSource.FromVisual(_window)!;
        _source.AddHook(WndProc);
        if (xButton != 0)
        {
            _proc = HookCallback; _hook = SetWindowsHookEx(14, _proc, GetModuleHandle(null), 0); return _hook != IntPtr.Zero;
        }
        _id = Random.Shared.Next(0x1000, 0x7fff);
        if (!RegisterHotKey(_source.Handle, _id, _mods, _vk)) { RegistrationFailed?.Invoke(this, "快捷键已被系统或其他应用占用"); return false; }
        return true;
    }
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) { if(msg==WmHotkey && wParam.ToInt32()==_id){Triggered?.Invoke(this, EventArgs.Empty);handled=true;} return IntPtr.Zero; }
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam.ToInt32() == WmXButtonDown)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam); var button = (data.mouseData >> 16) & 0xffff;
            var expected = _vk == 0x1001 ? 1u : 2u;
            var mods = Keyboard.Modifiers;
            var current = (mods.HasFlag(ModifierKeys.Control) ? ModControl : 0) | (mods.HasFlag(ModifierKeys.Alt) ? ModAlt : 0) | (mods.HasFlag(ModifierKeys.Shift) ? ModShift : 0) | (mods.HasFlag(ModifierKeys.Windows) ? ModWin : 0);
            if (button == expected && current == _mods) Triggered?.Invoke(this, EventArgs.Empty);
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
    public void Unregister()
    {
        if (_source is not null) { if (_id != 0) UnregisterHotKey(_source.Handle, _id); _source.RemoveHook(WndProc); _source=null; }
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook=IntPtr.Zero; } _id=0;
    }
    private static bool TryParse(string value, out uint mods, out uint vk, out int xButton)
    {
        mods=0; vk=0; xButton=0; foreach(var token in value.Split('+', StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)){switch(token.ToLowerInvariant()){case "ctrl":case "control":mods|=ModControl;break;case "alt":mods|=ModAlt;break;case "shift":mods|=ModShift;break;case "win":case "windows":mods|=ModWin;break;case "xbutton1":vk=0x1001;xButton=1;break;case "xbutton2":vk=0x1002;xButton=2;break;default: if(!TryParseKey(token, out vk)) return false; break;}} return mods!=0 && vk!=0;
    }
    private static bool TryParseKey(string token, out uint vk)
    {
        vk = 0;
        if (token.Length == 1 && char.IsDigit(token[0])) { vk = (uint)KeyInterop.VirtualKeyFromKey(Key.D0 + (token[0] - '0')); return true; }
        if (Enum.TryParse<Key>(token, true, out var key)) { vk = (uint)KeyInterop.VirtualKeyFromKey(key); return vk != 0; }
        return false;
    }
    public void Dispose()=>Unregister();
    [StructLayout(LayoutKind.Sequential)] private struct POINT{public int x,y;} [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT{public POINT pt;public uint mouseData,flags,time;public IntPtr dwExtraInfo;}
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool RegisterHotKey(IntPtr hWnd,int id,uint fsModifiers,uint vk); [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd,int id); [DllImport("user32.dll",SetLastError=true)] private static extern IntPtr SetWindowsHookEx(int idHook,LowLevelMouseProc lpfn,IntPtr hMod,uint threadId); [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk); [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk,int nCode,IntPtr wParam,IntPtr lParam); [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? name);
}
