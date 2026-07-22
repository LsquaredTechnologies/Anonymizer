using System.Text.Json.Serialization;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using Tokenizers.HuggingFace.Tokenizer;

using Encoding = System.Text.Encoding;

namespace Anonymizer.Extractor.PII;

public sealed class OnnxPiiDetectorOptions
{
    [JsonPropertyName("exclude_labels")]
    public HashSet<string> ExcludeLabels { get; set; } = [];
}
public sealed class OnnxPiiDetector : IPiiDetector, IDisposable
{
    public OnnxPiiDetector(DirectoryInfo modelsDirectory, string modelName = "pii", OnnxPiiDetectorOptions? options = null)
    {
        DirectoryInfo modelDir = new(Path.Combine(modelsDirectory.FullName, modelName));
        FileInfo onnxFile = new(Path.Combine(modelDir.FullName, "model.onnx"));
        FileInfo tokenizerFile = new(Path.Combine(modelDir.FullName, "tokenizer.json"));
        FileInfo configFile = new(Path.Combine(modelDir.FullName, "config.json"));

        if (!tokenizerFile.Exists) throw new FileNotFoundException($"Tokenizer introuvable : {tokenizerFile.FullName}");
        if (!onnxFile.Exists) throw new FileNotFoundException($"Modèle ONNX introuvable : {onnxFile.FullName}");
        if (!configFile.Exists) throw new FileNotFoundException($"Fichier de configuration introuvable : {configFile.FullName}");

        _tokenizer = Tokenizer.FromFile(tokenizerFile.FullName);
        _session = new(onnxFile.FullName);
        _modelSeqLen =
            _session.InputMetadata.TryGetValue(_session.InputMetadata.Keys.First(), out var meta) &&
            meta.Dimensions is { Length: >= 2 } dims && dims[1] > 0
                ? dims[1]
                : 512;

        _labels = LoadLabels(configFile);

        var inputNames = _session.InputMetadata.Keys.ToList();
        _nameInputIds = inputNames.FirstOrDefault(n => n.Contains("input_ids", StringComparison.OrdinalIgnoreCase)) ?? inputNames[0];
        _nameAttention = inputNames.FirstOrDefault(n => n.Contains("attention_mask", StringComparison.OrdinalIgnoreCase)) ?? inputNames.ElementAtOrDefault(1) ?? inputNames[0];
        _nameTypeIds = inputNames.FirstOrDefault(n => n.Contains("token_type", StringComparison.OrdinalIgnoreCase) || n.Contains("type_ids", StringComparison.OrdinalIgnoreCase));

        _options = options ?? new();
    }

    public void Dispose() =>
        _session?.Dispose();

    public async Task<IReadOnlyList<Entity>> AnalyzeMarkdownFileAsync(FileInfo filePath)
    {
        if (!filePath.Exists) throw new FileNotFoundException($"Fichier Markdown introuvable : {filePath.FullName}");
        string markdownText = await File.ReadAllTextAsync(filePath.FullName);
        return AnalyzeText(markdownText);
    }

    public IReadOnlyList<Entity> AnalyzeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var encodings = _tokenizer.Encode(
            text,
            addSpecialTokens: true,
            includeTypeIds: true,
            includeTokens: true,
            includeOffsets: true,
            includeSpecialTokensMask: true,
            includeAttentionMask: true,
            includeWords: true
        );

        long[] allInputIds = [.. encodings.SelectMany(e => e.Ids).Select(i => (long)i)];
        long[] allAttentionMask = [.. encodings.SelectMany(e => e.AttentionMask).Select(i => (long)i)];
        long[] allTypeIds = [.. encodings.SelectMany(e => e.TypeIds).Select(i => (long)i)];
        Offsets[] allOffsets = [.. encodings.SelectMany(e => e.Offsets)];
        string[] allTokens = [.. encodings.SelectMany(e => e.Tokens)];
        uint[] allSpecialMask = [.. encodings.SelectMany(e => e.SpecialTokensMask.ToArray() ?? [])];

        int totalTokens = allInputIds.Length;
        int maxSeqLen = _modelSeqLen;

        List<Entity> allEntities = new(1024);

        int expectedNumLabels = _labels.Length;
        Span<float> logits = stackalloc float[expectedNumLabels];
        Span<double> exps = stackalloc double[expectedNumLabels];

