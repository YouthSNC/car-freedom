using Colossal.IO.AssetDatabase;
using Game;
using Game.Modding;
using Game.SceneFlow;
using UnityEngine;
using UnityEngine.Scripting;

namespace HouseholdCars
{
    /// <summary>
    /// HouseholdCars - makes sure every resident household owns at least one
    /// personal car, spawned right at their home so it is never parked far away.
    /// </summary>
    public class Mod : IMod
    {
        public const string kModName = "HouseholdCars";

        // Every locale id present in Cities2_Data\Content\Game\Locale.cok.
        // AddSource is a no-op for an id the game did not load, so listing all
        // of them is safe even if a future patch drops one.
        private static readonly string[] kSupportedLocales =
        {
            "en-US",
            "zh-HANS",
            "zh-HANT",
            "de-DE",
            "es-ES",
            "fr-FR",
            "it-IT",
            "ja-JP",
            "ko-KR",
            "pl-PL",
            "pt-BR",
            "ru-RU",
        };

        // Settings live for the whole game session and are read by
        // HouseholdCarGrantSystem on every scan pass, so a slider change takes
        // effect on the next pass without restarting anything.
        private static CarFreedomSetting s_Settings;

        public static CarFreedomSetting Settings
        {
            get { return s_Settings; }
        }

        [Preserve]
        public Mod()
        {
        }

        public void OnLoad(UpdateSystem updateSystem)
        {
            Debug.Log("[" + kModName + "] loaded - granting household cars near home");

            // Same sequence as the official Colossal Order mod template:
            //   1. construct the settings object
            //   2. register it in the Options UI (this is what makes a page
            //      appear under Options -> Mods)
            //   3. add the locale strings
            //   4. load the persisted values from disk (falls back to the
            //      defaults passed as the third argument on first run)
            //
            // Wrapped in try/catch on purpose: if any step of the settings
            // plumbing fails, the mod must still fall back to the default
            // threshold and keep granting cars. An exception escaping OnLoad
            // would skip the UpdateAt below and disable the mod entirely.
            try
            {
                s_Settings = new CarFreedomSetting(this);
                s_Settings.RegisterInOptionsUI();

                // The settings page is English-only on purpose: the mod is
                // published internationally and English is the one language
                // every player can be assumed to read.
                //
                // The English dictionary is registered for EVERY locale the
                // game ships, not just en-US. Reason: the mod description on
                // PDX is the only translated thing users see; the options page
                // itself stays English no matter which language the player
                // runs the game in. Registering only en-US would also *usually*
                // work - LocalizationManager falls back to en-US for missing
                // keys (GameManager constructs it with fallbackLocaleId
                // "en-US") - but that path depends on source ordering and on
                // the fallback merge happening after our source is added, so
                // it is spelled out explicitly instead.
                //
                // Ids are taken verbatim from the game's own Locale.cok. The
                // match is exact and case sensitive: "zh-Hans" would silently
                // never be applied (no error, the page just stays English -
                // which, here, is the intended outcome anyway).
                var locale = GameManager.instance.localizationManager;
                var english = new LocaleEN(s_Settings);
                for (int i = 0; i < kSupportedLocales.Length; i++)
                {
                    locale.AddSource(kSupportedLocales[i], english);
                }

                AssetDatabase.global.LoadSettings(kModName, s_Settings, new CarFreedomSetting(this));

                Debug.Log("[" + kModName + "] settings ready - minimum household members: " + s_Settings.MinHouseholdMembers);
            }
            catch (System.Exception e)
            {
                // Settings are a convenience, not a prerequisite. Report and move on.
                s_Settings = null;
                Debug.LogError("[" + kModName + "] settings page could not be registered, falling back to defaults: " + e);
            }

            updateSystem.UpdateAt<HouseholdCarGrantSystem>(SystemUpdatePhase.GameSimulation);
        }

        public void OnDispose()
        {
            if (s_Settings != null)
            {
                try
                {
                    s_Settings.UnregisterInOptionsUI();
                }
                catch (System.Exception e)
                {
                    Debug.LogError("[" + kModName + "] failed to unregister settings UI: " + e);
                }
                s_Settings = null;
            }
            Debug.Log("[" + kModName + "] disposed");
        }
    }
}
