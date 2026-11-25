namespace DynamicIsland.Services;

/// <summary>Texts of the Verjot pages: Chinese, and English for every other interface language.</summary>
internal static class V
{
    public static string T(string zh, string en) => Loc.Lang == "zh" ? zh : en;

    public static string F(string zh, string en, params object?[] args) =>
        string.Format(Loc.Culture, Loc.Lang == "zh" ? zh : en, args);
}
