using System.Reflection;
using Motorways.Audio;
using UnityEngine.UI;

namespace RouteViz
{
    /// <summary>
    /// Our buttons are clones of the game's back button; give them the game's ordinary button sound
    /// (UIAudioProfile.Generic: a hover note, and "sineFX_35" + "ui_lineOpens" on click) instead of the back sound.
    /// </summary>
    public partial class RouteVizPlugin
    {
        /// <summary>
        /// Sets the clone's audio profile to Generic, the default of every game button (Pause/Play/FastForward have
        /// their own profiles, which also change the game speed's music). TouchButton then schedules the UI Click event
        /// itself in OnPointerUp/OnSubmit (right after our click listener runs), and MouseOver on hover.
        /// The clone's audio system reference isn't serialized, so copy it from the source button (or the global one).
        /// </summary>
        private void ApplyGameButtonSound(TouchButton button, TouchButton source = null)
        {
            FieldInfo profile = FindField(typeof(TouchButton), "_audioProfile");
            if (profile != null)
                profile.SetValue(button, UIAudioProfile.Generic);

            FieldInfo system = FindField(typeof(TouchButton), "_audioSystem");
            if (system != null && system.GetValue(button) == null)
            {
                object value = source != null ? system.GetValue(source) : null;
                system.SetValue(button, value ?? AudioSystem.Instance);
            }
        }
    }
}
