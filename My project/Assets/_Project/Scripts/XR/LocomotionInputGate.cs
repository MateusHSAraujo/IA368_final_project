using System.Collections.Generic;
using UnityEngine;

namespace VRRobot.XR
{
    /// <summary>
    /// Suspends Meta's locomotion input interactors while any owner (menu, fly mode) holds the gate.
    /// </summary>
    public class LocomotionInputGate : MonoBehaviour
    {
        [SerializeField, Tooltip("Locomotion interactors (left slide, right turner, right teleport) to suspend.")]
        GameObject[] _locomotionObjects;

        readonly HashSet<object> _owners = new HashSet<object>();
        bool[] _saved;

        public bool IsHeld => _owners.Count > 0;

        public bool IsHeldByOtherThan(object owner) => _owners.Count > (_owners.Contains(owner) ? 1 : 0);

        public void Acquire(object owner)
        {
            if (owner == null || !_owners.Add(owner) || _owners.Count != 1) return;
            _saved = new bool[_locomotionObjects.Length];
            for (int i = 0; i < _locomotionObjects.Length; i++)
            {
                if (_locomotionObjects[i] == null) continue;
                _saved[i] = _locomotionObjects[i].activeSelf;
                _locomotionObjects[i].SetActive(false);
            }
        }

        public void Release(object owner)
        {
            if (owner == null || !_owners.Remove(owner) || _owners.Count != 0 || _saved == null) return;
            for (int i = 0; i < _locomotionObjects.Length; i++)
                if (_locomotionObjects[i] != null) _locomotionObjects[i].SetActive(_saved[i]);
            _saved = null;
        }
    }
}
