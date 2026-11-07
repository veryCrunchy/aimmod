namespace AimMod.InGame.Multiplayer;

static class MatchControlKeys
{
    public static bool ReturnHeld(GameBinds.Bound binds, Func<int, bool> down) =>
        new[] { "MoveForward+", "MoveForward-", "MoveRight+", "MoveRight-", "Fire", "Jump" }
            .SelectMany(binds.Of).Any(key => Held(key, down));

    static bool Held(string binding, Func<int, bool> down)
    {
        var parts = binding.Split('+');
        foreach (var modifier in parts.SkipLast(1))
            if (!down(modifier switch { "Ctrl" => 0x11, "Alt" => 0x12, "Shift" => 0x10, "Cmd" => 0x5B, _ => 0 })) return false;
        var key = parts[^1];
        var code = key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]) ? char.ToUpperInvariant(key[0])
            : key.StartsWith('F') && int.TryParse(key[1..], out var f) && f is >= 1 and <= 24 ? 0x6F + f
            : key switch
            {
                "LeftMouseButton" => 0x01, "RightMouseButton" => 0x02, "MiddleMouseButton" => 0x04,
                "ThumbMouseButton" => 0x05, "ThumbMouseButton2" => 0x06, "SpaceBar" => 0x20,
                "Left" => 0x25, "Up" => 0x26, "Right" => 0x27, "Down" => 0x28,
                "LeftShift" => 0xA0, "RightShift" => 0xA1, "LeftControl" => 0xA2, "RightControl" => 0xA3,
                "LeftAlt" => 0xA4, "RightAlt" => 0xA5, "Tab" => 0x09, "Enter" => 0x0D, "CapsLock" => 0x14,
                "Zero" => 0x30, "One" => 0x31, "Two" => 0x32, "Three" => 0x33, "Four" => 0x34,
                "Five" => 0x35, "Six" => 0x36, "Seven" => 0x37, "Eight" => 0x38, "Nine" => 0x39,
                "Home" => 0x24, "End" => 0x23, "PageUp" => 0x21, "PageDown" => 0x22, "Insert" => 0x2D, "Delete" => 0x2E,
                _ => 0,
            };
        return code != 0 && down(code);
    }
}
