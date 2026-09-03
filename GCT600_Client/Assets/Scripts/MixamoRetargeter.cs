using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Drives a Humanoid avatar's bone rotations using MediaPipe Pose world_landmarks.
/// Compares each bone's direction vector (parent → child landmark) against its
/// T-Pose rest direction to compute and apply a delta rotation.
///
/// [Usage]
/// 1. Import a Mixamo FBX with Rig set to Humanoid and place it in the scene
/// 2. Add this component to the character GameObject
/// 3. Assign a StreamManager to the streamManager field in the Inspector
/// 4. (Optional) Remove or leave the AnimatorController empty
///    - Works without an AnimatorController
///    - If one exists, LateUpdate will override the Animator output
/// 5. Press Play — the character will follow the incoming pose data
/// </summary>
[RequireComponent(typeof(Animator))]
public class MixamoRetargeter : MonoBehaviour
{
    [Header("Data Source")]
    [Tooltip("Assign a StreamManager to automatically find a Pose-type StreamClient.")]
    public StreamManager streamManager;

    [HideInInspector]
    public StreamClient poseClient;

    [Header("Retargeting")]
    [Tooltip("Rotation smoothing (0 = instant, 1 = no movement)")]
    [Range(0f, 0.99f)]
    public float smoothing = 0.5f;

    [Tooltip("Mirror X axis (default true for webcam)")]
    public bool mirrorX = true;

    [Header("Root Motion")]
    [Tooltip("Apply hip center position to the character root")]
    public bool applyRootPosition = false;
    public float rootPositionScale = 1.0f;
    public Vector3 rootOffset = Vector3.zero;
    [Tooltip("When enabled, keeps the root Y position at its initial value.")]
    public bool rootIgnoreY = true;

    // -- MediaPipe Pose landmark indices --
    const int MP_NOSE          = 0;
    const int MP_L_SHOULDER    = 11;
    const int MP_R_SHOULDER    = 12;
    const int MP_L_ELBOW       = 13;
    const int MP_R_ELBOW       = 14;
    const int MP_L_WRIST       = 15;
    const int MP_R_WRIST       = 16;
    const int MP_L_HIP         = 23;
    const int MP_R_HIP         = 24;
    const int MP_L_KNEE        = 25;
    const int MP_R_KNEE        = 26;
    const int MP_L_ANKLE       = 27;
    const int MP_R_ANKLE       = 28;

    private Animator anim;

    // Bone mapping struct: MediaPipe landmark pair -> Unity bone
    private struct BoneMapping
    {
        public HumanBodyBones bone;
        public HumanBodyBones childBone; // used for direction calculation
        public int fromIdx;              // parent landmark
        public int toIdx;                // child landmark
    }

    private BoneMapping[] limbMappings;

    // T-Pose rest data
    private Dictionary<HumanBodyBones, Quaternion> restWorldRotation = new Dictionary<HumanBodyBones, Quaternion>();
    private Dictionary<HumanBodyBones, Vector3> restBoneDirection = new Dictionary<HumanBodyBones, Vector3>();

    // Smoothing
    private Dictionary<HumanBodyBones, Quaternion> smoothedRotation = new Dictionary<HumanBodyBones, Quaternion>();
    private Vector3 smoothedRootPos;
    private float initialRootY;

    void Awake()
    {
        anim = GetComponent<Animator>();
        if (!anim.isHuman)
        {
            Debug.LogError("[MixamoRetargeter] Avatar is not Humanoid type.");
            enabled = false;
        }
    }

    void Start()
    {
        StartCoroutine(InitAfterDelay());
    }

    IEnumerator InitAfterDelay()
    {
        // Wait for StreamManager to create its clients
        yield return new WaitForSeconds(3f);

        // Auto-find a Pose-type client from StreamManager
        if (poseClient == null && streamManager != null)
        {
            foreach (var client in streamManager.activeClients)
            {
                if (client.clientType == StreamClient.ClientType.Pose)
                {
                    poseClient = client;
                    break;
                }
            }
            if (poseClient == null)
            {
                Debug.LogWarning("[MixamoRetargeter] No Pose-type client found in StreamManager.");
                yield break;
            }
        }

        Init();
    }

    void Init()
    {
        // Define limb bone mappings
        limbMappings = new BoneMapping[]
        {
            // Left arm
            new BoneMapping { bone = HumanBodyBones.LeftUpperArm,  childBone = HumanBodyBones.LeftLowerArm,  fromIdx = MP_L_SHOULDER, toIdx = MP_L_ELBOW },
            new BoneMapping { bone = HumanBodyBones.LeftLowerArm,  childBone = HumanBodyBones.LeftHand,      fromIdx = MP_L_ELBOW,    toIdx = MP_L_WRIST },
            // Right arm
            new BoneMapping { bone = HumanBodyBones.RightUpperArm, childBone = HumanBodyBones.RightLowerArm, fromIdx = MP_R_SHOULDER, toIdx = MP_R_ELBOW },
            new BoneMapping { bone = HumanBodyBones.RightLowerArm, childBone = HumanBodyBones.RightHand,     fromIdx = MP_R_ELBOW,    toIdx = MP_R_WRIST },
            // Left leg
            new BoneMapping { bone = HumanBodyBones.LeftUpperLeg,  childBone = HumanBodyBones.LeftLowerLeg,  fromIdx = MP_L_HIP,  toIdx = MP_L_KNEE },
            new BoneMapping { bone = HumanBodyBones.LeftLowerLeg,  childBone = HumanBodyBones.LeftFoot,      fromIdx = MP_L_KNEE, toIdx = MP_L_ANKLE },
            // Right leg
            new BoneMapping { bone = HumanBodyBones.RightUpperLeg, childBone = HumanBodyBones.RightLowerLeg, fromIdx = MP_R_HIP,  toIdx = MP_R_KNEE },
            new BoneMapping { bone = HumanBodyBones.RightLowerLeg, childBone = HumanBodyBones.RightFoot,     fromIdx = MP_R_KNEE, toIdx = MP_R_ANKLE },
        };

        // Store each bone's rest rotation and direction from T-Pose
        foreach (var map in limbMappings)
        {
            CaptureRestPose(map.bone, map.childBone);
        }

        // Also store Hips -> Spine direction
        CaptureRestPose(HumanBodyBones.Hips, HumanBodyBones.Spine);

        smoothedRootPos = transform.position;
        initialRootY = transform.position.y;
    }

