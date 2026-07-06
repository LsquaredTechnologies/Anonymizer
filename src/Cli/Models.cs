namespace Anonymizer;

internal static class Models
{
    public static Uri BaseUri = new("https://github.com/LsquaredTechnologies/Anonymizer/releases/latest/download/");

    internal static class PII
    {
        internal static class Model
        {
            public static readonly Uri RemoteUri = new(BaseUri, "pii_model.zip");
        }
    }

    internal static class Face
    {
        internal static class Model
        {
            public static readonly Uri RemoteUri = new(BaseUri, "face_model.zip");
        }
    }
}
