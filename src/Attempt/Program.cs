using System.Text;

using Anonymizer.Extractor.PII;

using Lsquared.Anonymizer;

using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

// OnnxPiiDetector renvoie des offsets en octets UTF-8 alors que page.Text est indexé
// en caractères UTF-16 : on remappe pour retrouver les bonnes lettres à masquer.
static int[] BuildByteToCharMap(string text)
{
    var byteLen = Encoding.UTF8.GetByteCount(text);
    var map = new int[byteLen + 1];
    var bytePos = 0;
    for (var i = 0; i < text.Length;)
    {
        var charLen = char.IsSurrogatePair(text, i) ? 2 : 1;
        var byteCount = Encoding.UTF8.GetByteCount(text.AsSpan(i, charLen));
        for (var k = 0; k < byteCount; k++) map[bytePos + k] = i;
        bytePos += byteCount;
        i += charLen;
    }
    map[bytePos] = text.Length;
    return map;
}

// var path = "./samples/BDX-Développeur_TS-LILA.pdf";
var path = "./samples/CV-LionelLalande-V5.4.pdf";
var outputPath = "./samples/xxx.pdf";

using var sourcePdf = PdfDocument.Open(path);
PDFParser parser = new();
var doc = parser.Parse(sourcePdf);


// bon :
// - onnx-community/pii-ner-nemotron-ONNX : détecte first_name + last_name + nickname + date / mauvais address_city + organization / mauvais sur CV_Lionel
//
// - bardsai/eu-pii-anonimization-multilang : détecte PERSON_NAME, PERSON_ROLE_OR_TITLE, ORGANIZATION_NAME (un peu trop) / pas PROPER NAME, emails / mauvais sur CV_Lionel
//
// - 1-13-am/xlm-roberta-base-pii-finetuned : détecte NAME_STUDENT et URL_PERSONAL
// - giji2/pii_distilbert_v3 : détecte NAME_STUDENT plutôt bien
// - wjarka/eu-pii-anonimization-multilang : IDEM
// moyen :
// - narayan214/distilbert_base_pii_redact : FIRSTNAME, LASTNAME, / mauvais COUNTRY, ORGANIZATION / très mauvais sur CV_Lionel
// mauvais :
// - onnx-community/deberta-v3-base-pii-en-ONNX
// - onnx-community/piiranha-v1-detect-personal-information-ONNX
// - onnx-community/multilang-pii-ner-ONNX
// - yonigo/distilbert-base-multilingual-cased-pii
// - HikmaAI/hikmaai-distilbert-pii
// - yalen-ai/distilbert_pii_ner_yalen
// - itsgnani/pii-distilbert-cased : pas de nom, prénom, très non sur les dates
// incorrect :
// - onnx-community/gliner_multi_pii-v1
// - barflyman/xlm-roberta-pii-ner-4lang
// - mozilla-ai/tiny-pii-roBERTa-base
// - lakshyakh93/deberta_finetuned_pii





// pii-bert est potentiellement le mieux sur un CV Ippon !
using OnnxPiiDetector piiDetector = new(new DirectoryInfo("../Anonymizer/models/"), "Ozgunn/distil_bert_pii_model-fine-tuned", new()
{
    ExcludeLabels = {
        "NOM_SOCIETE", // x
        "ORG", // davlan / ar86bat
    },
});
RegexPiiDetector regexPiiDetector = new();

Dictionary<int, List<Entity>> entities = new();
foreach (var page in doc.Pages)
{
    var byteToChar = BuildByteToCharMap(page.Text);
    List<Entity> pageEntities = new(1024);
    foreach (var entity in piiDetector.AnalyzeText(page.Text))
    {
        entity.Start = byteToChar[Math.Clamp(entity.Start, 0, byteToChar.Length - 1)];
        entity.End = byteToChar[Math.Clamp(entity.End, 0, byteToChar.Length - 1)];
        pageEntities.Add(entity);
    }
    // pageEntities.AddRange(regexPiiDetector.AnalyzeText(page.Text));
    entities[page.Number] = pageEntities;

    Console.WriteLine($"Page {page.Number}: {pageEntities.Count} PII entities detected");
    foreach (var entity in pageEntities)
        Console.WriteLine($"  {entity.Label} at {entity.Start}-{entity.End}: {entity.Text}");
}
{
    using var sourcePdf2 = PdfDocument.Open(path);
    var outputPath2 = "./samples/xxx.notext.pdf";
    using var stream = File.OpenWrite(outputPath2);
    PdfTextRemover.RemoveText(sourcePdf2, stream);
}
{
    using var sourcePdf3 = PdfDocument.Open(path);
    using var stream = File.OpenWrite(outputPath);
    PdfBuilder.Rebuild(doc, sourcePdf3, entities, stream);
}

Console.WriteLine($"Anonymized PDF written to {outputPath}");
