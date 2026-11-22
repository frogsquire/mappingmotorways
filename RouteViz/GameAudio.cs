using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Motorways.Audio;

namespace RouteViz
{
    /// <summary>
    /// Click sounds through the game's own audio system (its UI player), so they follow the game's volume and mute.
    /// Falls back to the synthesised Sounds when the game's samples aren't available.
    /// </summary>
    public partial class RouteVizPlugin
    {
        // Preferred game samples, first one that exists wins. Ding and dong share a sample at different pitches.
        private static readonly string[] DingCandidates = { "ui_checked", "PinAppears-01", "sineFX_35", "ui_lineOpens" };
        private static readonly string[] ErrorCandidates = { "ui_stationWarning", "DangerTick", "Erase" };
        private const float DingPitch = 1.25f;
        private const float DongPitch = 0.8f;

        private bool _gameAudioProbed;
        private string _gameDing;
        private string _gameError;

        private void PlayClick(Sounds.Kind kind)
        {
            float volume = UnityEngine.Mathf.Clamp01(_soundVolume.Value);
            if (volume <= 0f)
                return;

            if (_useGameSounds.Value && TryPlayGameSound(kind, volume))
                return;
            _sounds?.Play(kind);
        }

        private bool TryPlayGameSound(Sounds.Kind kind, float volume)
        {
            try
            {
                if (!_gameAudioProbed)
                    ProbeGameAudio();
                AudioPlayer ui = AudioPlayer.UI;
                if (ui == null)
                    return false;

                string sample = kind == Sounds.Kind.Error ? _gameError : _gameDing;
                if (sample == null)
                    return false;
                float pitch = kind == Sounds.Kind.Ding ? DingPitch : kind == Sounds.Kind.Dong ? DongPitch : 1f;
                // isImportant: the game drops non-important samples while it's in its "Minimal" state; its own UI sounds pass true.
                // The warning sample is loud by design, so the "brr" plays well under the on/off sounds.
                float gain = volume * (kind == Sounds.Kind.Error ? 0.35f : 0.9f);
                return ui.PlaySample(sample, 0.5f, gain, pitch, isImportant: true) != null; // ~0.45 at the default volume: under the game's own UI cues
            }
            catch (Exception e)
            {
                if (_verboseLogging.Value)
                    Logger.LogWarning($"Game audio failed ({e.Message}); using built-in sounds.");
                _gameDing = _gameError = null;
                return false;
            }
        }

        /// <summary>Finds which preferred samples the game has loaded (and logs the full list once, when verbose).</summary>
        private void ProbeGameAudio()
        {
            _gameAudioProbed = true;
            HashSet<string> names = GameSampleNames();
            if (names.Count == 0)
            {
                Logger.LogInfo("Game audio samples not found; using built-in click sounds.");
                return;
            }

            _gameDing = Pick(_dingSampleName.Value, DingCandidates, names);
            _gameError = Pick(_errorSampleName.Value, ErrorCandidates, names);
            Logger.LogInfo($"Click sounds from the game: on/off '{_gameDing ?? "(built-in)"}', error '{_gameError ?? "(built-in)"}'.");
            if (_verboseLogging.Value)
                Logger.LogInfo($"Game samples ({names.Count}): {string.Join(", ", names.OrderBy(n => n))}");
        }

        private static string Pick(string configured, string[] candidates, HashSet<string> names)
        {
            if (!string.IsNullOrEmpty(configured) && names.Contains(configured.Trim()))
                return configured.Trim();
            return candidates.FirstOrDefault(names.Contains);
        }

        /// <summary>Names in the game's loaded sound banks (AudioDatabase.activeAudioDataBanks[*].sampleIndex keys).</summary>
        private static HashSet<string> GameSampleNames()
        {
            var names = new HashSet<string>();
            IAudioSystem system = AudioSystem.Instance;
            AudioDatabase db = system?.Database;
            if (db == null)
                return names;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            if (!(typeof(AudioDatabase).GetField("activeAudioDataBanks", flags)?.GetValue(db) is IEnumerable banks))
                return names;
            FieldInfo indexField = typeof(AudioDataBank).GetField("sampleIndex", flags);
            foreach (object bank in banks)
                if (indexField?.GetValue(bank) is Dictionary<string, int> index)
                    foreach (string key in index.Keys)
                        names.Add(key);
            return names;
        }
    }
}
