using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace VRRobot.XR
{
    /// <summary>A small colored pill with a text, used to show a status (neutral / ok / warning / error).</summary>
    public class StatusChip : MonoBehaviour
    {
        public enum Level { Neutral, Ok, Warn, Error }

        [SerializeField] Image _background;
        [SerializeField] TMP_Text _label;
        [SerializeField] Color _neutral = new Color32(0x5C, 0x63, 0x70, 0xFF);
        [SerializeField] Color _ok = new Color32(0x2E, 0xB8, 0x72, 0xFF);
        [SerializeField] Color _warn = new Color32(0xE8, 0xA3, 0x3D, 0xFF);
        [SerializeField] Color _error = new Color32(0xE5, 0x48, 0x4D, 0xFF);
        [SerializeField] Color _textColor = Color.white;

        bool _hasLevel;
        Level _level;

        /// <summary>Sets the text and level; unchanged values are not reassigned (avoids UI rebuilds).</summary>
        public void Set(string text, Level level)
        {
            if (_label != null)
            {
                text = text ?? "";
                if (_label.text != text) _label.text = text;
                if (_label.color != _textColor) _label.color = _textColor;
            }
            if (_background != null && (!_hasLevel || _level != level))
            {
                var c = ColorFor(level);
                if (_background.color != c) _background.color = c;
            }
            _level = level;
            _hasLevel = true;
        }

        Color ColorFor(Level level)
        {
            switch (level)
            {
                case Level.Ok: return _ok;
                case Level.Warn: return _warn;
                case Level.Error: return _error;
                default: return _neutral;
            }
        }
    }
}
