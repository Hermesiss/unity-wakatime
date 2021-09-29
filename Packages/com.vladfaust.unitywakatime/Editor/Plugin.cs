#if (UNITY_EDITOR)

using System;
using System.IO;
using System.Net.Http;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Networking;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.SceneManagement;

// Heavily inspired by https://github.com/bengsfort/WakaTime-Unity

namespace WakaTime {
  [InitializeOnLoad]
  public class Plugin {
    public const string API_KEY_PREF = "WakaTime/APIKey";
    public const string ENABLED_PREF = "WakaTime/Enabled";
    public const string DEBUG_PREF = "WakaTime/Debug";
    public const string WAKATIME_PROJECT_FILE = ".wakatime-project";

    public static string ProjectName { get; private set; }

    private static string _apiKey = "";
    private static bool _enabled = true;
    private static bool _debug = true;

    private const string URL_PREFIX = "https://api.wakatime.com/api/v1/";
    private const int HEARTBEAT_COOLDOWN = 1;

    private static Heartbeat _lastHeartbeat;

    static Plugin() {
      Initialize();
    }

    public static void Initialize() {
      if (EditorPrefs.HasKey(ENABLED_PREF))
        _enabled = EditorPrefs.GetBool(ENABLED_PREF);

      if (EditorPrefs.HasKey(DEBUG_PREF))
        _debug = EditorPrefs.GetBool(DEBUG_PREF);

      if (!_enabled) {
        if (_debug) Debug.Log("<WakaTime> Explicitly disabled, skipping initialization...");
        return;
      }

      if (EditorPrefs.HasKey(API_KEY_PREF)) {
        _apiKey = EditorPrefs.GetString(API_KEY_PREF);
      }

      if (_apiKey == string.Empty) {
        Debug.LogWarning("<WakaTime> API key is not set, skipping initialization...");
        return;
      }

      ProjectName = GetProjectName();

      if (_debug) Debug.Log("<WakaTime> Initializing...");

      SendHeartbeat();
      LinkCallbacks();
    }

    /// <summary>
    /// Reads .wakatime-project file
    /// <seealso cref="https://wakatime.com/faq#rename-projects"/>
    /// </summary>
    /// <returns>Lines of .wakatime-project or null if file not found</returns>
    public static string[] GetProjectFile() =>
      !File.Exists(WAKATIME_PROJECT_FILE) ? null : File.ReadAllLines(WAKATIME_PROJECT_FILE);

    /// <summary>
    /// Rewrites o creates new .wakatime-project file with given lines
    /// <seealso cref="https://wakatime.com/faq#rename-projects"/>
    /// </summary>
    /// <example>
    /// <code>
    /// project-override-name
    /// branch-override-name
    /// </code>
    /// </example>
    /// <param name="content"></param>
    public static void SetProjectFile(string[] content) {
      File.WriteAllLines(WAKATIME_PROJECT_FILE, content);
    }

    [Serializable]
    private struct Response<T> {
      public string error;
      public T data;
    }

    [Serializable]
    private struct HeartbeatResponse {
      public string id;
      public string entity;
      public string type;
      public double time;

      public HeartbeatResponse(string id, string entity, string type, double time) {
        this.id = id;
        this.entity = entity;
        this.type = type;
        this.time = time;
      }

      public override string ToString() {
        return $"{id}, {entity}, {type}, {time}";
      }
    }

    [Serializable]
    private struct Heartbeat {
      public string entity;
      public string type;
      public double time;
      public string project;
      public string branch;
      public string plugin;
      public string language;
      public bool is_write;
      public bool is_debugging;

      public Heartbeat(string file, bool save = false) {
        entity = file == string.Empty ? "Unsaved Scene" : file;
        type = "file";
        time = (double) DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalSeconds;
        project = ProjectName;
        plugin = "unity-wakatime";
        branch = "master";
        language = "unity";
        is_write = save;
        is_debugging = _debug;
      }

      public override string ToString() {
        return $"{entity}, {type}, {time}";
      }
    }

    private static void SendHeartbeat(bool fromSave = false) {
      var currentScene = SceneManager.GetActiveScene().path;
      var file = currentScene != string.Empty
        ? Application.dataPath + "/" + currentScene.Substring("Assets/".Length)
        : string.Empty;

      var heartbeat = new Heartbeat(file, fromSave);
      if (!fromSave && (heartbeat.time - _lastHeartbeat.time < HEARTBEAT_COOLDOWN)) {
        if (_debug) {
          Debug.Log($"{heartbeat.time}, {_lastHeartbeat.time}, {HEARTBEAT_COOLDOWN}");
          Debug.Log("<WakaTime> Skip this heartbeat");
        }
        return;
      }

      var cachedHeartbeat = _lastHeartbeat;

      _lastHeartbeat = heartbeat;

      var heartbeatJSON = JsonUtility.ToJson(heartbeat);
      
      if (_debug) Debug.Log($"<WakaTime> Sending heartbeat... {heartbeatJSON}");

      var request = UnityWebRequest.Post(URL_PREFIX + "users/current/heartbeats?api_key=" + _apiKey, string.Empty);
      request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(heartbeatJSON));
      request.SetRequestHeader("Content-Type", "application/json");

