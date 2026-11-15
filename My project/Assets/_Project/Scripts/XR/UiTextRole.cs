using TMPro;
using UnityEngine;

namespace VRRobot.XR
{
    /// <summary>
    /// Applies the font and size of a text role from <see cref="UiTextStyles"/>. Runs after Meta's UIThemeManager
    /// (which forces its own font in Start), so the project styles win.
    /// </summary>
    [DefaultExecutionOrder(100)]
    [RequireComponent(typeof(TMP_Text))]
    public class UiTextRole : MonoBehaviour
    {
        public enum Role { Title, Heading, Body, Caption, Label }

        [SerializeField] Role _role = Role.Body;
        [SerializeField] UiTextStyles _styles;
        [SerializeField] bool _applySize = true;

        public Role CurrentRole
        {
            get => _role;
            set { _role = value; Apply(); }
        }

        void Start() => Apply();

        void OnValidate() => Apply();

        /// <summary>Sets the font (and size) of the text from the styles; only writes values that differ.</summary>
        public void Apply()
        {
            if (_styles == null) return;
            var text = GetComponent<TMP_Text>();
            if (text == null) return;
            var font = _styles.FontFor(_role);
            if (font != null && text.font != font) text.font = font;
            if (_applySize)
            {
                float size = _styles.SizeFor(_role);
                if (!Mathf.Approximately(text.fontSize, size)) text.fontSize = size;
            }
        }
    }
}
