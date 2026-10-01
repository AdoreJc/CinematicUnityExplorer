using UnityEngine.SceneManagement;
using UnityExplorer.Runtime;

namespace UnityExplorer.ObjectExplorer
{
    public static class SceneHandler
    {
        /// <summary>The currently inspected Scene.</summary>
        public static Scene? SelectedScene
        {
            get => selectedScene;
            internal set
            {
                if (selectedScene.HasValue && selectedScene == value)
                    return;
                selectedScene = value;
                OnInspectedSceneChanged?.Invoke((Scene)selectedScene);
            }
        }
        private static Scene? selectedScene;

        /// <summary>The GameObjects in the currently inspected scene.</summary>
        public static IEnumerable<GameObject> CurrentRootObjects { get; private set; } = new GameObject[0];

        /// <summary>All currently loaded Scenes.</summary>
        public static List<Scene> LoadedScenes { get; private set; } = new();
        //private static HashSet<Scene> previousLoadedScenes;

        /// <summary>The names of all scenes in the build settings, if they could be retrieved.</summary>
        public static List<string> AllSceneNames { get; private set; } = new();

        /// <summary>Invoked when the currently inspected Scene changes. The argument is the new scene.</summary>
        public static event Action<Scene> OnInspectedSceneChanged;

        /// <summary>Invoked whenever the list of currently loaded Scenes changes. The argument contains all loaded scenes after the change.</summary>
        public static event Action<List<Scene>> OnLoadedScenesUpdated;

        /// <summary>Generally will be 2, unless DontDestroyExists == false, then this will be 1.</summary>
        internal static int DefaultSceneCount => 1 + (DontDestroyExists ? 1 : 0);

        /// <summary>Whether or not we are currently inspecting the "HideAndDontSave" asset scene.</summary>
        public static bool InspectingAssetScene => SelectedScene.HasValue && SceneCompat.GetIntHandle(SelectedScene.Value) == -1;

        /// <summary>Whether or not we successfuly retrieved the names of the scenes in the build settings.</summary>
        public static bool WasAbleToGetScenesInBuild { get; private set; }

        /// <summary>Whether or not the "DontDestroyOnLoad" scene exists in this game.</summary>
        public static bool DontDestroyExists { get; private set; }

        private const string DONT_DESTROY_NAME = "DontDestroyOnLoad";

