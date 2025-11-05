// PhotonKeys.cs
// Centralized keys used by multiple scripts
public static class PhotonKeys
{
    public const string PROP_CHARACTER_INDEX = "characterIndex";
    public const string PROP_CHARACTER_PREFAB = "characterPrefab";
    public const string PROP_TRIAD = "characterTriad"; // stored as object[] of ints in Photon custom props

    // PlayerPrefs fallback keys
    public const string PREF_KEY_TRIAD = "characterTriad"; // CSV "a,b,c"
    public const string PREF_CHARACTER_INDEX = "characterIndex";
    public const string PREF_CHARACTER_PREFAB = "characterPrefab";
}
