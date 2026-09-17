using System;
using GCT600.AvatarNetworking;
using Oculus.Avatar2;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class MetaAvatarReceiver : MonoBehaviour
{
    [Header("Remote Meta Avatar")]
    public OvrAvatarEntity avatar;
    [Tooltip("Apply transmitted root position/rotation. Disable to keep the remote avatar at a fixed location.")]
    public bool applyRootPose = true;
    [Tooltip("Maps the sender's reference space into this scene. Must NOT be the avatar or a child of it.")]
    public Transform displayOrigin;

    [Header("Relay")]
    public bool connectToRelay = true;
    public string serverAddress = "127.0.0.1";
    public int serverPort = 5060;
    public string channel = "quest-user";

    [Header("Runtime status")]
    [SerializeField] private string connectionStatus;
    [SerializeField] private int packetsApplied;
    [SerializeField] private uint lastSequence;
    [SerializeField] private float secondsSinceLastPose = -1;
    [SerializeField] private bool poseStale = true;

    private MetaAvatarRelayConnection connection;
    private byte[] pendingPacket;
    private float lastPoseTime = -1;
    private bool errorReported;

    private void Reset() { avatar = GetComponent<OvrAvatarEntity>(); }

    private void OnEnable()
    {
        if (avatar == null) avatar = GetComponent<OvrAvatarEntity>();
        if (!ValidateAvatar()) { enabled = false; return; }
        errorReported = false;
        lastPoseTime = -1;
        if (connectToRelay)
        {
            try { connection = new MetaAvatarRelayConnection(serverAddress, serverPort, channel, false); }
            catch (Exception ex)
            {
                Debug.LogError("[MetaAvatarReceiver] " + ex.Message, this);
                enabled = false;
            }
        }
    }

    private bool ValidateAvatar()
    {
        if (avatar == null || avatar.IsLocal || !avatar.HasAllFeatures(CAPI.ovrAvatar2EntityFeatures.Preset_Remote) ||
            avatar.HasAnyFeatures(CAPI.ovrAvatar2EntityFeatures.Animation) || avatar.InputManager != null)
        {
            Debug.LogError("[MetaAvatarReceiver] Use a REMOTE avatar: Is Local off, Features = Preset_Remote, " +
                "Animation off, Tracking Input Manager = None. See docs/AVATAR_NETWORKING.md.", this);
            return false;
        }
        // Sample scripts live in an optional imported assembly. Do not require it to compile this component.
        foreach (MonoBehaviour component in avatar.GetComponents<MonoBehaviour>())
        {
            if (component != null && component.enabled && component.GetType().Name == "OvrAvatarAnimationBehavior")
            {
                Debug.LogError("[MetaAvatarReceiver] Disable OvrAvatarAnimationBehavior on the REMOTE avatar.", this);
                return false;
            }
        }
        if (displayOrigin != null && (displayOrigin == avatar.transform || displayOrigin.IsChildOf(avatar.transform)))
        {
            Debug.LogError("[MetaAvatarReceiver] Display Origin must be outside the avatar hierarchy.", this);
            return false;
        }
        return true;
    }

    // Called by MetaAvatarSender on the main thread for the initial, network-free test.
    public void QueueLocalTestPacket(byte[] packet)
    {
        if (isActiveAndEnabled && !connectToRelay) pendingPacket = packet;
    }

    private void Update()
    {
        connectionStatus = connectToRelay ? connection?.Status ?? "Stopped" : "In-scene loopback";
        secondsSinceLastPose = lastPoseTime < 0 ? -1 : Time.unscaledTime - lastPoseTime;
        poseStale = lastPoseTime < 0 || secondsSinceLastPose > 1f;
        if (connectToRelay)
        {
            if (connection == null || !connection.Connected) pendingPacket = null;
            else if (connection.TryTake(out byte[] bytes)) pendingPacket = bytes;
        }
        if (pendingPacket == null || !avatar.HasJoints) return;
        byte[] latest = pendingPacket;
        pendingPacket = null;
        try
        {
            var packet = MetaAvatarNetworkProtocol.Decode(latest);
            // All SDK and Transform operations stay on the Unity main thread.
            if (!avatar.ApplyStreamData(packet.AvatarData))
            {
                if (!errorReported) Debug.LogWarning("[MetaAvatarReceiver] ApplyStreamData failed. Check the same " +
                    "SDK version, preset/quality, and remote entity settings on both ends.", this);
                errorReported = true;
                return;
            }
            if (applyRootPose)
            {
                float[] root = packet.Root;
                Vector3 position = new Vector3(root[0], root[1], root[2]);
                Quaternion rotation = new Quaternion(root[3], root[4], root[5], root[6]).normalized;
                if (displayOrigin != null)
                {
                    position = displayOrigin.TransformPoint(position);
                    rotation = displayOrigin.rotation * rotation;
                }
                avatar.transform.SetPositionAndRotation(position, rotation);
            }
            packetsApplied++;
            lastSequence = packet.Sequence;
            lastPoseTime = Time.unscaledTime;
            errorReported = false;
        }
        catch (Exception ex)
        {
            if (!errorReported) Debug.LogError("[MetaAvatarReceiver] " + ex.Message, this);
            errorReported = true;
        }
    }

    private void OnDisable()
    {
        connection?.Dispose();
        connection = null;
        pendingPacket = null;
        connectionStatus = "Stopped";
    }
}