        internal static void Init()
        {
            // Check if the game has "DontDestroyOnLoad".
            // Scene.GetNameInternal's signature changed in Unity 6000.3+ (int -> SceneHandle),
            // so SceneCompat converts the argument reflectively to support both.
            DontDestroyExists = SceneCompat.GetNameOfHandle(-12) == DONT_DESTROY_NAME;

            // Fallback 1: enumerate the SceneManager's scene list by name.
            if (!DontDestroyExists)
            {
                try
                {
                    for (int i = 0; i < SceneManager.sceneCount; i++)
                    {
                        if (SceneManager.GetSceneAt(i).name == DONT_DESTROY_NAME)
                        {
                            DontDestroyExists = true;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    ExplorerCore.LogWarning($"Unable to check DontDestroyOnLoad via SceneManager: {ex.Message}");
                }
            }

            // Fallback 2 (last resort, expensive): find a loaded GameObject living in the DontDestroyOnLoad scene.
            if (!DontDestroyExists)
            {
                try
                {
                    foreach (UnityEngine.Object obj in RuntimeHelper.FindObjectsOfTypeAll(typeof(GameObject)))
                    {
                        GameObject go = obj.TryCast<GameObject>();
                        if (go && go.transform != null && go.scene.IsValid() && go.scene.name == DONT_DESTROY_NAME)
                        {
                            DontDestroyExists = true;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    ExplorerCore.LogWarning($"Unable to check DontDestroyOnLoad via GameObjects: {ex.Message}");
                }
            }

            // Try to get all scenes in the build settings. This may not work.
            try
            {
                Type sceneUtil = ReflectionUtility.GetTypeByName("UnityEngine.SceneManagement.SceneUtility");
                if (sceneUtil == null)
                    throw new Exception("This version of Unity does not ship with the 'SceneUtility' class, or it was not unstripped.");

                System.Reflection.MethodInfo method = sceneUtil.GetMethod("GetScenePathByBuildIndex", ReflectionUtility.FLAGS);
                int sceneCount = SceneManager.sceneCountInBuildSettings;
                for (int i = 0; i < sceneCount; i++)
                {
                    string scenePath = (string)method.Invoke(null, new object[] { i });
                    AllSceneNames.Add(scenePath);
                }

                WasAbleToGetScenesInBuild = true;
            }
            catch (Exception ex)
            {
                WasAbleToGetScenesInBuild = false;
                ExplorerCore.LogWarning($"Unable to generate list of all Scenes in the build: {ex}");
            }
        }

        internal static void Update()
        {
            // Inspected scene will exist if it's DontDestroyOnLoad or HideAndDontSave
            bool inspectedExists =
                SelectedScene.HasValue
                && ((DontDestroyExists && SceneCompat.GetIntHandle(SelectedScene.Value) == -12)
                    || SceneCompat.GetIntHandle(SelectedScene.Value) == -1);

            LoadedScenes.Clear();
            bool realDontDestroyLoaded = false;

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene == default || !scene.isLoaded || !scene.IsValid())
                    continue;

                if (scene.name == DONT_DESTROY_NAME)
                    realDontDestroyLoaded = true;

                // If we have not yet confirmed inspectedExists, check if this scene is our currently inspected one.
                if (!inspectedExists && scene == SelectedScene)
                    inspectedExists = true;

                LoadedScenes.Add(scene);
            }

            // If the real DontDestroyOnLoad scene was enumerated, remap a selected synthetic placeholder
            // (older engines return DontDestroyOnLoad only as a placeholder; Unity 6000.3+ may enumerate it).
            // Membership check instead of name lookup: a synthetic scene's name is not trustworthy on every engine.
            if (realDontDestroyLoaded && DontDestroyExists
                && SelectedScene.HasValue
                && !LoadedScenes.Contains(SelectedScene.Value)
                && SceneCompat.GetIntHandle(SelectedScene.Value) == -12)
            {
                Scene realDontDestroy = LoadedScenes.First(s => s.name == DONT_DESTROY_NAME);
                SelectedScene = realDontDestroy;
                inspectedExists = true;
            }

            // Only add a synthetic DontDestroyOnLoad placeholder if the real one wasn't enumerated
            // (older Unity engines don't return it from SceneManager.GetSceneAt; Unity 6000.3+ may).
            if (DontDestroyExists && !realDontDestroyLoaded)
                LoadedScenes.Add(SceneCompat.CreatePlaceholderScene(-12));
            LoadedScenes.Add(SceneCompat.CreatePlaceholderScene(-1));

            // Default to first scene if none selected or previous selection no longer exists.
            if (!inspectedExists)
                SelectedScene = LoadedScenes.First();

            // Notify on the list changing at all
            OnLoadedScenesUpdated?.Invoke(LoadedScenes);

            // Finally, update the root objects list.
            if (SelectedScene != null && ((Scene)SelectedScene).IsValid())
                CurrentRootObjects = RuntimeHelper.GetRootGameObjects((Scene)SelectedScene);
            else
            {
                UnityEngine.Object[] allObjects = RuntimeHelper.FindObjectsOfTypeAll(typeof(GameObject));
                List<GameObject> objects = new();
                foreach (UnityEngine.Object obj in allObjects)
                {
                    GameObject go = obj.TryCast<GameObject>();
                    if (go.transform.parent == null && !go.scene.IsValid())
                        objects.Add(go);
                }
                CurrentRootObjects = objects;
            }
        }
    }
}