        for (int i = 0; i < totalTokens; i += maxSeqLen)
        {
            int currentLen = Math.Min(maxSeqLen, totalTokens - i);

            long[] inputIds = allInputIds[i..(i + currentLen)];
            long[] attentionMask = allAttentionMask[i..(i + currentLen)];
            long[] typeIds = allTypeIds[i..(i + currentLen)];
            Offsets[] offsets = allOffsets[i..(i + currentLen)];
            string[] tokens = allTokens[i..(i + currentLen)];
            uint[] specialMask = allSpecialMask[i..(i + currentLen)];

            int seqLen = currentLen;
            if (seqLen < maxSeqLen)
            {
                int pad = maxSeqLen - seqLen;
                inputIds = [.. inputIds, .. new long[pad]];
                attentionMask = [.. attentionMask, .. new long[pad]];
                typeIds = [.. typeIds, .. new long[pad]];
                tokens = [.. tokens, .. Enumerable.Repeat("[PAD]", pad)];

                var padOffset = offsets.Length > 0 ? offsets[0] : new Offsets();
                offsets = [.. offsets, .. Enumerable.Repeat(padOffset, pad)];
                specialMask = [.. specialMask, .. Enumerable.Repeat(1u, pad)];
                seqLen = maxSeqLen;
            }

            var inputIdsTensor = new DenseTensor<long>(inputIds, [1, maxSeqLen]);
            var attentionMaskTensor = new DenseTensor<long>(attentionMask, [1, maxSeqLen]);
            var typeIdsTensor = new DenseTensor<long>(typeIds, [1, maxSeqLen]);

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_nameInputIds, inputIdsTensor),
                NamedOnnxValue.CreateFromTensor(_nameAttention, attentionMaskTensor),
            };
            if (_nameTypeIds is not null)
                inputs.Add(NamedOnnxValue.CreateFromTensor(_nameTypeIds, typeIdsTensor));

            using var results = _session.Run(inputs);
            var outputTensor = results.First(r => r.Name == _session.OutputMetadata.Keys.First()).AsTensor<float>();

            ReadOnlySpan<int> dims = outputTensor.Dimensions;
            int numLabels = dims[2];
            int[] preds = new int[seqLen];
            float[] conf = new float[seqLen];

            for (int t = 0; t < seqLen; t++)
            {
                int argmax = 0;
                float maxv = float.NegativeInfinity;

                for (int l = 0; l < numLabels; l++)
                {
                    float v = outputTensor[0, t, l];
                    logits[l] = v;
                    if (v > maxv) { maxv = v; argmax = l; }
                }

                float maxLogit = maxv;
                double sumExp = 0.0;
                for (int l = 0; l < numLabels; l++)
                {
                    exps[l] = Math.Exp(logits[l] - maxLogit);
                    sumExp += exps[l];
                }

                preds[t] = argmax;
                conf[t] = (float)(exps[argmax] / sumExp);
            }

            bool IsSpecial(int idx)
            {
                if (specialMask != null && idx < specialMask.Length && specialMask[idx] is 1) return true;
                if (idx < offsets.Length) return offsets[idx].Start is 0 && offsets[idx].End is 0 && tokens[idx].StartsWith('[');
                return false;
            }

            var chunkEntities = DecodeBioEntities(Encoding.UTF8.GetBytes(text), tokens, offsets, preds, conf, IsSpecial);
            allEntities.AddRange(chunkEntities);
        }

        return MergeAdjacentEntities(allEntities, Encoding.UTF8.GetBytes(text));
    }

    private List<Entity> DecodeBioEntities(ReadOnlySpan<byte> originalText, string[] tokens, Offsets[] offsets, int[] preds, float[] confidences, Func<int, bool> isSpecialToken)
    {
        List<Entity> entities = [];
        Entity? current = null;
        double currentScoreSum = 0.0;
        int currentCount = 0;

        void FinalizeCurrent(ReadOnlySpan<byte> originalText)
        {
            if (current is null) return;
            int s = current.Start;
            int e = current.End;
            NormalizeEntitySpan(originalText, ref s, ref e);
            if (e > s)
            {
                current.Start = s;
                current.End = e;
                current.Text = Encoding.UTF8.GetString(originalText[s..e]);
                current.Score = (float)(currentScoreSum / Math.Max(1, currentCount));
                entities.Add(current);
            }
            current = null;
            currentScoreSum = 0.0;
            currentCount = 0;
        }

        for (int i = 0; i < preds.Length && i < offsets.Length && i < tokens.Length; i++)
        {
            if (isSpecialToken(i)) continue;

            if (current != null && i > 0 && !isSpecialToken(i - 1))
            {
                // Si le token actuel est directement collé au token précédent, on l'absorbe dans l'entité
                if (offsets[i - 1].End == offsets[i].Start)
                {
                    current.End = Math.Max(current.End, (int)offsets[i].End);
                    currentScoreSum += confidences[i];
                    currentCount++;
                    continue;
                }
            }

            int p = preds[i];
            string label = (p >= 0 && p < _labels.Length) ? _labels[p] : "O";

            if (label is "O")
            {
                FinalizeCurrent(originalText);
                continue;
            }

            var parts = label.Split('-', 2);
            if (parts.Length != 2)
            {
                FinalizeCurrent(originalText);
                continue;
            }

            string bio = parts[0];
            string tag = parts[1];
            if (_options.ExcludeLabels.Contains(tag))
            {
                FinalizeCurrent(originalText);
                continue;
            }

            var off = offsets[i];
            if (off.Start is 0 && off.End is 0 && tokens[i].StartsWith('[')) continue;

            if (bio is "B" || current is null || current.Label != tag)
            {
                int startIndex = i;
                while (startIndex > 0 && !isSpecialToken(startIndex - 1))
                {
                    if (offsets[startIndex - 1].End == offsets[startIndex].Start)
                        startIndex--;
                    else break;
                }

                FinalizeCurrent(originalText);

                current = new Entity
                {
                    Label = tag,
                    Start = (int)offsets[startIndex].Start,
                    End = (int)off.End,
                    Score = confidences[i],
                    Text = string.Empty,
                };
                currentScoreSum = confidences[i];
                currentCount = 1;
            }
            else if (bio is "I" && current is not null && current.Label == tag)
            {
                current.End = Math.Max(current.End, (int)off.End);
                currentScoreSum += confidences[i];
                currentCount++;
            }
        }

        FinalizeCurrent(originalText);
        return entities.Where(e => e.Start >= 0 && e.End > e.Start && !string.IsNullOrWhiteSpace(e.Text) && !_options.ExcludeLabels.Contains(e.Label)).ToList();
    }

    private static List<Entity> MergeAdjacentEntities(List<Entity> entities, ReadOnlySpan<byte> originalText)
    {
        if (entities.Count <= 1) return entities;

        var merged = new List<Entity>();
        var current = entities[0];

        for (int i = 1; i < entities.Count; i++)
        {
            var next = entities[i];

            if (current.Label == next.Label)
            {
                int gapStart = current.End;
                int gapEnd = next.Start;
                bool onlySpaces = true;

                if (gapEnd >= gapStart && gapEnd <= originalText.Length)
                {
                    for (int j = gapStart; j < gapEnd; j++)
                    {
                        if (!char.IsWhiteSpace((char)originalText[j]))
                        {
                            onlySpaces = false;
                            break;
                        }
                    }
                }
                else
                {
                    onlySpaces = false;
                }

                if (onlySpaces)
                {
                    current.End = next.End;
                    int s = current.Start;
                    int e = current.End;
                    NormalizeEntitySpan(originalText, ref s, ref e);

                    current.Start = s;
                    current.End = e;
                    current.Text = Encoding.UTF8.GetString(originalText[s..e]);
                    current.Score = (current.Score + next.Score) / 2f;
                    continue;
                }
            }

            merged.Add(current);
            current = next;
        }
        merged.Add(current);
        return merged;
    }

    private static void NormalizeEntitySpan(ReadOnlySpan<byte> originalText, ref int start, ref int end)
    {
        if (start < 0 || end <= start || start >= originalText.Length) return;
        if (end > originalText.Length) end = originalText.Length;

        while (start < end && start < originalText.Length && char.IsWhiteSpace((char)originalText[start])) start++;
        while (end > start && end <= originalText.Length && char.IsWhiteSpace((char)originalText[end - 1])) end--;
        while (end > start && end <= originalText.Length && char.IsPunctuation((char)originalText[end - 1])) end--;
        while (start < end && start < originalText.Length && char.IsPunctuation((char)originalText[start])) start++;

        if (end < start) end = start;
    }

    private static string[] LoadLabels(FileInfo configFile)
    {
        string json = File.ReadAllText(configFile.FullName);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("id2label", out var id2label))
        {
            return id2label.EnumerateObject()
                .OrderBy(p => int.TryParse(p.Name, out int idx) ? idx : int.MaxValue)
                .Select(p => p.Value.GetString() ?? "O")
                .ToArray();
        }
        return [];
    }

    private readonly Tokenizer _tokenizer;
    private readonly InferenceSession _session;
    private readonly int _modelSeqLen = -1;
    private readonly string _nameInputIds;
    private readonly string _nameAttention;
    private readonly string? _nameTypeIds;
    private readonly string[] _labels;
    private readonly OnnxPiiDetectorOptions _options;
}