      request.SendWebRequest().completed +=
        operation => {
          var downloadHandlerText = request.downloadHandler.text;
          if (downloadHandlerText == string.Empty) {
            Debug.LogWarning(
              "<WakaTime> Network is unreachable. Consider disabling completely if you're working offline");
            return;
          }

          if (_debug) {
            Debug.Log("<WakaTime> Got response\n" + downloadHandlerText);
            if (!HandleHttpResponseSuccess(request)) return;
          }
          
          Response<HeartbeatResponse>? r = null;
          try {
            r = JsonUtility.FromJson<Response<HeartbeatResponse>>(downloadHandlerText);
          }
          catch (Exception e) {
            Debug.LogError($"<WakaTime> error: {e}\n Raw response: {downloadHandlerText}");
            throw;
          }

          var response = r.Value;

          if (response.error != null) {
            _lastHeartbeat = cachedHeartbeat;
            if (response.error == "Duplicate") {
              if (_debug) Debug.LogWarning("<WakaTime> Duplicate heartbeat");
            }
            else {
              Debug.LogError(
                "<WakaTime> Failed to send heartbeat to WakaTime!\n" +
                response.error);
            }
          }
          else {
            if (_debug) {
              Debug.Log($"<WakaTime> Sent heartbeat! {response.data}");
            }
            //_lastHeartbeat = response.data;
          }
        };
    }

    /// <summary>
    /// Checks for status code and takes correspond actions 
    /// </summary>
    /// <param name="request"></param>
    /// <returns>Is request fine for further operations</returns>
    private static bool HandleHttpResponseSuccess(UnityWebRequest request) {
      var code = request.responseCode;
      switch (code) {
        case 200:
        case 201:
        case 202:
          return true;
        case 400:
        case 401:
        case 403:
        case 404:
          Debug.LogException(new HttpRequestException($"<WakaTime> {request.error}"));
          break;
        case 429:
          //TODO: cooldown
          if (_debug)
            Debug.LogWarning("<WakaTime> Too many request, cooling down");
          break;
        case 500:
          break;
        default:
          throw new HttpRequestException($"<WakaTime> Unable to decide what to do with this code: {code}");
      }
      return false;
    }

    [DidReloadScripts]
    private static void OnScriptReload() {
      Initialize();
    }

    private static void OnPlaymodeStateChanged(PlayModeStateChange change) {
      SendHeartbeat();
    }

    private static void OnPropertyContextMenu(GenericMenu menu, SerializedProperty property) {
      SendHeartbeat();
    }

    private static void OnHierarchyWindowChanged() {
      SendHeartbeat();
    }

    private static void OnSceneSaved(Scene scene) {
      SendHeartbeat(true);
    }

    private static void OnSceneOpened(Scene scene, OpenSceneMode mode) {
      SendHeartbeat();
    }

    private static void OnSceneClosing(Scene scene, bool removingScene) {
      SendHeartbeat();
    }

    private static void OnSceneCreated(Scene scene, NewSceneSetup setup, NewSceneMode mode) {
      SendHeartbeat();
    }

    private static void LinkCallbacks(bool clean = false) {
      if (clean) {
        EditorApplication.playModeStateChanged -= OnPlaymodeStateChanged;
        EditorApplication.contextualPropertyMenu -= OnPropertyContextMenu;
        #if UNITY_2018_1_OR_NEWER
        EditorApplication.hierarchyChanged -= OnHierarchyWindowChanged;
        #else
          EditorApplication.hierarchyWindowChanged -= OnHierarchyWindowChanged;
        #endif
        EditorSceneManager.sceneSaved -= OnSceneSaved;
        EditorSceneManager.sceneOpened -= OnSceneOpened;
        EditorSceneManager.sceneClosing -= OnSceneClosing;
        EditorSceneManager.newSceneCreated -= OnSceneCreated;
      }

      EditorApplication.playModeStateChanged += OnPlaymodeStateChanged;
      EditorApplication.contextualPropertyMenu += OnPropertyContextMenu;
      #if UNITY_2018_1_OR_NEWER
      EditorApplication.hierarchyChanged += OnHierarchyWindowChanged;
      #else
        EditorApplication.hierarchyWindowChanged += OnHierarchyWindowChanged;
      #endif
      EditorSceneManager.sceneSaved += OnSceneSaved;
      EditorSceneManager.sceneOpened += OnSceneOpened;
      EditorSceneManager.sceneClosing += OnSceneClosing;
      EditorSceneManager.newSceneCreated += OnSceneCreated;
    }

    /// <summary>
    /// Project name for sending <see cref="Heartbeat"/>
    /// </summary>
    /// <returns><see cref="Application.productName"/> or first line of .wakatime-project</returns>
    private static string GetProjectName() =>
      File.Exists(WAKATIME_PROJECT_FILE)
        ? File.ReadAllLines(WAKATIME_PROJECT_FILE)[0]
        : Application.productName;
  }
}

#endif
