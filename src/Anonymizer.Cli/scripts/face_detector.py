import cv2
import numpy as np
import onnxruntime as ort
import sys


class FaceDetector:
    def __init__(self, model_path, conf_threshold=0.7):
        self.conf_threshold = conf_threshold
        try:
            opts = ort.SessionOptions()
            opts.log_severity_level = 3  # 0=VERBOSE, 1=INFO, 2=WARNING, 3=ERROR
            self.ort_session = ort.InferenceSession(model_path, sess_options=opts)
            self.input_name = self.ort_session.get_inputs()[0].name
        except Exception as e:
            print(f"Error loading ONNX model: {e}")
            sys.exit(1)

    def _preprocess(self, image):
        # UltraFace RFB-320 expects 320x240 input normalized to [-1, 1]
        image = cv2.resize(image, (320, 240))
        image_rgb = cv2.cvtColor(image, cv2.COLOR_BGR2RGB)
        image_norm = (image_rgb - 127.0) / 128.0
        image_chw = np.transpose(image_norm, (2, 0, 1))
        return np.expand_dims(image_chw, axis=0).astype(np.float32)

    def detect_faces(self, image_bytes):
        """Return True if at least one face is detected above the confidence threshold."""
        if not image_bytes:
            return False

        try:
            nparr = np.frombuffer(image_bytes, np.uint8)
            image = cv2.imdecode(nparr, cv2.IMREAD_COLOR)

            if image is None:
                return False

            input_tensor = self._preprocess(image)
            outputs = self.ort_session.run(None, {self.input_name: input_tensor})

            # Column 1 holds the face confidence score
            confidences = outputs[0]
            return any(score > self.conf_threshold for score in confidences[0][:, 1])

        except Exception as e:
            print(f"Error during face detection: {e}")
            return False
