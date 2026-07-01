using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using OpenCvSharp;

namespace Anonymizer.Extractor.PII;

public sealed class FaceDetector : IDisposable
{
    public FaceDetector(string modelPath, float confThreshold = 0.7f)
    {
        SessionOptions options = new()
        {
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };

        _session = new(modelPath, options);
        _inputName = _session.InputMetadata.Keys.First();
        _confThreshold = confThreshold;
    }

    public void Dispose() =>
        _session?.Dispose();

    public bool DetectFaces(ReadOnlySpan<byte> imageBytes)
    {
        if (imageBytes.Length is 0)
            return false;

        try
        {
            using var image = Cv2.ImDecode(imageBytes, ImreadModes.Color);

            if (image.Empty())
                return false;

            // Preprocessing
            var inputTensor = Preprocess(image);

            // Preparing inputs for ONNX
            List<NamedOnnxValue> inputs = [
                NamedOnnxValue.CreateFromTensor(_inputName, inputTensor)
            ];

            using var results = _session.Run(inputs);

            // Retrieving the scores. We assume the format is [1, num_boxes, 2]
            var output = results.First().AsTensor<float>();
            var dimensions = output.Dimensions;
            int numBoxes = dimensions[1];

            // Column 1 contains the confidence score for each box
            for (int i = 0; i < numBoxes; i++)
            {
                float score = output[0, i, 1];
                if (score > _confThreshold)
                    return true; // At least one face has been detected
            }

            return false;
        }
        catch (Exception e)
        {
            Console.WriteLine($"Error during face detection: {e.Message}");
            return false;
        }
    }

    private static DenseTensor<float> Preprocess(Mat image)
    {
        // 1. Resizing (UltraFace RFB-320 wants 320x240)
        using var resized = new Mat();
        Cv2.Resize(image, resized, new Size(320, 240));

        // 2. Convert BGR to RGB
        using var rgbImage = new Mat();
        Cv2.CvtColor(resized, rgbImage, ColorConversionCodes.BGR2RGB);

        // 3. Create tensor in CHW format [batch, channels, height, width]
        DenseTensor<float> tensor = new ([1, 3, 240, 320]);

        // 4. Normalization: (image_rgb - 127.0) / 128.0 and fill the tensor
        var indexer = rgbImage.GetUnsafeGenericIndexer<Vec3b>();
        for (int y = 0; y < 240; y++)
        {
            for (int x = 0; x < 320; x++)
            {
                Vec3b color = indexer[y, x];
                tensor[0, 0, y, x] = (color.Item0 - 127.0f) / 128.0f; // Canal 0 : Rouge (R)
                tensor[0, 1, y, x] = (color.Item1 - 127.0f) / 128.0f; // Canal 1 : Vert (G)
                tensor[0, 2, y, x] = (color.Item2 - 127.0f) / 128.0f; // Canal 2 : Bleu (B)
            }
        }

        return tensor;
    }

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly float _confThreshold;
}
