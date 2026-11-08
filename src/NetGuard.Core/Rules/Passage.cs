using System.Security.Cryptography;
using System.Text;

namespace NetGuard.Core.Rules;

/// <summary>Random everyday words to retype when confirming an unlock. A new passage for every request.</summary>
public static class Passage
{
    private static readonly string[] Words = (
        "apple river stone window garden pencil cloud mirror basket candle forest bridge ladder rabbit orange silver " +
        "button coffee island marble pocket rocket saddle tunnel violin wallet yellow anchor blanket cactus dolphin engine " +
        "feather giraffe harbor jacket kettle lemon meadow needle oyster parrot quilt ribbon spider teapot umbrella velvet " +
        "walnut zipper bottle carpet desert pepper ginger hammer igloo jungle kitten lantern magnet napkin pillow puzzle " +
        "rainbow shovel tomato valley wagon breeze castle dinner falcon glove helmet insect jigsaw lizard mitten nugget " +
        "olive pebble raisin sandal ticket walrus banana cherry donkey eagle fabric goblet hollow kernel ladle muffin " +
        "noodle otter pickle rattle sunset turtle vessel whistle biscuit cotton dragon finger garlic honey journal lobster " +
        "mango nickel ocean planet radish salmon thimble tulip velcro waffle beetle canyon dimple ember flannel grape " +
        "harvest ivory jelly lagoon maple nectar orchid peanut quarry robin spoon tractor voyage willow acorn bucket " +
        "cobweb daisy easel funnel gravel hinge kayak locket mustard nutmeg paddle quiver ripple satchel trumpet " +
        "copper meadow summer winter autumn spring morning evening silent gentle golden purple simple steady quiet " +
        "careful bright humble patient honest clever brave calm tidy cozy"
    ).Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();

    public static string Generate(int length)
    {
        var sb = new StringBuilder();
        while (sb.Length < length)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(Words[RandomNumberGenerator.GetInt32(Words.Length)]);
        }
        return sb.ToString();
    }

    /// <summary>Exact match, except that extra spaces and line breaks don't matter.</summary>
    public static bool Matches(string expected, string? typed) =>
        Normalize(expected) == Normalize(typed ?? "");

    private static string Normalize(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
