using UndertaleModLib;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Addressing;

// ---------------------------------------------------------------------------
// WHICH game and chapter a loaded data.win is, read from the data itself:
//
//   Name "UNDERTALE"  DisplayName "UNDERTALE"            -> undertale, no chapter
//   Name "DELTARUNE"  DisplayName "DELTARUNE"            -> deltarune, no chapter (launcher)
//   Name "DELTARUNE"  DisplayName "DELTARUNE Chapter 2"  -> deltarune, chapter2
//
// A mod file belongs to the target when its address names the same game and
// the same chapter. The chapter is the route minus its last segment (the
// container: data.win or an audiogroup name):
//
//   deltarune/chapter2/data.win/sprites/spr_x/0.png   chapter = chapter2
//   deltarune/data.win/sprites/spr_x/0.png            chapter = (none)
//   undertale/data.win/sprites/spr_x/0.png            chapter = (none)
// ---------------------------------------------------------------------------

public sealed record ModTarget(string Game, string? Chapter)
{
    /// <summary>
    /// Identify the target from loaded data. Returns null when the data is not
    /// one of the games the address grammar knows, so the caller can warn.
    /// </summary>
    public static ModTarget? FromData(UndertaleData data)
    {
        string name = data.GeneralInfo?.Name?.Content ?? "";
        string game = name.ToLowerInvariant();
        if (game.Length == 0)
            return null;

        // Whatever DisplayName adds after the game name is the chapter:
        // "DELTARUNE Chapter 2" -> "Chapter 2" -> "chapter2".
        string display = data.GeneralInfo?.DisplayName?.Content ?? "";
        string chapter = display.StartsWith(name, StringComparison.OrdinalIgnoreCase)
            ? display[name.Length..].Replace(" ", "").ToLowerInvariant()
            : "";

        return new ModTarget(game, IsChapterName(chapter) ? chapter : null);
    }

    /// <summary>True when the address is for this game and this chapter.</summary>
    public bool Accepts(ModAddress addr)
    {
        if (!addr.Game.Equals(Game, StringComparison.OrdinalIgnoreCase))
            return false;
        if (addr.IsShared)
            return true;
        if (addr.InSharedFolder)
            return false;

        IReadOnlyList<string> chapter = addr.RoutePrefix;
        return Chapter is null
            ? chapter.Count == 0
            : chapter.Count == 1 && chapter[0].Equals(Chapter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the address is for this game but its chapter part could never
    /// match any target: anything other than nothing or "chapter" + a number.
    /// Catches a misspelled folder ("chaptr2") that would otherwise be skipped
    /// as if it were just another chapter.
    /// </summary>
    public bool LooksMisspelled(ModAddress addr)
    {
        if (!addr.Game.Equals(Game, StringComparison.OrdinalIgnoreCase))
            return false;

        IReadOnlyList<string> chapter = addr.RoutePrefix;
        if (chapter.Count == 0) return false;
        if (chapter.Count > 1) return true;

        return !IsChapterName(chapter[0]);
    }

    private static bool IsChapterName(string segment)
    {
        const string word = "chapter";
        return segment.StartsWith(word, StringComparison.OrdinalIgnoreCase)
               && segment.Length > word.Length
               && segment[word.Length..].All(char.IsAsciiDigit);
    }

    public override string ToString() => Chapter is null ? Game : $"{Game} {Chapter}";
}
