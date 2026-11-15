using TMPro;
using UnityEngine;

namespace VRRobot.XR
{
    /// <summary>Font assets and sizes of the menu text roles (see <see cref="UiTextRole"/>).</summary>
    [CreateAssetMenu(menuName = "VRRobot/UI Text Styles")]
    public class UiTextStyles : ScriptableObject
    {
        public TMP_FontAsset bold;
        public TMP_FontAsset medium;
        public TMP_FontAsset regular;

        public float title = 26f;
        public float heading = 18f;
        public float body = 18f;
        public float caption = 15f;

        public TMP_FontAsset FontFor(UiTextRole.Role r)
        {
            switch (r)
            {
                case UiTextRole.Role.Title:
                case UiTextRole.Role.Heading: return bold;
                case UiTextRole.Role.Body:
                case UiTextRole.Role.Caption: return regular;
                default: return medium;
            }
        }

        public float SizeFor(UiTextRole.Role r)
        {
            switch (r)
            {
                case UiTextRole.Role.Title: return title;
                case UiTextRole.Role.Heading: return heading;
                case UiTextRole.Role.Caption: return caption;
                default: return body;
            }
        }
    }
}
