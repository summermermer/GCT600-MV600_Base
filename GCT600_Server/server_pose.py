import cv2
import mediapipe as mp
import socket
import threading
import json
import time
import numpy as np
from flask import Flask, Response

from mediapipe.tasks import python
from mediapipe.tasks.python import vision

#---------------------------
from depth_module import DepthConfig, DepthState, build_pose_payload

depth_state = DepthState(
    DepthConfig(
        smoothing_alpha=0.35,
        pose_invert_world_z=False,
        face_global_scale=0.1,
        face_invert_tz=False,
        clamp_min=-20.0,
        clamp_max=20.0,
    )
)
#---------------------------

# Configuration
SOCKET_HOST = '0.0.0.0'
SOCKET_PORT = 5050
WEB_PORT = 5000
CAMERA_INDEX = 0
DEBUG_MODE = True
MODEL_PATH = 'models/pose_landmarker_heavy.task'
FACE_MODEL_PATH = 'models/face_landmarker.task'

# Global variables to share data between threads
current_frame = None
current_landmarks_result = None
current_face_result = None
lock = threading.Lock()

# Face detection thread shared state
latest_rgb_frame = None
latest_face_result = None
face_lock = threading.Lock()

# Initialize Flask
app = Flask(__name__)

def face_detect_thread(face_detector):
    """Runs face detection in a separate thread using the latest RGB frame."""
    global latest_face_result
    while True:
        frame = None
        with face_lock:
            if latest_rgb_frame is not None:
                frame = latest_rgb_frame

        if frame is not None:
            mp_image = mp.Image(image_format=mp.ImageFormat.SRGB, data=frame)
            result = face_detector.detect(mp_image)
            if result and getattr(result, 'face_landmarks', None):
                latest_face_result = result
        else:
            time.sleep(0.01)

def draw_landmarks_on_image(rgb_image, detection_result):
    annotated_image = np.copy(rgb_image)
    height, width, _ = annotated_image.shape

    if detection_result.pose_landmarks:
        for pose_landmarks in detection_result.pose_landmarks:
            for lm in pose_landmarks:
                x = int(lm.x * width)
                y = int(lm.y * height)
                cv2.circle(annotated_image, (x, y), 3, (0, 255, 0), -1)

    return annotated_image

def socket_server_thread():
    """Handles the socket connection to Unity."""
    server_socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        server_socket.bind((SOCKET_HOST, SOCKET_PORT))
        server_socket.listen(1)
        print(f"[Socket] Listening on {SOCKET_HOST}:{SOCKET_PORT}")

        while True:
            client_socket, addr = server_socket.accept()
            print(f"[Socket] Connected by {addr}")
            try:
                while True:
                    global current_landmarks_result, current_face_result
                    data_to_send = None

                    with lock:
                        if current_landmarks_result and current_landmarks_result.pose_landmarks:
                            pose_payload = build_pose_payload(
                                current_landmarks_result, depth_state,
                                pose_index=0,
                                face_result=current_face_result,
                            )
                            if pose_payload is not None:
                                ## DEBUG: print depth info
                                #d = pose_payload.get('depth', {})
                                #plz = d.get('per_landmark_z', [])
                                #raw_tz = "N/A"
                                #if current_face_result is not None:
                                #    mats = getattr(current_face_result, 'facial_transformation_matrixes', None)
                                #    if mats and len(mats) > 0:
                                #        M = np.array(mats[0]).reshape(4,4)
                                #        raw_tz = f"{float(M[2,3]):.2f}"
                                #if plz:
                                #    print(f"[Depth] mode={d.get('mode')} raw_tz={raw_tz} global_z={d.get('global_z'):.4f} "
                                #          f"per_z min={min(plz):.4f} max={max(plz):.4f} spread={max(plz)-min(plz):.4f}")
                                data_to_send = json.dumps(pose_payload)
                    
                    if data_to_send:
                        # Send data followed by a newline as a delimiter
                        client_socket.sendall((data_to_send + "\n").encode('utf-8'))
                    
                    # Sleep briefly to match typical frame rate
                    time.sleep(0.033) 
            except (ConnectionResetError, BrokenPipeError):
                print(f"[Socket] Disconnected from {addr}")
            finally:
                client_socket.close()

    except Exception as e:
        print(f"[Socket] Server Error: {e}")
    finally:
        server_socket.close()

