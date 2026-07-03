// src/face_detector.rs
use ort::session::Session;
use ort::value::Value;
use std::path::Path;

pub struct FaceDetector {
    session: Session,
    input_name: String,
    conf_threshold: f32,
}

impl FaceDetector {
    pub fn new<P: AsRef<Path>>(
        model_path: P,
        conf_threshold: f32,
    ) -> Result<Self, Box<dyn std::error::Error>> {
        let session = Session::builder()?.commit_from_file(model_path)?;

        let input_name = session.inputs()[0].name().to_string();

        Ok(Self {
            session,
            input_name,
            conf_threshold,
        })
    }

    pub fn detect_faces(&mut self, image_bytes: &[u8]) -> bool {
        if image_bytes.is_empty() {
            return false;
        }

        let input_tensor = match self.preprocess(image_bytes) {
            Ok(tensor) => tensor,
            Err(e) => {
                eprintln!("[FaceDetector] Error during preprocessing: {e}");
                return false;
            }
        };

        let outputs = match self.session.run(ort::inputs![
            &self.input_name => input_tensor
        ]) {
            Ok(out) => out,
            Err(e) => {
                eprintln!("[FaceDetector] Error during ONNX inference: {e}");
                return false;
            }
        };

        let (shape, data) = match outputs[0].try_extract_tensor::<f32>() {
            Ok(res) => res,
            Err(_) => return false,
        };

        if shape.len() < 3 {
            return false;
        }

        let num_boxes = shape[1] as usize;
        for i in 0..num_boxes {
            let score_idx = (i * 2) + 1;
            if score_idx < data.len() {
                let score = data[score_idx];
                if score > self.conf_threshold {
                    return true;
                }
            }
        }

        false
    }

    fn preprocess(&self, image_bytes: &[u8]) -> Result<Value, Box<dyn std::error::Error>> {
        let img = image::load_from_memory(image_bytes)?;

        let resized = img.resize_exact(320, 240, image::imageops::FilterType::Triangle);
        let rgb_image = resized.to_rgb8();
        let mut float_data = vec![0.0f32; 1 * 3 * 240 * 320];
        for y in 0..240 {
            for x in 0..320 {
                let pixel = rgb_image.get_pixel(x, y);

                let r_idx = 0 * (240 * 320) + (y as usize) * 320 + (x as usize);
                let g_idx = 1 * (240 * 320) + (y as usize) * 320 + (x as usize);
                let b_idx = 2 * (240 * 320) + (y as usize) * 320 + (x as usize);

                float_data[r_idx] = (pixel[0] as f32 - 127.0) / 128.0;
                float_data[g_idx] = (pixel[1] as f32 - 127.0) / 128.0;
                float_data[b_idx] = (pixel[2] as f32 - 127.0) / 128.0;
            }
        }

        let tensor = Value::from_array(([1, 3, 240, 320], float_data))?;
        Ok(tensor.into())
    }
}
