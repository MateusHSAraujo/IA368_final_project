using System;
using Oculus.Interaction;
using Oculus.Interaction.Locomotion;
using UnityEngine;
using LocomotionCharacter = Oculus.Interaction.Locomotion.CharacterController;

namespace VRRobot.XR
{
    /// <summary>
    /// "Creative" fly mode: disables Meta's locomotor and moves the rig root directly (no collisions).
    /// Left stick = move, triggers = up/down (right up, left down), right stick = snap turn.
    /// </summary>
    public class VrFlyMode : MonoBehaviour
    {
        [SerializeField] FirstPersonLocomotor _locomotor;
        [SerializeField] LocomotionCharacter _character;
        [SerializeField, Tooltip("Camera Rig root.")] Transform _playerOrigin;
        [SerializeField, Tooltip("CenterEyeAnchor.")] Transform _head;
        [SerializeField] LocomotionInputGate _gate;
        [SerializeField] VrPlayerBootstrap _bootstrap;
        [SerializeField, Optional] GameObject _hudLabel;
        [SerializeField] float _speed = 2f;
        [SerializeField] float _verticalSpeed = 1.5f;
        [SerializeField] float _deadZone = 0.15f;
        [SerializeField] float _snapAngle = 45f;

        const float FireThreshold = 0.75f, ArmThreshold = 0.3f;
        bool _armed = true;

        public bool IsFlying { get; private set; }

        public event Action<bool> FlyingChanged;

        static readonly RaycastHit[] s_hits = new RaycastHit[16];

        public void SetFlying(bool on)
        {
            if (on == IsFlying) return;
            Vector3 ground = default, landing = default;
            if (!on)
            {
                // Without any surface below and no NavMesh to fall back on, turning collisions on would drop the
                // player into the void: stay in fly mode.
                bool hasGround = TryFindGround(out ground);
                if (!hasGround && !_bootstrap.IsLocomotionReady)
                {
                    Debug.LogWarning("[XRLoco] Fly mode stays on: there is no surface below the player", this);
                    FlyingChanged?.Invoke(true); // resync the menu toggle
                    return;
                }
                // Rig origin sits HeightOffset above the feet.
                if (!hasGround) ground = new Vector3(_head.position.x, _playerOrigin.position.y - _locomotor.HeightOffset, _head.position.z);
                landing = _bootstrap.ResolveLanding(ground);
            }
            IsFlying = on;
            if (on)
            {
                _gate.Acquire(this);
                _bootstrap.GuardPaused = true;
                _locomotor.DisableMovement();
                _locomotor.enabled = false;
                _armed = true;
                if (_hudLabel != null) _hudLabel.SetActive(true);
                Debug.Log("[XRLoco] Fly mode on", this);
            }
            else
            {
                // Fall where you are: put the character's feet under the head at the rig's current height.
                var feet = new Vector3(_head.position.x, _playerOrigin.position.y - _locomotor.HeightOffset, _head.position.z);
                _character.SetPosition(CapsuleClearance.CenterForFeet(_character, feet));
                _locomotor.enabled = true;
                _locomotor.EnableMovement();
                // The surface below is not walkable from the current area (roof, outside ground, furniture top):
                // land on the closest walkable point instead.
                if (Vector3.ProjectOnPlane(landing - ground, Vector3.up).sqrMagnitude > 0.05f * 0.05f
                    || Mathf.Abs(landing.y - ground.y) > 0.3f)
                    _bootstrap.TeleportTo(landing);
                _gate.Release(this);
                _bootstrap.GuardPaused = false;
                if (_hudLabel != null) _hudLabel.SetActive(false);
                Debug.Log("[XRLoco] Fly mode off", this);
            }
            FlyingChanged?.Invoke(on);
        }

        void OnDisable()
        {
            if (IsFlying) SetFlying(false);
        }

        /// <summary>Closest collider surface straight below the head (ignoring the player rig).</summary>
        bool TryFindGround(out Vector3 point)
        {
            point = default;
            int n = Physics.RaycastNonAlloc(_head.position, Vector3.down, s_hits, 200f, _character.LayerMask,
                QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            var rigRoot = _character.transform.root;
            for (int i = 0; i < n; i++)
            {
                if (s_hits[i].collider.transform.root == rigRoot || s_hits[i].distance >= best) continue;
                best = s_hits[i].distance;
                point = s_hits[i].point;
            }
            return best < float.MaxValue;
        }

        void Update()
        {
            if (!IsFlying || _gate.IsHeldByOtherThan(this)) return;
            float dt = Time.deltaTime;

            Vector2 stick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
            if (stick.magnitude < _deadZone) stick = Vector2.zero;
            float vertical = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.RTouch)
                           - OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.LTouch);
            var fwd = Vector3.ProjectOnPlane(_head.forward, Vector3.up).normalized;
            var right = Vector3.ProjectOnPlane(_head.right, Vector3.up).normalized;
            _playerOrigin.position += (fwd * stick.y + right * stick.x) * (_speed * dt)
                                    + Vector3.up * (vertical * _verticalSpeed * dt);

            float x = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch).x;
            if (_armed && Mathf.Abs(x) > FireThreshold)
            {
                _armed = false;
                _playerOrigin.RotateAround(_head.position, Vector3.up, Mathf.Sign(x) * _snapAngle);
            }
            else if (!_armed && Mathf.Abs(x) < ArmThreshold) _armed = true;
        }
    }
}
