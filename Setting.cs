using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using System.Collections.Generic;

namespace HouseholdCars
{
    /// <summary>
    /// Settings page for Car Freedom. Appears in the game's Options menu
    /// (Mods tab) as a normal settings page, exactly like the official
    /// Colossal Order mod template does it.
    ///
    /// The whole structure is:
    ///   [FileLocation]        -> the .coc file the values are persisted to
    ///   [SettingsUIGroupOrder] / [SettingsUIShowGroupName] -> group layout
    ///   [SettingsUISection]   -> which tab + group a property belongs to
    ///   [SettingsUISlider]    -> renders the property as a slider
    /// </summary>
    [FileLocation(Mod.kModName)]
    [SettingsUIGroupOrder(kGrantGroup)]
    [SettingsUIShowGroupName(kGrantGroup)]
    public class CarFreedomSetting : ModSetting
    {
        // "tab" in the options UI. The template calls this a section; the
        // localization key is built with GetOptionTabLocaleID.
        public const string kSection = "General";

        // "group" inside the tab.
        public const string kGrantGroup = "Grant";

        public const int kMinMembersLower = 1;
        public const int kMinMembersUpper = 20;

        // 3, not 1: one- and two-person households make up a large share of a
        // mature city and each one gets a full car, so defaulting to 1 spawns
        // a lot of vehicles on the first load of a big save. 3 still covers the
        // typical family while keeping that first wave noticeably smaller.
        // Players who want the pre-1.2 behaviour can drag it to 1.
        public const int kMinMembersDefault = 3;

        public CarFreedomSetting(IMod mod) : base(mod)
        {
        }

        /// <summary>
        /// Households with FEWER members than this are skipped entirely - they
        /// never reach the "does it need a car?" stage. The default is 3; set
        /// it to 1 for the behaviour of every version before 1.2, which is
        /// "no filtering".
        /// </summary>
        [SettingsUISlider(min = kMinMembersLower, max = kMinMembersUpper, step = 1, unit = "integer")]
        [SettingsUISection(kSection, kGrantGroup)]
        public int MinHouseholdMembers { get; set; } = kMinMembersDefault;

        /// <summary>
        /// Called by the settings framework when defaults are needed. Must be
        /// implemented - ModSetting declares it abstract. The property
        /// initializer above covers the plain-construction path; this covers
        /// the "reset to defaults" button.
        /// </summary>
        public override void SetDefaults()
        {
            MinHouseholdMembers = kMinMembersDefault;
        }
    }

    /// <summary>
    /// English strings for the settings page - the ONLY language this mod
    /// ships (see Mod.OnLoad for why the same source is registered for every
    /// locale the game supports).
    ///
    /// To bring back a translated page later, add a class like this one that
    /// returns the translated values, and register it for the matching locale
    /// id inside Mod.OnLoad - nothing else has to change.
    /// </summary>
    public class LocaleEN : IDictionarySource
    {
        private readonly CarFreedomSetting m_Setting;

        public LocaleEN(CarFreedomSetting setting)
        {
            m_Setting = setting;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "Car Freedom" },

                { m_Setting.GetOptionTabLocaleID(CarFreedomSetting.kSection), "General" },
                { m_Setting.GetOptionGroupLocaleID(CarFreedomSetting.kGrantGroup), "Car grants" },

                { m_Setting.GetOptionLabelLocaleID(nameof(CarFreedomSetting.MinHouseholdMembers)), "Minimum household members" },
                { m_Setting.GetOptionDescLocaleID(nameof(CarFreedomSetting.MinHouseholdMembers)),
                    "Only households with at least this many members are given a car. The default is 3 - set it to 1 to give a car to every household. "
                    + "Households below this threshold are left completely untouched - they are not counted as carless and nothing is spawned for them." },
            };
        }

        public void Unload()
        {
        }
    }
}
