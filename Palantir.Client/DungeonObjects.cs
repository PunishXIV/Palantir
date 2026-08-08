namespace Palantir;

internal static class DungeonObjects
{
    // trap vfx/glyphs, which only exist while a Pomander of Sight is up. first four spawn everywhere;
    // the others are that particular deep dungeon's transformation trap
    public static readonly Dictionary<uint, string> TrapVfx = new()
    {
        { 2007182, "Landmine" },
        { 2007183, "Luring Trap" },
        { 2007184, "Enfeebling Trap" },
        { 2007185, "Impeding Trap" },
        { 2007186, "Toading Trap" }, // PotD
        { 2009504, "Odder Trap" },   // HoH
        { 2013284, "Owlet Trap" },   // EO
        { 2014939, "Fae Trap" },     // PT
    };

    // accursed hoard
    public static readonly HashSet<uint> HoardObjects =
    [
        2007542, // intuition marker
        2007543, // banded coffer
    ];

    // passage
    public static readonly HashSet<uint> Passage =
    [
        2007188, // PotD
        2009507, // HoH
        2013287, // EO
        2014756, // PT
    ];

    // return
    public static readonly HashSet<uint> Return =
    [
        2007187, // PotD
        2009506, // HoH
        2013286, // EO
        2014755, // PT
    ];

    // PT only, applies some sort of buff on the next floor if activated
    public const uint Candelabra = 2014759;

    public const uint SilverCoffer = 2007357;
    public const uint GoldCoffer = 2007358;
    
    public static readonly HashSet<uint> BronzeCoffers =
    [
        // PotD
        782, 783, 784, 785, 786, 787, 788, 789, 790,
        802, 803, 804, 805,
        // HoH
        1036, 1037, 1038, 1039, 1040, 1041, 1042, 1043, 1044,
        1045, 1046, 1047, 1048, 1049,
        // EO
        // not yet scraped these IDs
        // PT
        // not yet scraped these IDs either lol
    ];
    
    // the bronze coffer that is really a mimic, PotD <= floor 49 only
    public const uint MimicCoffer = 2006020;
}
