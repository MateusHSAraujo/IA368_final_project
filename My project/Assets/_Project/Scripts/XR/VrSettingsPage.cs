using System.Globalization;
using Oculus.Interaction;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace VRRobot.XR
{
    /// <summary>
    /// Settings page of the VR menu: the fly mode toggle (kept in sync with <see cref="VrFlyMode"/>) and the
    /// height offset controls (<see cref="VrHeightSetting"/>).
    /// </summary>
    public class VrSettingsPage : MonoBehaviour
    {
        [SerializeField] Toggle _flyToggle;
        [SerializeField] VrFlyMode _flyMode;

        [Header("Height")]
        [SerializeField, Optional] VrHeightSetting _height;
        [SerializeField, Optional] Button _heightDown;
        [SerializeField, Optional] Button _heightUp;
        [SerializeField, Optional] Button _heightReset;
        [SerializeField, Optional] TMP_Text _heightLabel;

        void Awake()
        {
            _flyToggle.onValueChanged.AddListener(v => _flyMode.SetFlying(v));
            if (_height == null) return;
            if (_heightDown != null) _heightDown.onClick.AddListener(_height.Decrease);
            if (_heightUp != null) _heightUp.onClick.AddListener(_height.Increase);
            if (_heightReset != null) _heightReset.onClick.AddListener(_height.ResetOffset);
        }

        void OnEnable()
        {
            _flyToggle.SetIsOnWithoutNotify(_flyMode.IsFlying);
            _flyMode.FlyingChanged += OnFlyingChanged;
            if (_height != null)
            {
                _height.Changed += OnHeightChanged;
                OnHeightChanged(_height.Offset);
            }
        }

        void OnDisable()
        {
            _flyMode.FlyingChanged -= OnFlyingChanged;
            if (_height != null) _height.Changed -= OnHeightChanged;
        }

        void OnFlyingChanged(bool on) => _flyToggle.SetIsOnWithoutNotify(on);

        void OnHeightChanged(float offset)
        {
            if (_heightLabel != null)
                _heightLabel.text = $"{offset.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture)} m  " +
                                    $"(eyes {_height.EyeHeight.ToString("0.00", CultureInfo.InvariantCulture)} m)";
            if (_heightDown != null) _heightDown.interactable = offset > _height.Min + 1e-4f;
            if (_heightUp != null) _heightUp.interactable = offset < _height.Max - 1e-4f;
        }
    }
}
