using System;
using Oculus.Interaction.Locomotion;
using UnityEngine;

namespace VRRobot.XR
{
    /// <summary>
    /// User height adjustment: drives Meta's <see cref="FirstPersonLocomotor.HeightOffset"/>, which raises or lowers
    /// the view relative to the character's feet (the capsule grows by the same amount). Saved in PlayerPrefs.
    /// </summary>
    public class VrHeightSetting : MonoBehaviour
    {
        [SerializeField] FirstPersonLocomotor _locomotor;
        [SerializeField, Tooltip("Camera Rig root.")] Transform _playerOrigin;
        [SerializeField, Tooltip("CenterEyeAnchor.")] Transform _head;
        [SerializeField] float _min = -0.5f;
        [SerializeField, Tooltip("The capsule grows with the offset; above ~+0.25 m it no longer fits under door lintels (~2.05 m).")]
        float _max = 0.25f;
        [SerializeField] float _step = 0.05f;

        const string PrefsKey = "VRRobot.HeightOffset";

        public float Offset { get; private set; }
        public float Min => _min;
        public float Max => _max;

        /// <summary>Eye height above the floor, including the offset.</summary>
        public float EyeHeight => _head.localPosition.y + Offset;

        public event Action<float> Changed;

        void Awake()
        {
            float saved = 0f;
            try { saved = PlayerPrefs.GetFloat(PrefsKey, 0f); }
            catch (Exception e) { Debug.LogWarning("[XRLoco] Could not read the saved height offset: " + e.Message, this); }
            Apply(saved, save: false);
        }

        // This component owns FirstPersonLocomotor.HeightOffset. Meta's StandingSetting (standing/seated) also writes
        // it when its mode is applied, so its locomotor reference is cleared in VrScene; re-apply here in case
        // another component reset it during startup.
        void Start() => _locomotor.HeightOffset = Offset;

        public void Increase() => Set(Offset + _step);
        public void Decrease() => Set(Offset - _step);
        public void ResetOffset() => Set(0f);

        public void Set(float value) => Apply(value, save: true);

        void Apply(float value, bool save)
        {
            float clamped = Mathf.Round(Mathf.Clamp(value, _min, _max) / 0.01f) * 0.01f; // whole centimeters
            float delta = clamped - Offset;
            Offset = clamped;
            _locomotor.HeightOffset = clamped;
            // With the locomotor disabled (fly mode, start flow) nothing re-places the rig: shift it directly.
            if (!_locomotor.enabled && Mathf.Abs(delta) > 0f) _playerOrigin.position += Vector3.up * delta;
            if (save)
            {
                try { PlayerPrefs.SetFloat(PrefsKey, clamped); PlayerPrefs.Save(); }
                catch (Exception e) { Debug.LogWarning("[XRLoco] Could not save the height offset: " + e.Message, this); }
                Debug.Log($"[XRLoco] Height offset {clamped.ToString("+0.00;-0.00")} m", this);
            }
            Changed?.Invoke(clamped);
        }
    }
}
