namespace WinWingOverlay;

/// <summary>
/// A parsed global hotkey, e.g. "Ctrl+Alt+M". Key names are <see cref="Keys"/> names, so
/// letters are A-Z, number-row digits are D1-D9, and function keys are F1-F12.
/// </summary>
internal readonly record struct Hotkey(uint Modifiers, Keys Key)
{
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        uint mods = 0;
        Keys key = Keys.None;

        foreach (string part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL": mods |= Native.MOD_CONTROL; break;
                case "ALT": mods |= Native.MOD_ALT; break;
                case "SHIFT": mods |= Native.MOD_SHIFT; break;
                case "WIN":
                case "WINDOWS": mods |= Native.MOD_WIN; break;
                default:
                    if (key != Keys.None) return false;
                    if (!Enum.TryParse(part, ignoreCase: true, out Keys parsed)) return false;
                    key = parsed;
                    break;
            }
        }

        // A global hotkey without a modifier would swallow the key from every other program.
        if (key == Keys.None || mods == 0) return false;

        hotkey = new Hotkey(mods, key);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>(4);
        if ((Modifiers & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((Modifiers & Native.MOD_ALT) != 0) parts.Add("Alt");
        if ((Modifiers & Native.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((Modifiers & Native.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }
}
