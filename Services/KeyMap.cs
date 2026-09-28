using Avalonia.Input;

namespace FortniteAFKXPMonitor.Services;

/// <summary>Maps the neutral Avalonia <see cref="Key"/> to Windows virtual-key codes and Linux evdev codes.</summary>
public static class KeyMap
{
    private static readonly Dictionary<Key, (int Vk, int Evdev)> Map = Build();

    public static bool TryGetVk(Key key, out int vk)
    {
        bool ok = Map.TryGetValue(key, out var v);
        vk = v.Vk;
        return ok;
    }

    public static bool TryGetEvdev(Key key, out int code)
    {
        bool ok = Map.TryGetValue(key, out var v);
        code = v.Evdev;
        return ok;
    }

    public static bool TryFromVk(int vk, out Key key)
    {
        foreach (var (k, v) in Map)
        {
            if (v.Vk == vk) { key = k; return true; }
        }
        key = Key.None;
        return false;
    }

    public static bool TryFromEvdev(int code, out Key key)
    {
        foreach (var (k, v) in Map)
        {
            if (v.Evdev == code) { key = k; return true; }
        }
        key = Key.None;
        return false;
    }

    public static bool IsSupported(Key key) => Map.ContainsKey(key);

    /// <summary>All evdev codes we can emit (used to advertise capabilities on the virtual device).</summary>
    public static IEnumerable<int> AllEvdevCodes => Map.Values.Select(v => v.Evdev);

    private static Dictionary<Key, (int, int)> Build()
    {
        var m = new Dictionary<Key, (int, int)>();

        // Letters: evdev codes follow the physical QWERTY layout, not the alphabet.
        int[] letterEv = { 30, 48, 46, 32, 18, 33, 34, 35, 23, 36, 37, 38, 50, 49, 24, 25, 16, 19, 31, 20, 22, 47, 17, 45, 21, 44 };
        for (int i = 0; i < 26; i++)
            m[Key.A + i] = (0x41 + i, letterEv[i]);

        // Digits row: 1..9 = 2..10, 0 = 11.
        for (int i = 1; i <= 9; i++)
            m[Key.D0 + i] = (0x30 + i, 1 + i);
        m[Key.D0] = (0x30, 11);

        // Function keys.
        int[] fEv = { 59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 87, 88 };
        for (int i = 0; i < 12; i++)
            m[Key.F1 + i] = (0x70 + i, fEv[i]);
        for (int i = 0; i < 12; i++)
            m[Key.F13 + i] = (0x7C + i, 183 + i);

        // Numpad.
        int[] npEv = { 82, 79, 80, 81, 75, 76, 77, 71, 72, 73 };
        for (int i = 0; i < 10; i++)
            m[Key.NumPad0 + i] = (0x60 + i, npEv[i]);
        m[Key.Multiply] = (0x6A, 55);
        m[Key.Add] = (0x6B, 78);
        m[Key.Subtract] = (0x6D, 74);
        m[Key.Decimal] = (0x6E, 83);
        m[Key.Divide] = (0x6F, 98);

        m[Key.Space] = (0x20, 57);
        m[Key.Return] = (0x0D, 28);
        m[Key.Tab] = (0x09, 15);
        m[Key.Escape] = (0x1B, 1);
        m[Key.Back] = (0x08, 14);
        m[Key.CapsLock] = (0x14, 58);

        m[Key.Up] = (0x26, 103);
        m[Key.Down] = (0x28, 108);
        m[Key.Left] = (0x25, 105);
        m[Key.Right] = (0x27, 106);
        m[Key.Home] = (0x24, 102);
        m[Key.End] = (0x23, 107);
        m[Key.PageUp] = (0x21, 104);
        m[Key.PageDown] = (0x22, 109);
        m[Key.Insert] = (0x2D, 110);
        m[Key.Delete] = (0x2E, 111);

        m[Key.LeftShift] = (0xA0, 42);
        m[Key.RightShift] = (0xA1, 54);
        m[Key.LeftCtrl] = (0xA2, 29);
        m[Key.RightCtrl] = (0xA3, 97);
        m[Key.LeftAlt] = (0xA4, 56);
        m[Key.RightAlt] = (0xA5, 100);

        m[Key.OemTilde] = (0xC0, 41);
        m[Key.OemMinus] = (0xBD, 12);
        m[Key.OemPlus] = (0xBB, 13);
        m[Key.OemOpenBrackets] = (0xDB, 26);
        m[Key.OemCloseBrackets] = (0xDD, 27);
        m[Key.OemPipe] = (0xDC, 43);
        m[Key.OemSemicolon] = (0xBA, 39);
        m[Key.OemQuotes] = (0xDE, 40);
        m[Key.OemComma] = (0xBC, 51);
        m[Key.OemPeriod] = (0xBE, 52);
        m[Key.OemQuestion] = (0xBF, 53);

        return m;
    }
}
