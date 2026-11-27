namespace RemoteControl;

internal static class MacKeys
{
    // Windows virtual key -> physical Quartz key code (ANSI hardware layout).
    // Text/IME input travels separately as Unicode.
    private static readonly ushort[] Letters = [
        0, 11, 8, 2, 14, 3, 5, 4, 34, 38, 40, 37, 46, 45, 31, 35, 12, 15, 1, 17, 32, 9, 13, 7, 16, 6
    ];
    private static readonly ushort[] Digits = [29, 18, 19, 20, 21, 23, 22, 26, 28, 25];
    private static readonly ushort[] Keypad = [82, 83, 84, 85, 86, 87, 88, 89, 91, 92];
    private static readonly ushort[] Functions = [
        122, 120, 99, 118, 96, 97, 98, 100, 101, 109, 103, 111, 105, 107, 113, 106, 64, 79, 80, 90
    ];

    internal static int FromVirtualKey(int key, bool extended) => key switch
    {
        >= 0x41 and <= 0x5a => Letters[key - 0x41],
        >= 0x30 and <= 0x39 => Digits[key - 0x30],
        >= 0x60 and <= 0x69 => Keypad[key - 0x60],
        >= 0x70 and <= 0x83 => Functions[key - 0x70],
        0x08 => 51, 0x09 => 48, 0x0c or 0x90 => 71, 0x0d => extended ? 76 : 36, 0x1b => 53,
        0x10 or 0xa0 => 56, 0xa1 => 60, 0x11 or 0xa2 => 59, 0xa3 => 62,
        0x12 or 0xa4 => 58, 0xa5 => 61, 0x14 => 57, 0x20 => 49,
        0x21 => 116, 0x22 => 121, 0x23 => 119, 0x24 => 115,
        0x25 => 123, 0x26 => 126, 0x27 => 124, 0x28 => 125, 0x2d => 114, 0x2e => 117,
        0x5b => 55, 0x5c => 54, 0x6a => 67, 0x6b => 69, 0x6d => 78, 0x6e => 65, 0x6f => 75,
        0xba => 41, 0xbb => 24, 0xbc => 43, 0xbd => 27, 0xbe => 47, 0xbf => 44,
        0xc0 => 50, 0xdb => 33, 0xdc => 42, 0xdd => 30, 0xde => 39, 0xe2 => 10,
        _ => -1
    };

    internal static ulong Modifier(ushort code) => code switch
    {
        56 or 60 => 1UL << 17, 59 or 62 => 1UL << 18, 58 or 61 => 1UL << 19,
        54 or 55 => 1UL << 20, _ => 0
    };

    internal static (int Key, int Modifier) Shortcut(string? name) => name switch
    {
        "Enter" => (36, -1), "Escape" => (53, -1), "Backspace" => (51, -1), "Delete" => (117, -1),
        "Tab" => (48, -1), "Space" => (49, -1), "ArrowUp" => (126, -1), "ArrowDown" => (125, -1),
        "ArrowLeft" => (123, -1), "ArrowRight" => (124, -1), "Home" => (115, -1), "End" => (119, -1),
        "PageUp" => (116, -1), "PageDown" => (121, -1), "F5" => (96, -1),
        "Ctrl+A" => (0, 55), "Ctrl+C" => (8, 55), "Ctrl+V" => (9, 55), "Ctrl+X" => (7, 55),
        "Ctrl+Z" => (6, 55), "Alt+Tab" => (48, 55), "Win+D" => (103, -1), // F11 (Show Desktop)
        _ => (-1, -1)
    };
}
