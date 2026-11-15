using System;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace VRRobot.HMI
{
    /// <summary>
    /// Map page of the VR menu: current map and last result, the saved maps list (paged, load), save as new /
    /// overwrite, and the two-step confirmed "reset map" and "clear no-go zones". Tolerates a missing
    /// <see cref="RosHmiClient"/>.
    /// </summary>
    public class VrMapPage : MonoBehaviour
    {
        const int ConfirmSeconds = 4;

        [Header("Status")]
        [SerializeField] TMP_Text _mapLabel;
        [SerializeField] TMP_Text _resultLabel;

        [Header("Saved maps (one slot per row)")]
        [SerializeField] TMP_Text[] _rowLabels;
        [SerializeField] Button[] _rowLoadButtons;
        [SerializeField] Button _prevButton;
        [SerializeField] Button _nextButton;
        [SerializeField] TMP_Text _pageLabel;

        [Header("Commands")]
        [SerializeField] Button _saveNewButton;
        [SerializeField] Button _overwriteButton;
        [SerializeField] TMP_Text _overwriteLabel;
        [SerializeField] Button _resetButton;
        [SerializeField] TMP_Text _resetLabel;
        [SerializeField] Button _clearButton;
        [SerializeField] TMP_Text _clearLabel;

        static readonly Color Ok = new Color(0.75f, 1f, 0.75f);
        static readonly Color Bad = new Color(1f, 0.45f, 0.4f);

        [Header("Scene references (fall back to FindFirstObjectByType)")]
        [SerializeField] TacticalToolController _tools;

        RosHmiClient _ros;
        int _page;
        float _resetDeadline = -1f;
        float _clearDeadline = -1f;

        int Rows => Mathf.Max(1, _rowLabels != null ? _rowLabels.Length : 0);

        void Awake()
        {
            if (_tools == null) _tools = FindFirstObjectByType<TacticalToolController>(FindObjectsInactive.Include);
            for (int i = 0; _rowLoadButtons != null && i < _rowLoadButtons.Length; i++)
            {
                int row = i;
                if (_rowLoadButtons[i] != null) _rowLoadButtons[i].onClick.AddListener(() => LoadRow(row));
            }
            if (_prevButton != null) _prevButton.onClick.AddListener(() => { _page--; Refresh(); });
            if (_nextButton != null) _nextButton.onClick.AddListener(() => { _page++; Refresh(); });
            if (_saveNewButton != null)
                _saveNewButton.onClick.AddListener(() => Send("SAVE " + HmiJson.TimestampMapName(DateTime.Now)));
            if (_overwriteButton != null) _overwriteButton.onClick.AddListener(() =>
            {
                var s = _ros != null ? _ros.MapState : null;
                if (s != null && s.map != null) Send("SAVE " + s.map);
            });
            if (_resetButton != null) _resetButton.onClick.AddListener(() =>
            {
                if (!Confirm(ref _resetDeadline)) return;
                Send("RESET");
                Refresh();
            });
            if (_clearButton != null) _clearButton.onClick.AddListener(() =>
            {
                if (!Confirm(ref _clearDeadline)) return;
                if (_ros != null) _ros.ClearKeepout();
                if (_tools != null) _tools.ClearPending();
                Refresh();
            });
        }

        void OnEnable()
        {
            _ros = RosHmiClient.Instance;
            if (_ros != null)
            {
                _ros.ConnectionChanged += OnConnectionChanged;
                _ros.MapStateChanged += OnMapStateChanged;
            }
            _resetDeadline = _clearDeadline = -1f;
            Refresh();
        }

        void OnDisable()
        {
            if (_ros != null)
            {
                _ros.ConnectionChanged -= OnConnectionChanged;
                _ros.MapStateChanged -= OnMapStateChanged;
            }
            _ros = null;
        }

        void Update()
        {
            bool expired = false;
            if (_resetDeadline >= 0f && Time.unscaledTime > _resetDeadline) { _resetDeadline = -1f; expired = true; }
            if (_clearDeadline >= 0f && Time.unscaledTime > _clearDeadline) { _clearDeadline = -1f; expired = true; }
            if (expired) Refresh();
        }

        void OnConnectionChanged(bool connected) => Refresh();
        void OnMapStateChanged(MapState state) => Refresh();

        /// <summary>First click arms the confirmation; a second click before the deadline confirms.</summary>
        bool Confirm(ref float deadline)
        {
            if (deadline >= 0f && Time.unscaledTime <= deadline)
            {
                deadline = -1f;
                return true;
            }
            deadline = Time.unscaledTime + ConfirmSeconds;
            Refresh();
            return false;
        }

        void Send(string cmd)
        {
            if (_ros != null) _ros.SendMapCmd(cmd);
        }

        void LoadRow(int row)
        {
            var s = _ros != null ? _ros.MapState : null;
            int index = _page * Rows + row;
            if (s == null || index >= s.maps.Length) return;
            Send("LOAD " + s.maps[index]);
        }

        static string Shorten(string text, int max) =>
            text.Length <= max ? text : text.Substring(0, max - 2) + "..";

        void Refresh()
        {
            var s = _ros != null ? _ros.MapState : null;
            bool usable = _ros != null && _ros.IsConnected && s != null && !s.IsBusy;
            var maps = s != null ? s.maps : new string[0];

            if (_mapLabel != null)
            {
                _mapLabel.text = s != null ? $"Map: {s.map ?? "-"} ({s.state})"
                                           : (_ros == null ? "ROS: not available" : "Map: no data");
                _mapLabel.color = _ros != null && _ros.IsConnected ? Color.white : Bad;
            }
            if (_resultLabel != null)
            {
                if (s == null || string.IsNullOrEmpty(s.message)) _resultLabel.text = "";
                else
                {
                    _resultLabel.text = (s.ok ? "OK: " : "Failed: ") + s.message;
                    _resultLabel.color = s.ok ? Ok : Bad;
                }
            }

            int pages = Mathf.Max(1, (maps.Length + Rows - 1) / Rows);
            _page = Mathf.Clamp(_page, 0, pages - 1);
            int rowCount = _rowLabels != null ? _rowLabels.Length : 0;
            for (int i = 0; i < rowCount; i++)
            {
                int index = _page * Rows + i;
                bool has = index < maps.Length;
                if (_rowLabels[i] != null)
                    _rowLabels[i].text = has ? Shorten(maps[index], 26) + (maps[index] == s.map ? " (current)" : "")
                                             : (i == 0 && maps.Length == 0 ? "No saved maps" : "");
                if (_rowLoadButtons != null && i < _rowLoadButtons.Length && _rowLoadButtons[i] != null)
                {
                    _rowLoadButtons[i].gameObject.SetActive(has);
                    _rowLoadButtons[i].interactable = usable;
                }
            }
            if (_prevButton != null) _prevButton.interactable = _page > 0;
            if (_nextButton != null) _nextButton.interactable = _page < pages - 1;
            if (_pageLabel != null)
                _pageLabel.text = $"{(_page + 1).ToString(CultureInfo.InvariantCulture)} / {pages.ToString(CultureInfo.InvariantCulture)}";

            if (_saveNewButton != null) _saveNewButton.interactable = usable;
            bool hasMap = s != null && s.map != null;
            if (_overwriteLabel != null) _overwriteLabel.text = hasMap ? "Overwrite " + Shorten(s.map, 12) : "Overwrite";
            if (_overwriteButton != null) _overwriteButton.interactable = usable && hasMap;

            if (_resetLabel != null) _resetLabel.text = _resetDeadline >= 0f ? "Confirm reset?" : "Reset map";
            if (_clearLabel != null) _clearLabel.text = _clearDeadline >= 0f ? "Confirm clear?" : "Clear no-go zones";
            if (_resetButton != null) _resetButton.interactable = usable;
            if (_clearButton != null) _clearButton.interactable = usable;
        }
    }
}
