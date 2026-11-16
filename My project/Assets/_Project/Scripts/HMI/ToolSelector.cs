using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using VRRobot.XR;

namespace VRRobot.HMI
{
    /// <summary>
    /// Tool picker shared by the Mission page of the VR menu and the operations panel toolbar: tool toggles
    /// (None, Goal, Path, No-go), the no-go brush row and "Clear no-go zones" (two-step confirm). Every instance mirrors
    /// the <see cref="TacticalToolController"/> state through its events, so all pickers stay in sync.
    /// </summary>
    public class ToolSelector : MonoBehaviour
    {
        const float ConfirmSeconds = 3f;

        [SerializeField, Tooltip("Falls back to FindFirstObjectByType.")] TacticalToolController _tools;

        [Header("Tools (None, Goal, Path, No-go)")]
        [SerializeField] Toggle[] _toolButtons = new Toggle[0];

        [Header("Brush (S, M, L)")]
        [SerializeField, Tooltip("Visible only with the No-go tool.")] GameObject _brushRow;
        [SerializeField] Toggle[] _brushButtons = new Toggle[0];
        [SerializeField] TMP_Text[] _brushLabels = new TMP_Text[0];

        [Header("Clear no-go zones (optional)")]
        [SerializeField, Tooltip("Row holding the Clear button; visible only with the No-go tool, like the brush row.")]
        GameObject _clearRow;
        [SerializeField] Button _clearButton;
        [SerializeField] TMP_Text _clearLabel;
        [SerializeField] string _clearText = "Clear no-go zones";
        [SerializeField] string _confirmText = "Confirm clear?";

        RosHmiClient _ros;
        bool _syncing;      // true while code sets toggles (see UiToggles)
        bool _resyncTools;  // a toggle changed by the user; re-apply the tool/brush state after the event
        float _clearDeadline = -1f;

        void Awake()
        {
            if (_tools == null) _tools = FindFirstObjectByType<TacticalToolController>(FindObjectsInactive.Include);
            for (int i = 0; i < _toolButtons.Length; i++)
            {
                var tool = (TacticalTool)i;
                if (_toolButtons[i] != null) _toolButtons[i].onValueChanged.AddListener(on =>
                {
                    if (_syncing) return;
                    if (on && _tools != null) _tools.SetTool(tool);
                    _resyncTools = true; // turning the active tool off is undone in LateUpdate
                });
            }
            for (int i = 0; i < _brushButtons.Length; i++)
            {
                int index = i;
                if (_brushButtons[i] != null) _brushButtons[i].onValueChanged.AddListener(on =>
                {
                    if (_syncing) return;
                    if (on && _tools != null) _tools.SetBrush(index);
                    _resyncTools = true;
                });
            }
            if (_clearButton != null) _clearButton.onClick.AddListener(OnClear);
        }

        void OnEnable()
        {
            _ros = RosHmiClient.Instance;
            if (_tools != null)
            {
                _tools.ToolChanged += OnToolChanged;
                _tools.BrushChanged += RefreshTools;
            }
            if (_ros != null)
            {
                _ros.ConnectionChanged += OnConnectionChanged;
                _ros.MapStateChanged += OnMapStateChanged;
            }
            _clearDeadline = -1f;
            RefreshTools();
            RefreshClear();
        }

        void OnDisable()
        {
            if (_tools != null)
            {
                _tools.ToolChanged -= OnToolChanged;
                _tools.BrushChanged -= RefreshTools;
            }
            if (_ros != null)
            {
                _ros.ConnectionChanged -= OnConnectionChanged;
                _ros.MapStateChanged -= OnMapStateChanged;
            }
            _ros = null;
        }

        void Update()
        {
            if (_ros == null && RosHmiClient.Instance != null && isActiveAndEnabled)
            {
                // RosHmiClient may start after this panel was enabled.
                OnDisable();
                OnEnable();
            }
            if (_clearDeadline >= 0f && Time.unscaledTime > _clearDeadline)
            {
                _clearDeadline = -1f;
                RefreshClear();
            }
        }

        // Re-applied outside the toggle callbacks: setting a toggle inside its own onValueChanged would reach the
        // UI Set's highlight listener in the wrong order.
        void LateUpdate()
        {
            if (!_resyncTools) return;
            _resyncTools = false;
            RefreshTools();
        }

        void OnToolChanged(TacticalTool tool) => RefreshTools();
        void OnConnectionChanged(bool connected) => RefreshClear();
        void OnMapStateChanged(MapState state) => RefreshClear();

        void OnClear()
        {
            // First click arms the confirmation; a second click before the deadline confirms.
            if (_clearDeadline < 0f || Time.unscaledTime > _clearDeadline)
            {
                _clearDeadline = Time.unscaledTime + ConfirmSeconds;
                RefreshClear();
                return;
            }
            _clearDeadline = -1f;
            if (_ros != null) _ros.ClearKeepout();
            if (_tools != null) _tools.ClearPending();
            RefreshClear();
        }

        void RefreshTools()
        {
            var current = _tools != null ? _tools.Tool : TacticalTool.None;
            for (int i = 0; i < _toolButtons.Length; i++)
            {
                if (_toolButtons[i] == null) continue;
                _toolButtons[i].interactable = _tools != null;
                UiToggles.Set(_toolButtons[i], i == (int)current, ref _syncing);
            }

            bool noGo = _tools != null && current == TacticalTool.NoGo;
            if (_brushRow != null) _brushRow.SetActive(noGo);
            if (_clearRow != null) _clearRow.SetActive(noGo);
            if (_tools == null) return;
            for (int i = 0; i < _brushButtons.Length; i++)
            {
                if (_brushButtons[i] == null) continue;
                bool exists = i < _tools.BrushCount;
                _brushButtons[i].gameObject.SetActive(exists);
                UiToggles.Set(_brushButtons[i], i == _tools.BrushIndex, ref _syncing);
                if (exists && i < _brushLabels.Length && _brushLabels[i] != null)
                {
                    float r = _tools.BrushRadiusAt(i);
                    _brushLabels[i].text = r.ToString("0.00", CultureInfo.InvariantCulture) + " m";
                }
            }
        }

        void RefreshClear()
        {
            var s = _ros != null ? _ros.MapState : null;
            bool usable = _ros != null && _ros.IsConnected && (s == null || !s.IsBusy);
            if (_clearButton != null) _clearButton.interactable = usable;
            if (_clearLabel != null) _clearLabel.text = _clearDeadline >= 0f ? _confirmText : _clearText;
        }
    }
}
