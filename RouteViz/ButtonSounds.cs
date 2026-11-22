using System.Reflection;
using Motorways.Audio;
using UnityEngine.UI;

namespace RouteViz
{
    /// <summary>
    /// Our buttons are clones of the game's back button. Instantiate only copies serialized fields, so runtime
    /// state from VariableDeviceSelectable.Initialize (menuNavigation, audio system, …) is missing and
    /// TouchButton.OnPointerExit NREs on menuNavigation.ClearFocus. Copy what we need from the source.
    /// </summary>
    public partial class RouteVizPlugin
    {
        /// <summary>
        /// Sets the clone's audio profile to Generic, the default of every game button (Pause/Play/FastForward have
        /// their own profiles, which also change the game speed's music). TouchButton then schedules the UI Click event
        /// itself in OnPointerUp/OnSubmit (right after our click listener runs), and MouseOver on hover.
        /// Also copies menuNavigation / feedback / device type from the source so hover/exit don't NRE.
        /// </summary>
        private void ApplyGameButtonSound(TouchButton button, TouchButton source = null)
        {
            FieldInfo profile = FindField(typeof(TouchButton), "_audioProfile");
            if (profile != null)
                profile.SetValue(button, UIAudioProfile.Generic);

            CopyField(button, source, "_audioSystem", AudioSystem.Instance);
            CopyField(button, source, "menuNavigation");
            CopyField(button, source, "_feedbackGenerator");

            // DeviceInputType is a private-set property on VariableDeviceSelectable; without it, exit still
            // reaches ClearFocus but with a default type — copy the live value from the back button.
            if (source != null)
            {
                PropertyInfo device = null;
                for (var t = button.GetType(); t != null && device == null; t = t.BaseType)
                    device = t.GetProperty("DeviceInputType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (device != null && device.CanWrite)
                    device.SetValue(button, device.GetValue(source));
                else if (device != null)
                {
                    FieldInfo backing = FindField(button.GetType(), "<DeviceInputType>k__BackingField");
                    if (backing != null)
                        backing.SetValue(button, device.GetValue(source));
                }
            }
        }

        private static void CopyField(TouchButton button, TouchButton source, string name, object fallback = null)
        {
            FieldInfo field = FindField(button.GetType(), name);
            if (field == null)
                return;
            object value = source != null ? field.GetValue(source) : null;
            field.SetValue(button, value ?? fallback);
        }
    }
}