def generate_frames():
    """Generator function for the Flask video stream."""
    while True:
        with lock:
            if current_frame is None:
                time.sleep(0.01)
                continue
            
            # Encode the frame in JPEG format
            ret, buffer = cv2.imencode('.jpg', current_frame)
            frame_bytes = buffer.tobytes()

        # Yield the output frame in the byte format
        yield (b'--frame\r\n'
               b'Content-Type: image/jpeg\r\n\r\n' + frame_bytes + b'\r\n')

@app.route('/video_feed')
def video_feed():
    return Response(generate_frames(), mimetype='multipart/x-mixed-replace; boundary=frame')

@app.route('/snapshot')
def snapshot():
    with lock:
        if current_frame is None:
            return "No frame", 503
        ret, buffer = cv2.imencode('.jpg', current_frame)
        return Response(buffer.tobytes(), mimetype='image/jpeg')

@app.route('/')
def index():
    return "<h1>MediaPipe Pose Server</h1><p><a href='/video_feed'>View Stream</a></p>"

def main():
    global current_frame, current_landmarks_result, current_face_result

    # Start Socket Server thread
    t_socket = threading.Thread(target=socket_server_thread, daemon=True)
    t_socket.start()

    # Start Flask thread
    t_flask = threading.Thread(target=lambda: app.run(host='0.0.0.0', port=WEB_PORT, debug=False, use_reloader=False), daemon=True)
    t_flask.start()
    print(f"[Web] Server running on http://localhost:{WEB_PORT}")

    # Set up MediaPipe Pose Landmarker
    base_options = python.BaseOptions(model_asset_path=MODEL_PATH)
    options = vision.PoseLandmarkerOptions(
        base_options=base_options,
        output_segmentation_masks=False)
    pose_detector = vision.PoseLandmarker.create_from_options(options)

    # Set up MediaPipe Face Landmarker (for absolute depth via transformation matrix)
    face_base_options = python.BaseOptions(model_asset_path=FACE_MODEL_PATH)
    face_options = vision.FaceLandmarkerOptions(
        base_options=face_base_options,
        output_face_blendshapes=False,
        output_facial_transformation_matrixes=True,
        num_faces=1)
    face_detector = vision.FaceLandmarker.create_from_options(face_options)

    # Video Capture
    cap = cv2.VideoCapture(CAMERA_INDEX)

    if not cap.isOpened():
        print("Error: Could not open camera.")
        return

    # Start face detection thread
    t_face = threading.Thread(target=face_detect_thread, args=(face_detector,), daemon=True)
    t_face.start()

    print("Starting Main Loop...")
    while cap.isOpened():
        success, image = cap.read()
        if not success:
            print("Ignoring empty camera frame.")
            continue

        # MediaPipe works with RGB
        image = cv2.cvtColor(image, cv2.COLOR_BGR2RGB)
        mp_image = mp.Image(image_format=mp.ImageFormat.SRGB, data=image)

        # Share frame for face detection thread
        with face_lock:
            global latest_rgb_frame
            latest_rgb_frame = image.copy()

        # Detect pose landmarks
        pose_result = pose_detector.detect(mp_image)

        # Create annotated image
        annotated_image = draw_landmarks_on_image(image, pose_result)

        # Convert back to BGR for OpenCV display and Streaming
        annotated_image_bgr = cv2.cvtColor(annotated_image, cv2.COLOR_RGB2BGR)

        with lock:
            current_landmarks_result = pose_result
            current_face_result = latest_face_result
            current_frame = annotated_image_bgr

        if DEBUG_MODE:
            cv2.imshow('MediaPipe Pose - Server', annotated_image_bgr)
            if cv2.waitKey(5) & 0xFF == 27:
                break

    cap.release()
    cv2.destroyAllWindows()

if __name__ == "__main__":
    main()
