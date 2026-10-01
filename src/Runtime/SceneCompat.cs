using System.Reflection;
using UnityEngine.SceneManagement;

namespace UnityExplorer.Runtime
{
    /// <summary>
    /// Compatibility helpers for UnityEngine.SceneManagement.Scene handle APIs.
    /// Scene.handle / Scene.m_Handle changed from Int32 to a SceneHandle struct in Unity 6000.3+,
    /// and Scene.GetNameInternal changed parameter type accordingly. All access goes through
    /// reflection so a single build works on both older and newer engines.
    /// </summary>
    internal static class SceneCompat
    {
        private static readonly PropertyInfo handleProperty;
        private static readonly FieldInfo handleField;
        private static readonly MethodInfo sceneNameInternal;

        private static readonly bool handleIsInt = true;
        private static readonly System.Type runtimeHandleType;
        private static readonly MethodInfo handleToInt; // SceneHandle -> int
        private static readonly MethodInfo intToHandle; // int -> SceneHandle

        static SceneCompat()
        {
            try
            {
                handleProperty = typeof(Scene).GetProperty("handle", BindingFlags.Public | BindingFlags.Instance);
                handleField = typeof(Scene).GetField("m_Handle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                runtimeHandleType = handleProperty?.PropertyType;
                handleIsInt = runtimeHandleType == null || runtimeHandleType == typeof(int);

                if (!handleIsInt)
                {
                    handleToInt = FindConvertOperator(runtimeHandleType, typeof(int));
                    intToHandle = FindConvertOperator(typeof(int), runtimeHandleType);
                }

                sceneNameInternal = typeof(Scene).GetMethod(
                    "GetNameInternal", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            }
            catch (System.Exception ex)
            {
                ExplorerCore.LogWarning($"SceneCompat init failed: {ex.Message}");
            }
        }

        private static MethodInfo FindConvertOperator(System.Type from, System.Type to)
        {
            foreach (MethodInfo method in from.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != "op_Implicit" || method.ReturnType != to)
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType == from)
                    return method;
            }
            return null;
        }

        /// <summary>
        /// Get the engine handle of a Scene as an int, converting SceneHandle (Unity 6000.3+) if needed.
        /// Handles round-trip: an int passed to <see cref="CreatePlaceholderScene"/> comes back unchanged.
        /// </summary>
        internal static int GetIntHandle(Scene scene)
        {
            if (handleProperty == null)
                return scene.IsValid() ? 0 : -1;

            object value = handleProperty.GetValue(scene, null);
            if (value is int asInt)
                return asInt;

            if (value == null)
                return -1;

            if (handleToInt != null)
                return (int)handleToInt.Invoke(null, new object[] { value });

            // Fallback for future engines that removed the int conversion operators.
            MethodInfo getRawData = runtimeHandleType.GetMethod("GetRawData", System.Type.EmptyTypes);
            if (getRawData != null && getRawData.ReturnType == typeof(ulong))
                return (int)(ulong)getRawData.Invoke(value, null);

            ExplorerCore.LogWarning("SceneCompat: could not convert Scene handle to int.");
            return -1;
        }

        /// <summary>
        /// Create a synthetic placeholder Scene (used for the DontDestroyOnLoad / HideAndDontSave entries).
        /// </summary>
        internal static Scene CreatePlaceholderScene(int intHandle)
        {
            Scene scene = default;
            if (handleField == null)
                return scene;

            object boxed = scene;
            object value = ToHandleObject(intHandle);
            if (value == null)
                return scene;

            handleField.SetValue(boxed, value);
            return (Scene)boxed;
        }

        /// <summary>
        /// Get the name of a scene from its int handle via Scene.GetNameInternal,
        /// converting the argument on Unity 6000.3+. Returns null if unavailable.
        /// </summary>
        internal static string GetNameOfHandle(int intHandle)
        {
            if (sceneNameInternal == null)
                return null;

            try
            {
                ParameterInfo parameter = sceneNameInternal.GetParameters()[0];
                object argument = parameter.ParameterType == typeof(int)
                    ? (object)intHandle
                    : ToHandleObject(intHandle);

                if (argument == null)
                    return null;

                return sceneNameInternal.Invoke(null, new object[] { argument }) as string;
            }
            catch (System.Exception ex)
            {
                ExplorerCore.LogWarning($"SceneCompat.GetNameOfHandle({intHandle}) failed: {ex.Message}");
                return null;
            }
        }

        private static object ToHandleObject(int intHandle)
        {
            if (handleIsInt)
                return intHandle;

            if (intToHandle != null)
                return intToHandle.Invoke(null, new object[] { intHandle });

            if (runtimeHandleType != null)
            {
                // Fallback for future engines that removed the int conversion operators.
                MethodInfo fromRawData = runtimeHandleType.GetMethod(
                    "FromRawData", BindingFlags.Public | BindingFlags.Static, null, new System.Type[] { typeof(ulong) }, null);
                if (fromRawData != null)
                    return fromRawData.Invoke(null, new object[] { (ulong)intHandle }); // sign-extend, matching Unity's own int->SceneHandle conversion
            }

            ExplorerCore.LogWarning("SceneCompat: could not convert int to Scene handle.");
            return null;
        }
    }
}
