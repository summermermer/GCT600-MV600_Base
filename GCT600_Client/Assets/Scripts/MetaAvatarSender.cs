using System;
using GCT600.AvatarNetworking;
using Oculus.Avatar2;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class MetaAvatarSender : MonoBehaviour
{
    [Header("Local Meta Avatar")]
    public OvrAvatarEntity avatar;
    [Tooltip("Root to synchronize. Defaults to the avatar GameObject, not the headset joint.")]
    public Transform rootTransform;
    [Tooltip("Optional shared-space origin. Use a stationary, unit-scale transform; null means Unity world space.")]
    public Transform referenceSpace;

    [Header("Relay")]
    public string serverAddress = "127.0.0.1";
    public int serverPort = 5060;
    public string channel = "quest-user";
    [Range(1, 60)] public int sendRate = 30;
    public OvrAvatarEntity.StreamLOD streamLOD = OvrAvatarEntity.StreamLOD.High;

    [Header("Optional in-scene test (bypasses TCP)")]
    public MetaAvatarReceiver localTestReceiver;

    [Header("Runtime status")]
    [SerializeField] private string connectionStatus;
    [SerializeField] private int packetsQueued;
    [SerializeField] private int lastPacketBytes;

    private MetaAvatarRelayConnection connection;
    private float nextSend;
    private uint sequence;
    private bool errorReported;

    private void Reset() { avatar = GetComponent<OvrAvatarEntity>(); }

    private void OnEnable()
    {
        if (avatar == null) avatar = GetComponent<OvrAvatarEntity>();
        if (avatar == null || !avatar.IsLocal)
        {
            Debug.LogError("[MetaAvatarSender] Assign a LOCAL OvrAvatarEntity (Is Local on).", this);
            enabled = false;
            return;
        }
        nextSend = 0;
        errorReported = false;
        if (localTestReceiver == null)
        {
            try { connection = new MetaAvatarRelayConnection(serverAddress, serverPort, channel, true); }
            catch (Exception ex)
            {
                Debug.LogError("[MetaAvatarSender] " + ex.Message, this);
                enabled = false;
            }
        }
    }

    private void LateUpdate()
    {
        connectionStatus = localTestReceiver != null ? "In-scene loopback" : connection?.Status ?? "Stopped";
        if (!avatar.HasJoints)
        {
            connectionStatus += " / waiting for avatar joints";
            return;
        }
        if (localTestReceiver == null && (connection == null || !connection.Connected)) return;
        if (Time.unscaledTime < nextSend) return;
        nextSend = Time.unscaledTime + 1f / Mathf.Clamp(sendRate, 1, 60);
        try
        {
            // Main thread only. The simple byte[] API makes ownership explicit for the
            // background sender; never give it a reused AutoBuffer without copying it.
            byte[] pose = avatar.RecordStreamData(streamLOD);
            if (pose == null || pose.Length == 0) return;
            Transform root = rootTransform != null ? rootTransform : avatar.transform;
            Vector3 position = root.position;
            Quaternion rotation = root.rotation;
            if (referenceSpace != null)
            {
                position = referenceSpace.InverseTransformPoint(position);
                rotation = Quaternion.Inverse(referenceSpace.rotation) * rotation;
            }
            float[] values = { position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w };
            byte[] packet = MetaAvatarNetworkProtocol.Encode(sequence++, Time.realtimeSinceStartupAsDouble, values, pose);
            if (localTestReceiver != null) localTestReceiver.QueueLocalTestPacket(packet);
            else connection.Publish(packet);
            packetsQueued++;
            lastPacketBytes = packet.Length;
            errorReported = false;
        }
        catch (Exception ex)
        {
            if (!errorReported) Debug.LogError("[MetaAvatarSender] " + ex.Message, this);
            errorReported = true;
        }
    }

    private void OnDisable()
    {
        connection?.Dispose();
        connection = null;
        connectionStatus = "Stopped";
    }
}
