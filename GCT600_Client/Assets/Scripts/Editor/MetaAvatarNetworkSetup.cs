using Oculus.Avatar2;
using UnityEditor;
using UnityEngine;

public static class MetaAvatarNetworkSetup
{
    [MenuItem("GCT600/Meta Avatar/Configure Selected As Receiver")]
    private static void ConfigureReceiver()
    {
        var avatar = Selection.activeGameObject.GetComponent<OvrAvatarEntity>();
        Undo.SetCurrentGroupName("Configure remote Meta Avatar");
        int group = Undo.GetCurrentGroup();
        var serialized = new SerializedObject(avatar);
        serialized.FindProperty("_isLocal").boolValue = false;
        serialized.FindProperty("_creationInfo.features").intValue = (int)CAPI.ovrAvatar2EntityFeatures.Preset_Remote;
        serialized.FindProperty("_activeView").intValue = (int)CAPI.ovrAvatar2EntityViewFlags.ThirdPerson;
        serialized.FindProperty("_activeManifestation").intValue = (int)CAPI.ovrAvatar2EntityManifestationFlags.Full;
        serialized.FindProperty("_creationInfo.renderFilters.viewFlags").intValue |= (int)CAPI.ovrAvatar2EntityViewFlags.ThirdPerson;
        serialized.FindProperty("_creationInfo.renderFilters.manifestationFlags").intValue |= (int)CAPI.ovrAvatar2EntityManifestationFlags.Full;
        foreach (string field in new[] { "_inputManager", "_lipSync", "_facePoseBehavior", "_eyePoseBehavior" })
            serialized.FindProperty(field).objectReferenceValue = null;
        serialized.ApplyModifiedProperties();
        foreach (MonoBehaviour component in avatar.GetComponents<MonoBehaviour>())
        {
            if (component != null && (component.GetType().Name == "OvrAvatarAnimationBehavior" || component is MetaAvatarSender))
            {
                Undo.RecordObject(component, "Disable local avatar driver");
                component.enabled = false;
                PrefabUtility.RecordPrefabInstancePropertyModifications(component);
            }
        }
        var receiver = avatar.GetComponent<MetaAvatarReceiver>();
        if (receiver == null) receiver = Undo.AddComponent<MetaAvatarReceiver>(avatar.gameObject);
        Undo.RecordObject(receiver, "Assign receiver avatar");
        receiver.avatar = avatar;
        receiver.enabled = true;
        PrefabUtility.RecordPrefabInstancePropertyModifications(receiver);
        Undo.CollapseUndoOperations(group);
        Debug.Log("Remote avatar configured. Use the SAME sample preset/quality as the sender. " +
            "Set receiver connection and Display Origin; see docs/AVATAR_NETWORKING.md.", avatar);
    }

    [MenuItem("GCT600/Meta Avatar/Configure Selected As Receiver", true)]
    private static bool CanConfigureReceiver()
    {
        return !EditorApplication.isPlayingOrWillChangePlaymode && Selection.activeGameObject != null &&
            Selection.activeGameObject.GetComponent<OvrAvatarEntity>() != null;
    }
}
