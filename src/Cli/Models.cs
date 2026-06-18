namespace Anonymizer.Cli;

internal static class Models
{
    internal static class PII
    {
        public const string Name = "yalen-ai/distilbert_pii_ner_yalen";
    }

    internal static class Face
    {
        public static readonly Uri RemoteUri = new("https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB/raw/master/models/onnx/version-RFB-320.onnx");
    }
}
