using UnityEngine.UI;

namespace VRRobot.XR
{
    /// <summary>
    /// Sets a toggle from code so that Meta's UI Set visuals follow it. The UI Set's
    /// <c>AnimatorOverrideLayerWeight</c> only listens to <see cref="Toggle.onValueChanged"/>, so
    /// <see cref="Toggle.SetIsOnWithoutNotify"/> would leave the "on" highlight stuck. Instead the value is set
    /// with notification while <paramref name="syncing"/> is true; the owner's listeners return early on it.
    /// </summary>
    public static class UiToggles
    {
        public static void Set(Toggle toggle, bool on, ref bool syncing)
        {
            if (toggle == null || toggle.isOn == on) return;
            bool previous = syncing;
            syncing = true;
            try { toggle.isOn = on; }
            finally { syncing = previous; }
        }
    }
}
