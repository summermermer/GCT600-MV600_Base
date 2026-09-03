using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public class StreamClient : MonoBehaviour
{
    public enum ClientType { Pose, Hand, Face }

    [Header("Connection Settings")]
    public string ipAddress = "127.0.0.1";
    public int port = 5050;
    public ClientType clientType;
    public bool autoConnect = true;

    [Header("Visualization")]
    public GameObject landmarkPrefab; 
    public float landmarkScale = 0.04f;
    // Size of each landmark GameObject in Unity world space.
    // Increase for better visibility, decrease if landmarks look too large.
    public float depthMultiplier = 10.0f; 
    // Multiplies incoming depth values before applying them to Z.
    // Increase this if forward/backward movement is too subtle.
    // Decrease it if landmarks move too far in depth.
    public Vector3 positionOffset = new Vector3(0, 0, -0.2f); 
    // Global offset applied after XY/Z placement.
    // Useful for moving the whole landmark set slightly forward/backward or sideways.
    public bool usePseudoDepth = true;
    // Enables image-size-based heuristic depth adjustment.
    // Disable this if you want to rely mainly on the depth module output.
    public float depthScale = 2.0f;
    // Strength of pseudo-depth when usePseudoDepth is enabled.
    // Increase to exaggerate image-size-based distance changes.
    // Decrease if pseudo-depth interferes with the new depth module.
    public bool invertDepth = false;
    // Inverts the pseudo-depth direction only.
    // Turn this on if pseudo-depth moves objects in the wrong direction.
    public bool mirrorX = true; 
    // Mirrors X coordinates horizontally.
    // Keep this on for webcam-style mirror behavior.
    // Turn it off if you want true camera-space left/right behavior.
    public Transform visualizationRoot; 
    public QuadDisplay quadDisplay;

    //-----------------------------
    [Header("Depth-based XY Compensation")] 
    public bool useXYDepthCompensation = true;
    // Enables depth-based XY scale compensation.
    // Turn this on to reduce the apparent size change when the user moves closer/farther.
    public float xyDepthCompensationStrength = 1.0f;
    // Controls how strongly XY spread is reduced as depth changes.
    // Increase this if landmarks still grow too much when moving closer.
    // Decrease this if the shape becomes too compressed.

    public bool useAbsGlobalDepthForCompensation = true;
    // Uses the absolute value of global depth when computing XY compensation.
    // Usually safer if depth sign may vary.
    // Turn this off only if you explicitly want sign-dependent XY scaling behavior.

    public float xyCompensationMinScale = 0.2f;
    // Minimum allowed XY compensation scale.
    // Prevents landmarks from collapsing too much toward the center.
    public float xyCompensationMaxScale = 2.0f;
    // Maximum allowed XY compensation scale.
    // Usually keep this near 1.0 if the goal is only to shrink close-up spread.
    //-----------------------------

    private TcpClient socket;
    private NetworkStream stream;
    private Thread receiveThread;
    private bool isRunning = false;
    private bool dataReceived = false;
    private string latestJsonData = "";
    private List<GameObject> spawnedLandmarks = new List<GameObject>();
    public List<Landmark> activeLandmarks;
    public PoseData latestPoseData;

    void Start()
    {
        if (autoConnect) Connect();
    }

    void OnDestroy()
    {
        Disconnect();
    }

    public void Connect()
    {
        if (isRunning) return;
        try
        {
            socket = new TcpClient();
            socket.Connect(ipAddress, port);
            stream = socket.GetStream();
            isRunning = true;
            receiveThread = new Thread(ReceiveData);
            receiveThread.IsBackground = true;
            receiveThread.Start();
            Debug.Log($"[{clientType}] Connected to {ipAddress}:{port}");
        }
        catch (Exception e) { Debug.LogError($"[{clientType}] Connection Error: {e.Message}"); }
    }

    public void Disconnect()
    {
        isRunning = false;
        if (receiveThread != null && receiveThread.IsAlive) receiveThread.Join(100);
        if (stream != null) stream.Close();
        if (socket != null) socket.Close();
    }

    private void ReceiveData()
    {
        byte[] buffer = new byte[16384]; 
        StringBuilder jsonBuilder = new StringBuilder();

        while (isRunning)
        {
            try
            {
                if (stream.DataAvailable)
                {
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    string chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    jsonBuilder.Append(chunk);

                    string currentStr = jsonBuilder.ToString();
                    int newlineIndex;
                    while ((newlineIndex = currentStr.IndexOf('\n')) != -1)
                    {
                        string jsonLine = currentStr.Substring(0, newlineIndex);
                        latestJsonData = jsonLine;
                        dataReceived = true;
                        currentStr = currentStr.Substring(newlineIndex + 1);
                    }
                    jsonBuilder.Clear();
                    jsonBuilder.Append(currentStr);
                }
                else
                    Thread.Sleep(100);
            }
            catch (Exception) { isRunning = false; }
        }
    }

    void Update()
    {
        if (dataReceived)
        {
            dataReceived = false;
            ProcessData(latestJsonData);
        }
    }

    private void ProcessData(string json)
    {
        try
        {
            switch (clientType)
            {
                case ClientType.Pose:
                    PoseData pose = JsonUtility.FromJson<PoseData>(json);
                    if (pose != null)
                    {
                        latestPoseData = pose;
                        //--------------------------
                        // Reverting to Hybrid/Normalized Visuals
                        //UpdateHybridVisuals(pose.landmarks, pose.world_landmarks);
                        UpdateHybridVisuals(pose.landmarks, pose.world_landmarks, pose.depth);
                        //--------------------------

                    }
                    break;

                //--------------------------
                //case ClientType.Hand:
                    //HandData handData = JsonUtility.FromJson<HandData>(json);
                    //if (handData != null && handData.hands != null)
                    //{
                        //List<Landmark> allNorm = new List<Landmark>();
                        //List<Landmark> allWorld = new List<Landmark>();
                        
                        //foreach(var hand in handData.hands)
                        //{
                            //allNorm.AddRange(hand.landmarks);
                             //// If world exists, add it, otherwise fill with nulls to stay consistent index-wise
                            //if (hand.world_landmarks != null && hand.world_landmarks.Count > 0)
                                //allWorld.AddRange(hand.world_landmarks);
                            //else
                                //// fill dummy to keep counts synced if mixing (shouldn't happen if server consistent)
                                //for(int i=0; i<hand.landmarks.Count; i++) allWorld.Add(null);
                        //}
                        //UpdateHybridVisuals(allNorm, allWorld); 
                    //}
                    //break;
                case ClientType.Hand:
                    HandData handData = JsonUtility.FromJson<HandData>(json);
                    if (handData != null && handData.hands != null)
                    {
                        List<Landmark> allNorm = new List<Landmark>();
                        List<Landmark> allWorld = new List<Landmark>();
                        List<float> allDepthZ = new List<float>();

                        float globalZSum = 0f;
                        int globalZCount = 0;

                        foreach (var hand in handData.hands)
                        {
                            if (hand.landmarks != null)
                                allNorm.AddRange(hand.landmarks);

                            if (hand.world_landmarks != null && hand.world_landmarks.Count > 0)
                            {
                                allWorld.AddRange(hand.world_landmarks);
                            }
                            else if (hand.landmarks != null)
                            {
                                for (int i = 0; i < hand.landmarks.Count; i++) allWorld.Add(null);
                            }

                            if (hand.depth != null)
                            {
                                globalZSum += hand.depth.global_z;
                                globalZCount++;

                                if (hand.depth.per_landmark_z != null && hand.depth.per_landmark_z.Count > 0)
                                {
                                    allDepthZ.AddRange(hand.depth.per_landmark_z);
                                }
                                else if (hand.landmarks != null)
                                {
                                    for (int i = 0; i < hand.landmarks.Count; i++) allDepthZ.Add(0f);
                                }
                            }
                            else if (hand.landmarks != null)
                            {
                                for (int i = 0; i < hand.landmarks.Count; i++) allDepthZ.Add(0f);
                            }
                        }

                        DepthInfo mergedDepth = new DepthInfo();
                        mergedDepth.mode = "hand_world";
                        mergedDepth.global_z = (globalZCount > 0) ? (globalZSum / globalZCount) : 0f;
                        mergedDepth.per_landmark_z = allDepthZ;

                        UpdateHybridVisuals(allNorm, allWorld, mergedDepth);
                    }
                    break;
                //--------------------------

                //--------------------------
                //case ClientType.Face:
                    //FaceData faceData = JsonUtility.FromJson<FaceData>(json);
                    //if (faceData != null && faceData.faces != null)
                    //{
                         //List<Landmark> allFaces = new List<Landmark>();
                        //foreach(var face in faceData.faces) allFaces.AddRange(face.landmarks);
                        //// Face currently no world landmarks support in this script
                        //UpdateHybridVisuals(allFaces, null); 
                    //}
                    //break;
                case ClientType.Face:
                    FaceData faceData = JsonUtility.FromJson<FaceData>(json);
                    if (faceData != null && faceData.faces != null)
                    {
                        List<Landmark> allFaces = new List<Landmark>();
                        List<float> allDepthZ = new List<float>();

                        float globalZSum = 0f;
                        int globalZCount = 0;

                        foreach (var face in faceData.faces)
                        {
                            if (face.landmarks != null)
                                allFaces.AddRange(face.landmarks);

                            if (face.depth != null)
                            {
                                globalZSum += face.depth.global_z;
                                globalZCount++;

                                if (face.depth.per_landmark_z != null && face.depth.per_landmark_z.Count > 0)
                                {
                                    allDepthZ.AddRange(face.depth.per_landmark_z);
                                }
                                else if (face.landmarks != null)
                                {
                                    for (int i = 0; i < face.landmarks.Count; i++) allDepthZ.Add(0f);
                                }
                            }
                            else if (face.landmarks != null)
                            {
                                for (int i = 0; i < face.landmarks.Count; i++) allDepthZ.Add(0f);
                            }
                        }

                        DepthInfo mergedDepth = new DepthInfo();
                        mergedDepth.mode = "face_transform_plus_local";
                        mergedDepth.global_z = (globalZCount > 0) ? (globalZSum / globalZCount) : 0f;
                        mergedDepth.per_landmark_z = allDepthZ;

                        UpdateHybridVisuals(allFaces, null, mergedDepth);
                    }
                    break;
                //--------------------------
            }
        }
        catch (Exception e) { Debug.LogError($"JSON Parse Error: {e.Message}"); }
    }

    //--------------------------------------
    //private void UpdateHybridVisuals(List<Landmark> normalized, List<Landmark> world)
    private void UpdateHybridVisuals(List<Landmark> normalized, List<Landmark> world, DepthInfo depthInfo = null)
    //--------------------------------------
    {

        if (normalized == null || normalized.Count == 0)
        {
            Debug.LogWarning($"[{clientType}] normalized landmarks are empty");
            for (int i = 0; i < spawnedLandmarks.Count; i++) spawnedLandmarks[i].SetActive(false);
            return;
        }


        // Check availability
        bool useWorld = (world != null && world.Count == normalized.Count && world.Count > 0 && world[0] != null);
        
        int count = normalized.Count;
        while (spawnedLandmarks.Count < count)
        {
            GameObject obj = Instantiate(landmarkPrefab, this.transform);
            spawnedLandmarks.Add(obj);
        }
        for (int i = count; i < spawnedLandmarks.Count; i++) spawnedLandmarks[i].SetActive(false);

        // Calculate Pseudo-Depth based on Bounding Box Size
        float depthAdjustment = 0;
        if (usePseudoDepth && count > 0)
        {
            float minX = 1f, maxX = 0f, minY = 1f, maxY = 0f;
            for(int i=0; i<count; i++)
            {
                 Landmark lm = normalized[i];
                 if (lm.x < minX) minX = lm.x;
                 if (lm.x > maxX) maxX = lm.x;
                 if (lm.y < minY) minY = lm.y;
                 if (lm.y > maxY) maxY = lm.y;
            }
            float width = maxX - minX;
            float height = maxY - minY;
            // Use maximum dimension to estimate scale
            float size = Mathf.Max(width, height);
            
            // Heuristic: Smaller size = Further away (Positive Z in Quad local space if Z- is front)
            // If size is 1.0 (Close), adj is 0.
            // If size is 0.0 (Far), adj is depthScale.
            if(size > 0.001f) 
            {
                float val = (1.0f - size) * depthScale;
                depthAdjustment = invertDepth ? -val : val;
            }
        }

        //-------------------------------------
        float centerX = 0f;
        float centerY = 0f;

        for (int i = 0; i < count; i++)
        {
            centerX += normalized[i].x;
            centerY += normalized[i].y;
        }

        if (count > 0)
        {
            centerX /= count;
            centerY /= count;
        }
        else
        {
            centerX = 0.5f;
            centerY = 0.5f;
        }
        float globalDepth = 0f;
        if (depthInfo != null)
            globalDepth = depthInfo.global_z;

        float depthForScale = useAbsGlobalDepthForCompensation ? Mathf.Abs(globalDepth) : globalDepth;

        // depth가 커질수록 XY 분포를 줄이는 방향
        float xyScaleCompensation = 1.0f / (1.0f + depthForScale * xyDepthCompensationStrength);
        xyScaleCompensation = Mathf.Clamp(xyScaleCompensation, xyCompensationMinScale, xyCompensationMaxScale);

        if (!useXYDepthCompensation)
            xyScaleCompensation = 1.0f;
        //-------------------------------------

        for (int i = 0; i < count; i++)
        {
            GameObject obj = spawnedLandmarks[i];
            obj.SetActive(true);
            obj.transform.localScale = Vector3.one * landmarkScale;

            Landmark lmNorm = normalized[i];
            
            if (quadDisplay == null) continue;

            //----------------------------------------
            // 1. Get Base Position on Quad surface from Normalized XY
            // Flip Y for Unity (Top is +0.5)
            // Mirror X if requested

            //float localX = mirrorX ? -(lmNorm.x - 0.5f) : (lmNorm.x - 0.5f);
            //float localY = -(lmNorm.y - 0.5f);

            float centeredX = lmNorm.x - centerX;
            float centeredY = lmNorm.y - centerY;

            // depth 기반으로 landmark 분포를 축소
            centeredX *= xyScaleCompensation;
            centeredY *= xyScaleCompensation;

            // 다시 중심점 기준으로 복원
            float compensatedX = centerX + centeredX;
            float compensatedY = centerY + centeredY;

            // Unity quad local coordinates
            float localX = mirrorX ? -(compensatedX - 0.5f) : (compensatedX - 0.5f);
            float localY = -(compensatedY - 0.5f);
            //----------------------------------------
            
            // 2. Depth
            // If we have World Data, use the Z from World Data (scaled).
            // If not, use Normalized Z (which is relative depth).
            
            //--------------------------------------------------
            //float zDepth = 0;
            //if (useWorld)
            //{
                //// World Z is in meters. MP Negative Z is "Towards Camera".
                //// Unity Quad Local Z- is "Front/Towards Camera".
                //// So MP Z should map directly to Local Z (proportional).
                //zDepth = world[i].z * depthMultiplier; 
            //}
            //else
            //{
                //// Normalized Z is also roughly scale-relative.
                 //zDepth = lmNorm.z * 0.5f * depthMultiplier; 
            //}

            float zDepth = 0;

            bool useDepthPacket = (depthInfo != null &&
                                depthInfo.per_landmark_z != null &&
                                i < depthInfo.per_landmark_z.Count);

            if (useDepthPacket)
            {
                zDepth = depthInfo.per_landmark_z[i] * depthMultiplier;
            }
            else if (useWorld && world[i] != null)
            {
                zDepth = world[i].z * depthMultiplier;
            }
            else
            {
                zDepth = lmNorm.z * 0.5f * depthMultiplier;
            }
            //--------------------------------------------------
            
            // Combine: Offset + Relative Depth + Absolute Pseudo-Depth
            // Quad Back is +Z, Front is -Z. 
            // We want 'Far' to go towards +Z (Into the wall).
            Vector3 basePos = new Vector3(localX, localY, zDepth + depthAdjustment); 
            Vector3 localPos = basePos + positionOffset; 

            // Transform local position relative to the Quad into World Space
            if (visualizationRoot != null)
            {
                obj.transform.position = visualizationRoot.TransformPoint(localPos);
            }
            else
            {
                obj.transform.position = localPos;
            }
            
            // Store the calculated world position into the landmark data
            lmNorm.worldPosition = obj.transform.position;
        }
        
        // Expose the list for other scripts to access
        activeLandmarks = normalized;
    }
}
