using System;
using System.Globalization;
using UnityEngine;
using VRRobot.Core;
using VRRobot.XR;

namespace VRRobot.HMI
{
    /// <summary>
    /// Owner of the teleop mode (Stage 6): X on the left controller, or <see cref="Toggle"/> from the menu, opens it.
    /// While it is open the robot camera is enabled, the player's locomotion is suspended
    /// (<see cref="LocomotionInputGate"/>) and the sticks drive the robot through <c>/cmd_vel_teleop</c>:
    /// left stick Y = forward / backward, right stick X = rotation (left = counter-clockwise).
    /// </summary>
    /// <remarks>
    /// There is no grip deadman: the spring-centred sticks are the deadman. Commands are published at a fixed rate
    /// only while a stick is deflected; one zero Twist is sent when both sticks return to the centre, when the mode
    /// closes, and while input is suspended (no OVR input focus; in a player build also application pause / focus loss,
    /// which the Editor skips because an unfocused Editor reports focus loss while the simulator still drives it). If ROS
    /// disconnects nothing can be sent, and the ROS side stops the robot by itself (twist_mux timeout + bridge watchdog,
    /// 0.5 s each). The mode closes when the start flow leaves the Playing phase. The view (<see cref="CameraPanel"/>)
    /// follows <see cref="ActiveChanged"/>.
    /// </remarks>
    public class FirstPersonTeleopController : MonoBehaviour
    {
        const string LogPrefix = "[HMI] ";

        [Header("Scene references")]
        [SerializeField] LocomotionInputGate _gate;
        [SerializeField, Tooltip("Optional: when assigned, the mode only opens in the Playing phase (after the spawn).")]
        AppFlowController _appFlow;

        [Header("Input")]
        [SerializeField, Tooltip("Opens and closes the teleop mode (X on the left controller).")]
        OVRInput.Button _toggleButton = OVRInput.Button.Three;
        [SerializeField, Range(0f, 0.9f)] float _deadzone = 0.15f;
        [SerializeField, Range(1f, 3f), Tooltip("Response curve: 1 = linear, larger = finer control near the centre.")]
        float _exponent = 1.5f;

        [Header("Command")]
        [SerializeField, Tooltip("m/s; clamped to TeleopMath.MaxLinear (contract limit).")]
        float _maxLinear = TeleopMath.MaxLinear;
        [SerializeField, Tooltip("rad/s; clamped to TeleopMath.MaxAngular (contract limit).")]
        float _maxAngular = TeleopMath.MaxAngular;
        [SerializeField, Tooltip("Publish rate while driving (Hz); the contract requires at least 10.")]
        float _rate = 15f;

        public bool IsActive { get; private set; }
        public event Action<bool> ActiveChanged;

        /// <summary>True while a nonzero command is being published.</summary>
        public bool IsDriving { get; private set; }
        /// <summary>Last commanded linear.x (m/s) and angular.z (rad/s), ROS convention.</summary>
        public float CurrentLinear { get; private set; }
        public float CurrentAngular { get; private set; }

        float _nextSend;
        bool _suspended;

        public void Open()
        {
            if (IsActive) return;
            if (_appFlow != null && _appFlow.CurrentPhase != AppFlowController.Phase.Playing)
            {
                Debug.Log(LogPrefix + "teleop ignored: the start flow is not finished");
                return;
            }
            IsActive = true;
            if (_gate != null) _gate.Acquire(this);
            var ros = RosHmiClient.Instance;
            if (ros != null) ros.SetCameraEnabled(true);
            Debug.Log(LogPrefix + "teleop opened");
            ActiveChanged?.Invoke(true);
        }

        public void Close()
        {
            if (!IsActive) return;
            StopDriving();
            IsActive = false;
            var ros = RosHmiClient.Instance;
            if (ros != null) ros.SetCameraEnabled(false);
            if (_gate != null) _gate.Release(this);
            Debug.Log(LogPrefix + "teleop closed");
            ActiveChanged?.Invoke(false);
        }

        public void Toggle()
        {
            if (IsActive) Close(); else Open();
        }

        void OnDisable() => Close();

        void OnApplicationPause(bool paused) => SetSuspended(paused);

        void OnApplicationFocus(bool focused) => SetSuspended(!focused);

        void SetSuspended(bool suspended)
        {
#if UNITY_EDITOR
            // An unfocused Editor reports focus loss although the simulator / Link keep driving it.
            _suspended = false;
            if (suspended) StopDriving();
#else
            _suspended = suspended;
            if (suspended) StopDriving();
#endif
        }

        void Update()
        {
            if (OVRInput.GetDown(_toggleButton)) Toggle();
            if (!IsActive) return;

            // Returning to the main menu (or respawning) ends the mode: gate, camera and driving off.
            if (_appFlow != null && _appFlow.CurrentPhase != AppFlowController.Phase.Playing)
            {
                Close();
                return;
            }

            // No input focus (system menu, headset removed) or suspended app: stop explicitly.
            if (_suspended || (OVRManager.instance != null && !OVRManager.hasInputFocus))
            {
                StopDriving();
                return;
            }

            // Without ROS nothing can be sent (the ROS side stops the robot); drop the stale driving state.
            var client = RosHmiClient.Instance;
            if (client == null || !client.IsConnected)
            {
                StopDriving();
                return;
            }

            // Raw axes, so TeleopMath alone owns the deadzone and the response curve. (In the Meta XR Simulator a
            // thumbstick input of 0.6 reads ~0.3 with both the raw and the virtual axes; full deflection reads 1.)
            float leftY = OVRInput.Get(OVRInput.RawAxis2D.LThumbstick).y;
            float rightX = OVRInput.Get(OVRInput.RawAxis2D.RThumbstick).x;
            TeleopMath.ToTwist(leftY, rightX, _maxLinear, _maxAngular, _deadzone, _exponent, out float lin, out float ang);

            if (TeleopMath.IsZero(lin, ang))
            {
                StopDriving();
                return;
            }

            CurrentLinear = lin;
            CurrentAngular = ang;
            float now = Time.unscaledTime;
            if (!IsDriving)
            {
                IsDriving = true;
                _nextSend = now; // first deflection: publish at once (auto-pauses an active mission)
                Debug.Log(LogPrefix + "teleop driving");
            }
            if (now < _nextSend) return;
            // Accumulate the schedule (not now + period) so the average rate holds at low frame rates; never let it
            // fall more than one period behind (no bursts after a hitch).
            float period = 1f / Mathf.Max(10f, _rate);
            _nextSend = Mathf.Max(_nextSend + period, now - period);
            var ros = RosHmiClient.Instance;
            if (ros != null && ros.IsConnected) ros.SendTeleop(lin, ang);
        }

        /// <summary>Ends a drive: one zero Twist (if a nonzero command was being sent) and zeroed state.</summary>
        void StopDriving()
        {
            bool wasDriving = IsDriving;
            IsDriving = false;
            CurrentLinear = 0f;
            CurrentAngular = 0f;
            if (!wasDriving) return;
            var ros = RosHmiClient.Instance;
            if (ros != null && ros.IsConnected) ros.SendTeleop(0f, 0f);
            Debug.Log(LogPrefix + "teleop stopped");
        }

        /// <summary>Status text for the views, e.g. "driving 0.32 m/s, -0.40 rad/s".</summary>
        public string DescribeCommand() => IsDriving
            ? $"driving {CurrentLinear.ToString("F2", CultureInfo.InvariantCulture)} m/s, " +
              $"{CurrentAngular.ToString("F2", CultureInfo.InvariantCulture)} rad/s"
            : "idle";
    }
}