    void CaptureRestPose(HumanBodyBones bone, HumanBodyBones childBone)
    {
        Transform boneT = anim.GetBoneTransform(bone);
        Transform childT = anim.GetBoneTransform(childBone);
        if (boneT == null || childT == null) return;

        restWorldRotation[bone] = boneT.rotation;
        restBoneDirection[bone] = (childT.position - boneT.position).normalized;
        smoothedRotation[bone] = boneT.rotation;
    }

    void LateUpdate()
    {
        if (poseClient == null || poseClient.latestPoseData == null) return;

        var wl = poseClient.latestPoseData.world_landmarks;
        if (wl == null || wl.Count < 29) return;

        // -- Coordinate conversion --
        // MediaPipe world: X=right, Y=down(+), Z=towards camera(+)
        // Unity:           X=right, Y=up(+),   Z=forward(+)
        // Conversion: flip Y, flip Z, optionally mirror X
        Vector3 MP(int idx)
        {
            float x = mirrorX ? -wl[idx].x : wl[idx].x;
            return new Vector3(x, -wl[idx].y, -wl[idx].z);
        }

        // -- 1. Hips/Spine rotation --
        Vector3 lHip = MP(MP_L_HIP);
        Vector3 rHip = MP(MP_R_HIP);
        Vector3 lShoulder = MP(MP_L_SHOULDER);
        Vector3 rShoulder = MP(MP_R_SHOULDER);

        Vector3 hipCenter = (lHip + rHip) * 0.5f;
        Vector3 shoulderCenter = (lShoulder + rShoulder) * 0.5f;

        Vector3 spineUp = (shoulderCenter - hipCenter).normalized;
        Vector3 shoulderRight = (rShoulder - lShoulder).normalized;
        Vector3 spineForward = Vector3.Cross(shoulderRight, spineUp).normalized;

        if (restBoneDirection.ContainsKey(HumanBodyBones.Hips) && spineUp.sqrMagnitude > 0.001f)
        {
            Vector3 restDir = restBoneDirection[HumanBodyBones.Hips];
            Quaternion delta = Quaternion.FromToRotation(restDir, spineUp);
            Quaternion targetRot = delta * restWorldRotation[HumanBodyBones.Hips];

            // Apply twist correction using spine forward direction
            if (spineForward.sqrMagnitude > 0.001f)
            {
                // Align the hips forward direction to spineForward
                Vector3 currentForward = targetRot * Vector3.forward;
                // Project both onto the plane perpendicular to spineUp
                Vector3 projectedCurrent = Vector3.ProjectOnPlane(currentForward, spineUp).normalized;
                Vector3 projectedTarget = Vector3.ProjectOnPlane(spineForward, spineUp).normalized;
                if (projectedCurrent.sqrMagnitude > 0.001f && projectedTarget.sqrMagnitude > 0.001f)
                {
                    Quaternion twistDelta = Quaternion.FromToRotation(projectedCurrent, projectedTarget);
                    targetRot = twistDelta * targetRot;
                }
            }

            ApplySmoothedRotation(HumanBodyBones.Hips, targetRot);
        }

        // -- 2. Limb rotations --
        foreach (var map in limbMappings)
        {
            if (!restBoneDirection.ContainsKey(map.bone)) continue;

            Vector3 from = MP(map.fromIdx);
            Vector3 to = MP(map.toIdx);
            Vector3 targetDir = (to - from).normalized;

            if (targetDir.sqrMagnitude < 0.001f) continue;

            Vector3 restDir = restBoneDirection[map.bone];
            Quaternion delta = Quaternion.FromToRotation(restDir, targetDir);
            Quaternion targetRot = delta * restWorldRotation[map.bone];

            ApplySmoothedRotation(map.bone, targetRot);
        }

        // -- 3. Root position: follow the visualized hip node positions --
        if (applyRootPosition)
        {
            var lms = poseClient.activeLandmarks;
            if (lms != null && lms.Count > MP_R_HIP)
            {
                Vector3 hipWorldPos = (lms[MP_L_HIP].worldPosition + lms[MP_R_HIP].worldPosition) * 0.5f;
                Vector3 targetPos = hipWorldPos + rootOffset;
                if (rootIgnoreY)
                    targetPos.y = initialRootY;
                smoothedRootPos = Vector3.Lerp(smoothedRootPos, targetPos, 1f - smoothing);
                transform.position = smoothedRootPos;
            }
        }
    }

    void ApplySmoothedRotation(HumanBodyBones bone, Quaternion targetRot)
    {
        Transform t = anim.GetBoneTransform(bone);
        if (t == null) return;

        smoothedRotation[bone] = Quaternion.Slerp(smoothedRotation[bone], targetRot, 1f - smoothing);
        t.rotation = smoothedRotation[bone];
    }
}
